// @vitest-environment jsdom
//
// REPORT ANSWERS BY VOICE, the host's half: the protocol messages, the frame host passing them, the dictation class
// recording and transcribing with a fake recorder and a fake transcriber, and the controller joining the two. The
// page's half is in devReportNotes.test.ts.

import { beforeEach, describe, expect, it } from "vitest";
import { DevReportController, type DevReportApi } from "./controller";
import type { DevReportDetail } from "./devReportsClient";
import {
  ANOTHER_BOX_RECORDING,
  DevReportDictation,
  NOTHING_HEARD,
  type DictationRecorder,
  type DictationTranscript,
} from "./dictationHost";
import { DevReportFrameHost, type HostPort } from "./frameHost";
import {
  DEV_REPORT_CHANNEL,
  emptyPageState,
  isValidItem,
  MAX_DICTATION_FIELD_ID,
  parsePortMessage,
  type DevReportDictationUpdate,
} from "./protocol";
import { DevReportStateStore, type StateStorage } from "./stateStore";
import { APP_DEV_REPORT_THEME } from "./theme";

function envelope(type: string, payload: unknown) {
  return { channel: DEV_REPORT_CHANNEL, version: 1, type, payload };
}

class FakePort implements HostPort {
  sent: Array<{ type: string; payload: Record<string, unknown> }> = [];
  onmessage: ((event: MessageEvent) => void) | null = null;
  postMessage(message: unknown): void {
    this.sent.push(message as { type: string; payload: Record<string, unknown> });
  }
  close(): void {}
  emit(type: string, payload: unknown): void {
    this.onmessage?.({ data: envelope(type, payload) } as MessageEvent);
  }
  ofType(type: string) {
    return this.sent.filter((m) => m.type === type).map((m) => m.payload);
  }
}

