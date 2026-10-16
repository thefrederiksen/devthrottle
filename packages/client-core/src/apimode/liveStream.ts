// THE READER OF ONE API-MODE SESSION'S LIVE STREAM, GET /sessions/{sid}/live (mission #4070, phase 3).
//
// The Gateway's contract for this route is in src/CcDirector.Gateway/Api/SessionLiveEndpoint.cs. What this reader does
// with it:
//   - It opens the stream with NO cursor, so the Gateway replays everything it holds for the session from the start,
//     then follows live. (`after=0` is not a cursor the Gateway hands out; it answers 400.)
//   - Each `items` frame is a JSON array of ruled items, handed on whole and in order. Its `id` is the frame's cursor.
//   - A `reset` frame is handed on, and its empty `id` clears the cursor, so a reconnect after it does not resend a
//     cursor the Gateway could not honour.
//   - The stream can end at any time (a proxy, a deploy, the network). That is not a gap: the reader reconnects with
//     the cursor of the last frame it handed on, in the Last-Event-ID header, and the Gateway either continues from
//     it or answers with a reset.
//   - A refusal (the session is in terminal mode, its Director is away, another account's session) is the Gateway's
//     own sentence, handed on verbatim, and the reader tries again after a pause.
//
// Why fetch and not EventSource: EventSource cannot carry the Authorization header, and it never shows the body of a
// refusal, so the reader could only say "it failed" where the Gateway said why. The parsing follows the Server-Sent
// Events rules: fields `id`, `event`, `data` (several data lines join with a line feed), comment lines start with a
// colon (the Gateway's keep-alive), and a blank line ends a frame.
//
// The network and the clock are injected, so a test drives the reader with a fake stream and a fake timer.
import { authHeaders, gatewayErrorMessage, gatewayFetch, GatewayError } from "../api/client";
import { describeAndReport } from "../errors/reportClientError";
import type { ApiModeLiveItem, ApiModeLiveReset } from "./liveTypes";

const SURFACE = "api-mode-live-stream";

/** How long the reader waits before opening the stream again after it ended or was refused. */
export const LIVE_RECONNECT_DELAY_MS = 3000;

/** What the reader's own connection to the stream is doing. This is about this device's connection, not the
 *  session: the session's state is the Gateway's verdict on the session row. */
export interface LiveStreamState {
  /** True while a stream is open and being read. */
  open: boolean;
  /** A sentence to show while the stream is not open, or null when there is nothing to say. */
  message: string | null;
}

export interface LiveStreamHandlers {
  /** One frame's items, in the order the Gateway sent them. */
  onItems: (items: ApiModeLiveItem[]) => void;
  /** The Gateway's reset: what the client holds must not be continued. */
  onReset: (reset: ApiModeLiveReset) => void;
  /** The reader's connection changed. */
  onState: (state: LiveStreamState) => void;
  /** Something the contract does not allow arrived (a frame that is not JSON, a frame of an unknown kind). Already
   *  reported by the reader; this is the sentence to show. */
  onProblem: (sentence: string) => void;
}

export type LiveFetch = (url: string, init: RequestInit) => Promise<Response>;

/** Run `fn` after `ms`; returns a function that cancels it. */
export type LiveSchedule = (fn: () => void, ms: number) => () => void;

export interface LiveStreamDeps {
  fetch?: LiveFetch;
  schedule?: LiveSchedule;
}

const defaultSchedule: LiveSchedule = (fn, ms) => {
  const handle = setTimeout(fn, ms);
  return () => clearTimeout(handle);
};

export const LIVE_CONNECTING_MESSAGE = "Connecting to the live conversation...";

function retryWords(sentence: string): string {
  return `${sentence} Trying again in ${Math.round(LIVE_RECONNECT_DELAY_MS / 1000)} seconds.`;
}

export const LIVE_ENDED_MESSAGE = "The live conversation stream ended.";

/** One parsed Server-Sent Events frame. `id` is undefined when the frame carried no id field. */
export interface SseFrame {
  id: string | undefined;
  event: string;
  data: string;
}

/**
 * A Server-Sent Events parser that is fed text as it arrives and returns the frames completed by it. Lines may end in
 * a line feed, a carriage return and line feed, or a lone carriage return.
 */
export class SseParser {
  private _buffer = "";
  private _id: string | undefined;
  private _event = "";
  private _data: string[] = [];
  private _hasData = false;

  push(text: string): SseFrame[] {
    this._buffer += text;
    const frames: SseFrame[] = [];
    for (;;) {
      const match = /\r\n|\n|\r/.exec(this._buffer);
      if (match === null) break;
      // A lone carriage return at the very end may be the first half of a CRLF still in flight.
      if (match[0] === "\r" && match.index === this._buffer.length - 1) break;
      const line = this._buffer.slice(0, match.index);
      this._buffer = this._buffer.slice(match.index + match[0].length);
      const frame = this.line(line);
      if (frame !== null) frames.push(frame);
    }
    return frames;
  }

