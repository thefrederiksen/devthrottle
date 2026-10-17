// Fakes for the API-mode live stream (mission #4070, phase 3): a fake network that answers GET /sessions/{sid}/live
// with a stream the test writes frames into, a fake timer, and items worded exactly as the Gateway's ApiModeLiveRuling
// words them. No real network and no real clock.
import type { ApiModeLiveItem, ApiModeLiveReset, ApiModeSessionDisplay } from "./liveTypes";
import type { LiveFetch, LiveSchedule, LiveStreamDeps } from "./liveStream";

/** One open stream the fake network handed out: write frames into it, or end it. */
export interface FakeLiveConnection {
  url: string;
  headers: Record<string, string>;
  signal: AbortSignal | null | undefined;
  /** Write raw text, as the Gateway would write bytes. */
  write: (text: string) => void;
  /** Write one `items` frame with the given cursor. */
  items: (cursor: string, items: ApiModeLiveItem[]) => void;
  /** Write one `reset` frame, with the empty id the Gateway sends. */
  reset: (reset: ApiModeLiveReset) => void;
  /** End the stream, as a proxy or a deploy would. */
  end: () => void;
}

export interface FakeLiveNetwork {
  deps: LiveStreamDeps;
  /** Every connection opened, oldest first. */
  connections: FakeLiveConnection[];
  /** The next answers, in order: "stream" opens a stream, a refusal answers that status with that sentence. When the
   *  queue is empty every request opens a stream. */
  answers: Array<"stream" | { status: number; error: string }>;
  /** The reconnects waiting on the fake timer. */
  pendingTimers: () => number;
  /** Run every reconnect waiting on the fake timer. */
  runTimers: () => void;
}

export function fakeLiveNetwork(): FakeLiveNetwork {
  const connections: FakeLiveConnection[] = [];
  const answers: FakeLiveNetwork["answers"] = [];
  let timers: Array<{ fn: () => void; cancelled: boolean }> = [];
  const encoder = new TextEncoder();

  const fetch: LiveFetch = async (url, init) => {
    const answer = answers.shift() ?? "stream";
    if (answer !== "stream")
      return new Response(JSON.stringify({ error: answer.error }), {
        status: answer.status,
        headers: { "Content-Type": "application/json" },
      });
    let controller!: ReadableStreamDefaultController<Uint8Array>;
    let ended = false;
    const body = new ReadableStream<Uint8Array>({ start: (c) => { controller = c; } });
    init.signal?.addEventListener("abort", () => {
      if (ended) return;
      ended = true;
      controller.error(new DOMException("aborted", "AbortError"));
    });
    const write = (text: string) => {
      if (!ended) controller.enqueue(encoder.encode(text));
    };
    connections.push({
      url,
      headers: { ...(init.headers as Record<string, string>) },
      signal: init.signal,
      write,
      items: (cursor, items) => write(`id: ${cursor}\nevent: items\ndata: ${JSON.stringify(items)}\n\n`),
      reset: (reset) => write(`id: \nevent: reset\ndata: ${JSON.stringify(reset)}\n\n`),
      end: () => {
        if (ended) return;
        ended = true;
        controller.close();
      },
    });
    return new Response(body, { status: 200, headers: { "Content-Type": "text/event-stream" } });
  };

  const schedule: LiveSchedule = (fn) => {
    const timer = { fn, cancelled: false };
    timers.push(timer);
    return () => { timer.cancelled = true; };
  };

  return {
    deps: { fetch, schedule },
    connections,
    answers,
    pendingTimers: () => timers.filter((t) => !t.cancelled).length,
    runTimers: () => {
      const due = timers.filter((t) => !t.cancelled);
      timers = [];
      for (const t of due) t.fn();
    },
  };
}

// ----- items, worded as ApiModeLiveRuling words them -----

export function receiptItem(
  promptId: string, text: string, tone: string, sequence: number, turnId?: string, promptText?: string,
): ApiModeLiveItem {
  return {
    itemId: `receipt:${promptId}`, kind: "receipt", mode: "replace", text, tone, promptId,
    turnId: turnId ?? null, promptText: promptText ?? null, sequence,
  };
}

export function replyDelta(messageId: string, turnId: string, text: string, sequence: number): ApiModeLiveItem {
  return { itemId: `reply:${messageId}`, kind: "replyText", mode: "append", text, tone: "neutral", turnId, sequence };
}

export function replyFinal(messageId: string, turnId: string, text: string, sequence: number): ApiModeLiveItem {
  return { itemId: `reply:${messageId}`, kind: "replyText", mode: "replace", text, tone: "neutral", turnId, sequence };
}

export function toolCard(
  toolCallId: string, turnId: string, title: string, input: string | null, status: string, tone: string, sequence: number,
): ApiModeLiveItem {
  return { itemId: `tool:${toolCallId}`, kind: "toolCard", mode: "replace", text: title, status, detail: input, tone, turnId, sequence };
}

export function turnEndedItem(turnId: string, text: string, tone: string, sequence: number): ApiModeLiveItem {
  return { itemId: `turn:${turnId}`, kind: "turnEnded", mode: "replace", text, tone, turnId, sequence };
}

export function connectionItem(sequence: number, text: string, tone: string, detail?: string): ApiModeLiveItem {
  return { itemId: `connection:${sequence}`, kind: "connection", mode: "replace", text, tone, detail: detail ?? null, sequence };
}

/** The session verdict mid-turn, worded as ApiModeSessionFold words it. */
export function workingDisplay(turn: number): ApiModeSessionDisplay {
  return {
    kind: "working",
    tone: "green",
    connectionLabel: "API mode",
    userHasTheWheel: false,
    activityLabel: `Working - turn ${turn}`,
    working: true,
    turnNumber: turn,
    queuedCount: 0,
    queueLabel: null,
    lastTurnEnding: null,
    agentConnected: true,
    agentLabel: "Agent connected (gpt-5.1-codex-mini)",
  };
}

/** Wait for the stream reader's promises (fetch, body reads) to settle. */
export async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
  await new Promise((r) => setTimeout(r, 0));
}