/** A recorder that does exactly what the test tells it, and remembers what was done to it. */
class FakeRecorder implements DictationRecorder {
  onCaptureLive: (() => void) | null = null;
  lastRecordedMs = 4200;
  level = 0.4;
  disposed = false;
  stopped = false;
  start(): Promise<void> {
    return Promise.resolve();
  }
  async stop(): Promise<Blob> {
    this.stopped = true;
    return new Blob(["audio"]);
  }
  displayLevel(): number {
    return this.level;
  }
  dispose(): void {
    this.disposed = true;
  }
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

interface Rig {
  dictation: DevReportDictation;
  updates: DevReportDictationUpdate[];
  recorders: FakeRecorder[];
  transcribed: Array<{ bytes: number; recordedMs: number }>;
  answer: () => Promise<DictationTranscript>;
  clock: { now: number };
  tick: () => void;
  /** When set, the microphone refuses to open with this error. */
  startError: Error | null;
}

function rig(): Rig {
  const r = {
    updates: [] as DevReportDictationUpdate[],
    recorders: [] as FakeRecorder[],
    transcribed: [] as Array<{ bytes: number; recordedMs: number }>,
    answer: async () => ({ text: "Email me after 3 days", warning: null }) as DictationTranscript,
    clock: { now: 1000 },
    tick: () => {},
    startError: null,
  } as Rig;
  r.dictation = new DevReportDictation({
    createRecorder: () => {
      const recorder = new FakeRecorder();
      r.recorders.push(recorder);
      return recorder;
    },
    startRecorder: (recorder) => (r.startError ? Promise.reject(r.startError) : recorder.start()),
    transcribe: async (captured, recordedMs) => {
      r.transcribed.push({ bytes: captured.size, recordedMs });
      return r.answer();
    },
    push: (update) => r.updates.push(update),
    now: () => r.clock.now,
    setInterval: (fn) => {
      r.tick = fn;
      return 1;
    },
    clearInterval: () => {
      r.tick = () => {};
    },
  });
  return r;
}

describe("the protocol's dictation messages", () => {
  it("reads a start and a stop that name a box, and nothing else", () => {
    expect(parsePortMessage(envelope("dictation-start", { fieldId: "q0:comment" }))).toEqual({ type: "dictation-start", fieldId: "q0:comment" });
    expect(parsePortMessage(envelope("dictation-stop", { fieldId: "note" }))).toEqual({ type: "dictation-stop", fieldId: "note" });
    expect(parsePortMessage(envelope("dictation-start", { fieldId: "" }))).toBeNull();
    expect(parsePortMessage(envelope("dictation-start", { fieldId: 3 }))).toBeNull();
    expect(parsePortMessage(envelope("dictation-start", { fieldId: "x".repeat(MAX_DICTATION_FIELD_ID + 1) }))).toBeNull();
  });

  it("accepts an own answer only with words, and ownAnswer only on an own answer", () => {
    const base = { id: "a1", kind: "answer", questionId: "q", question: "Q", optionLabel: "Something else", comment: "" };
    expect(isValidItem({ ...base, optionValue: "own-answer", ownAnswer: "words" })).toBe(true);
    expect(isValidItem({ ...base, optionValue: "own-answer" })).toBe(false);
    expect(isValidItem({ ...base, optionValue: "own-answer", ownAnswer: " " })).toBe(false);
    expect(isValidItem({ ...base, optionValue: "yes", ownAnswer: "words" })).toBe(false);
  });
});

describe("the frame host and dictation", () => {
  function host(onDictation?: (request: { action: "start" | "stop"; fieldId: string }) => void) {
    const refused: string[] = [];
    const h = new DevReportFrameHost({
      container: document.createElement("div"),
      title: "Dev report",
      window,
      script: "",
      theme: APP_DEV_REPORT_THEME,
      random: () => "abcd1234",
      restoreState: () => emptyPageState(),
      onStateChanged: () => {},
      onSend: () => {},
      onDictation,
      onRefused: (why) => refused.push(why),
    });
    const frameWindow = { name: "frame" };
    Object.defineProperty(h.frame, "contentWindow", { value: frameWindow });
    h.load("<p>r</p>");
    h.handleFrameLoad();
    const port = new FakePort();
    h.handleWindowMessage({ source: frameWindow, data: { ...envelope("ready", { questionIds: [] }), token: "abcd1234" }, ports: [port] });
    return { h, port, refused };
  }

  it("tells the page in restore whether it records, so a host without dictation draws no microphone", () => {
    expect(host(() => {}).port.ofType("restore")[0].dictation).toBe(true);
    expect(host().port.ofType("restore")[0].dictation).toBe(false);
  });

  it("passes a start and a stop to the app, and pushes the app's updates back over the port", () => {
    const requests: Array<{ action: string; fieldId: string }> = [];
    const { h, port } = host((request) => requests.push(request));
    port.emit("dictation-start", { fieldId: "q0:own" });
    port.emit("dictation-stop", { fieldId: "q0:own" });
    expect(requests).toEqual([
      { action: "start", fieldId: "q0:own" },
      { action: "stop", fieldId: "q0:own" },
    ]);
    expect(h.pushDictation({ fieldId: "q0:own", phase: "done", text: "words" })).toBe(true);
    expect(port.ofType("dictation")).toEqual([{ fieldId: "q0:own", phase: "done", text: "words" }]);
  });

  it("refuses a dictation request when the app offers none", () => {
    const { port, refused } = host();
    port.emit("dictation-start", { fieldId: "q0:own" });
    expect(refused).toEqual(["the page asked for dictation, and this host offers none"]);
  });
});

describe("DevReportDictation", () => {
  let r: Rig;
  beforeEach(() => {
    r = rig();
  });

  it("says starting, then recording with the seconds and the level once audio is live, then hands back the words verbatim", async () => {
    await r.dictation.start("q0:comment");
    expect(r.updates).toEqual([{ fieldId: "q0:comment", phase: "starting" }]);

    r.recorders[0].onCaptureLive!();
    r.clock.now += 7400;
    r.tick();
    expect(r.updates.slice(1)).toEqual([
      { fieldId: "q0:comment", phase: "recording", seconds: 0, level: 0.4 },
      { fieldId: "q0:comment", phase: "recording", seconds: 7, level: 0.4 },
    ]);

    r.answer = async () => ({ text: "  then said,  this ", warning: null });
    await r.dictation.stop("q0:comment");

    expect(r.updates.slice(3)).toEqual([
      { fieldId: "q0:comment", phase: "transcribing" },
      // Exactly what the Gateway returned - spaces and all. Nothing rewords or trims the words.
      { fieldId: "q0:comment", phase: "done", text: "  then said,  this " },
    ]);
    expect(r.transcribed).toEqual([{ bytes: 5, recordedMs: 4200 }]);
    expect(r.recorders[0].disposed).toBe(true);
    expect(r.dictation.activeFieldId).toBeNull();
  });

  it("records one box at a time: a second start is refused for that box, and the first carries on", async () => {
    await r.dictation.start("q0:comment");
    await r.dictation.start("note");

    expect(r.updates).toEqual([
      { fieldId: "q0:comment", phase: "starting" },
      { fieldId: "note", phase: "failed", reason: ANOTHER_BOX_RECORDING },
    ]);
    expect(r.recorders).toHaveLength(1);
    expect(r.dictation.activeFieldId).toBe("q0:comment");
  });

  it("says why when the microphone does not open, and lets it go", async () => {
    r.startError = new Error("The microphone did not open within 8 seconds.");
    await r.dictation.start("q0:comment");
    expect(r.updates[1]).toEqual({ fieldId: "q0:comment", phase: "failed", reason: "The microphone did not open within 8 seconds." });
    expect(r.recorders[0].disposed).toBe(true);
    expect(r.dictation.activeFieldId).toBeNull();
  });

  it("says why when the transcription fails, and adds nothing", async () => {
    await r.dictation.start("q0:own");
    r.recorders[0].onCaptureLive!();
    r.answer = async () => {
      throw new Error("The transcription service is busy.");
    };
    await r.dictation.stop("q0:own");
    expect(r.updates[r.updates.length - 1]).toEqual({ fieldId: "q0:own", phase: "failed", reason: "The transcription service is busy." });
    expect(r.updates.some((u) => u.phase === "done")).toBe(false);
  });

  it("says nothing was heard rather than adding an empty string", async () => {
    await r.dictation.start("note");
    r.recorders[0].onCaptureLive!();
    r.answer = async () => ({ text: "   ", warning: null });
    await r.dictation.stop("note");
    expect(r.updates[r.updates.length - 1]).toEqual({ fieldId: "note", phase: "failed", reason: NOTHING_HEARD });
  });

  it("passes a dropped-audio warning along with the words", async () => {
    await r.dictation.start("note");
    r.recorders[0].onCaptureLive!();
    r.answer = async () => ({ text: "words", warning: "About 2 seconds of your audio was not captured." });
    await r.dictation.stop("note");
    expect(r.updates[r.updates.length - 1]).toEqual({
      fieldId: "note",
      phase: "done",
      text: "words",
      reason: "About 2 seconds of your audio was not captured.",
    });
  });

  it("ignores a stop for a box that is not recording", async () => {
    await r.dictation.stop("q0:comment");
    expect(r.updates).toEqual([]);
  });

  it("lets the microphone go on cancel, and drops words that arrive for a page that went away", async () => {
    await r.dictation.start("q0:comment");
    r.recorders[0].onCaptureLive!();
    let release: (t: DictationTranscript) => void = () => {};
    r.answer = () => new Promise((resolve) => (release = resolve));
    const stopping = r.dictation.stop("q0:comment");
    await flush();

    r.dictation.cancel();
    release({ text: "too late", warning: null });
    await stopping;

    expect(r.recorders[0].disposed).toBe(true);
    expect(r.updates.some((u) => u.phase === "done")).toBe(false);
    expect(r.dictation.activeFieldId).toBeNull();
  });
});

describe("the controller wires the page's microphone to the dictation", () => {
  class MemoryStorage implements StateStorage {
    private readonly map = new Map<string, string>();
    get length() {
      return this.map.size;
    }
    key(i: number) {
      return Array.from(this.map.keys())[i] ?? null;
    }
    getItem(k: string) {
      return this.map.get(k) ?? null;
    }
    setItem(k: string, v: string) {
      this.map.set(k, v);
    }
    removeItem(k: string) {
      this.map.delete(k);
    }
  }

  const detail: DevReportDetail = {
    report: {
      id: "r1",
      sessionId: "s1",
      key: "C:/r.html",
      title: "A report",
      status: "waiting-on-you",
      version: 1,
      publishedAtUtc: "2026-11-07T10:00:00Z",
      updatedAtUtc: "2026-11-07T10:00:00Z",
      sessionEnded: false,
      openItems: 0,
    },
    items: [],
    replies: [],
  } as unknown as DevReportDetail;

  const api: DevReportApi = {
    getDetail: async () => detail,
    getHtml: async (_id, version) => ({ html: "<p>r</p>", version }),
    send: async () => [],
  };

  it("records for the page and puts the words back over the port; a page that goes away lets the microphone go", async () => {
    const recorders: FakeRecorder[] = [];
    const controller = new DevReportController({
      reportId: "r1",
      api,
      store: new DevReportStateStore(new MemoryStorage()),
      container: document.createElement("div"),
      window,
      script: "",
      theme: APP_DEV_REPORT_THEME,
      random: () => "abcd1234",
      dictation: {
        createRecorder: () => {
          const recorder = new FakeRecorder();
          recorders.push(recorder);
          return recorder;
        },
        startRecorder: (recorder) => recorder.start(),
        transcribe: async () => ({ text: "Email me after 3 days", warning: null }),
      },
    });
    const frameWindow = { name: "frame" };
    Object.defineProperty(controller.host.frame, "contentWindow", { value: frameWindow });
    await controller.refresh();
    controller.host.handleFrameLoad();
    const port = new FakePort();
    controller.host.handleWindowMessage({ source: frameWindow, data: { ...envelope("ready", { questionIds: [] }), token: "abcd1234" }, ports: [port] });

    expect(port.ofType("restore")[0].dictation).toBe(true);
    port.emit("dictation-start", { fieldId: "q0:own" });
    await flush();
    recorders[0].onCaptureLive!();
    port.emit("dictation-stop", { fieldId: "q0:own" });
    await flush();
    await flush();
    expect(port.ofType("dictation").map((u) => u.phase)).toEqual(["starting", "recording", "transcribing", "done"]);
    expect(port.ofType("dictation")[3]).toEqual({ fieldId: "q0:own", phase: "done", text: "Email me after 3 days" });

    // A second recording, then the page reloads (a new version): the microphone is let go at once.
    port.emit("dictation-start", { fieldId: "note" });
    await flush();
    controller.host.load("<p>r2</p>");
    expect(recorders[1].disposed).toBe(true);
    controller.dispose();
  });
});