  private line(line: string): SseFrame | null {
    if (line === "") {
      const frame = this._hasData || this._event !== "" || this._id !== undefined
        ? { id: this._id, event: this._event === "" ? "message" : this._event, data: this._data.join("\n") }
        : null;
      this._id = undefined;
      this._event = "";
      this._data = [];
      this._hasData = false;
      return frame;
    }
    if (line.startsWith(":")) return null;
    const colon = line.indexOf(":");
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "id") this._id = value;
    else if (field === "event") this._event = value;
    else if (field === "data") {
      this._data.push(value);
      this._hasData = true;
    }
    return null;
  }
}

/** The route for one session's live stream. */
export function liveStreamUrl(sessionId: string): string {
  return `/sessions/${encodeURIComponent(sessionId)}/live`;
}

/**
 * Open the session's live stream and keep it open until the returned function is called. Never throws: everything
 * that happens is reported through `handlers`.
 */
export function openLiveStream(sessionId: string, handlers: LiveStreamHandlers, deps: LiveStreamDeps = {}): () => void {
  const fetchFn: LiveFetch = deps.fetch ?? ((url, init) => gatewayFetch(url, init));
  const schedule = deps.schedule ?? defaultSchedule;
  let closed = false;
  let controller: AbortController | null = null;
  let cancelRetry: (() => void) | null = null;
  // The cursor of the last frame handed on; empty means none (never sent, or cleared by a reset).
  let cursor = "";
  // A break in the contract is reported here, where it is found, and its sentence handed on to be shown.
  const reportBrokenFrame = (err: unknown) => handlers.onProblem(describeAndReport(SURFACE, "read the live conversation", err, { sessionId }));

  const retryLater = (sentence: string) => {
    if (closed) return;
    handlers.onState({ open: false, message: retryWords(sentence) });
    cancelRetry = schedule(() => {
      cancelRetry = null;
      void connect();
    }, LIVE_RECONNECT_DELAY_MS);
  };

  const connect = async () => {
    if (closed) return;
    handlers.onState({ open: false, message: LIVE_CONNECTING_MESSAGE });
    controller = new AbortController();
    const headers: Record<string, string> = { Accept: "text/event-stream", ...(authHeaders() as Record<string, string>) };
    if (cursor !== "") headers["Last-Event-ID"] = cursor;
    let res: Response;
    try {
      res = await fetchFn(liveStreamUrl(sessionId), { method: "GET", headers, signal: controller.signal });
    } catch (err) {
      if (closed) return;
      retryLater(gatewayErrorMessage(err));
      return;
    }
    if (closed) return;
    if (!res.ok) {
      // The Gateway's own sentence, verbatim (a terminal-mode session, a Director that is away, an unknown id).
      const refusal = await GatewayError.from(res, "open the live conversation");
      retryLater(refusal.serverReason ?? refusal.message);
      return;
    }
    if (res.body === null) {
      retryLater(LIVE_ENDED_MESSAGE);
      return;
    }
    handlers.onState({ open: true, message: null });
    const parser = new SseParser();
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (closed) return;
        if (done) break;
        for (const frame of parser.push(decoder.decode(value, { stream: true }))) deliver(frame);
      }
    } catch (err) {
      if (closed) return;
      retryLater(gatewayErrorMessage(err));
      return;
    }
    retryLater(LIVE_ENDED_MESSAGE);
  };

  const deliver = (frame: SseFrame) => {
    if (frame.event === "items") {
      let items: ApiModeLiveItem[];
      try {
        items = JSON.parse(frame.data) as ApiModeLiveItem[];
      } catch (err) {
        reportBrokenFrame(err);
        return;
      }
      handlers.onItems(items);
      // The cursor moves only once the frame's items were handed on.
      if (frame.id !== undefined) cursor = frame.id;
      return;
    }
    if (frame.event === "reset") {
      let reset: ApiModeLiveReset;
      try {
        reset = JSON.parse(frame.data) as ApiModeLiveReset;
      } catch (err) {
        reportBrokenFrame(err);
        return;
      }
      cursor = frame.id ?? "";
      handlers.onReset(reset);
      return;
    }
    // The contract sends only items and reset. Anything else is reported, and moves nothing: the cursor stays on the
    // last frame actually shown, so nothing is skipped silently.
    reportBrokenFrame(new Error(`The live stream sent a frame of a kind this app does not know ("${frame.event}").`));
  };

  void connect();

  return () => {
    closed = true;
    cancelRetry?.();
    cancelRetry = null;
    controller?.abort();
  };
}
