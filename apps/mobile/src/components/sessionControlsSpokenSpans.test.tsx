// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";

// WHICH CHARACTERS THE PHONE'S COMPOSER SAYS WERE SPOKEN (source logging, owner's ruling 2026-09-05),
// through the REAL controls and the REAL dictation dialog - the same proof the Cockpit composer carries,
// on the other surface, so the two cannot drift. The composer used to send a whole-turn claim or nothing;
// it now tracks the transcript's character range as the person edits around it and sends those ranges as
// claims the Gateway verifies.

const { sendPrompt, transcribeUtterance, listSessions } = vi.hoisted(() => ({
  sendPrompt: vi.fn(async () => ({ delivering: false })),
  transcribeUtterance: vi.fn(async () => ({ text: "the dictated words", deliveryId: "utt-77" })),
  listSessions: vi.fn(async () => [{ sessionId: "sess-42", totalBufferBytes: 4321 }]),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  listSessions,
  sendEscape: vi.fn(async () => {}),
  sendInterrupt: vi.fn(async () => {}),
  uploadImage: vi.fn(async () => ""),
}));

vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend: vi.fn(async () => {}),
}));

vi.mock("@devthrottle/client-core/dictation/recorder", () => {
  class MicRecorder {
    onCaptureLive: (() => void) | null = null;
    lastRecordedMs = 1000;
    deviceLabel = "Fake Microphone";
    deviceId = "fake-mic";
    async start() {
      this.onCaptureLive?.();
    }
    async stop() {
      return new Blob(["clip"], { type: "audio/webm" });
    }
    level() {
      return 0;
    }
    msSinceLastAudio() {
      return 0;
    }
    msSinceMeterMoved() {
      return 0;
    }
    dispose() {}
  }
  return { MicRecorder, rmsLevel: () => 0 };
});
vi.mock("@devthrottle/client-core/dictation/wav", () => ({
  blobToWav16kMono: async () => ({
    wav: new Blob(["wav"]),
    decodedSeconds: 1,
    sourceBytes: 1000,
    nativeSamples: new Float32Array(0),
    nativeSampleRate: 16000,
  }),
}));
vi.mock("@devthrottle/client-core/dictation/readyCue", () => ({
  playReadyCue: () => {},
  primeCueAudio: () => {},
  releaseCueAudio: () => {},
  startThinkingCue: () => () => {},
  playYourTurnCue: () => {},
}));

import { SessionControls } from "./SessionControls";

const DICTATED = "the dictated words";

function input(): HTMLTextAreaElement {
  return screen.getByPlaceholderText(/type a message/i) as HTMLTextAreaElement;
}

function type(text: string, caret?: number) {
  const box = input();
  fireEvent.change(box, { target: { value: text } });
  box.selectionStart = caret ?? text.length;
  box.selectionEnd = box.selectionStart;
}

async function dictateInsert() {
  fireEvent.click(screen.getByRole("button", { name: "Speak" }));
  const dialog = await screen.findByRole("dialog", { name: "Dictate" });
  await within(dialog).findByText("RECORDING");
  fireEvent.click(within(dialog).getByText("Insert"));
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());
}

function lastSend(): { text: string; spans: { start: number; length: number; transcriptId?: string }[] } {
  const call = sendPrompt.mock.calls[sendPrompt.mock.calls.length - 1] as unknown as [
    string, string, boolean, undefined, string | undefined, { start: number; length: number; transcriptId?: string }[] | undefined,
  ];
  return { text: call[1], spans: call[5] ?? [] };
}

describe("the phone composer says which characters were spoken", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });
  afterEach(() => cleanup());

  it("sends the dictation's character range when the person types around it", async () => {
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    type("please ");
    await dictateInsert();
    await waitFor(() => expect(input().value).toContain(DICTATED));
    type(input().value + " now");

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));

    const { text, spans } = lastSend();
    expect(spans).toHaveLength(1);
    expect(spans[0].transcriptId).toBe("utt-77");
    expect(text.slice(spans[0].start, spans[0].start + spans[0].length)).toBe(DICTATED);
  });

  it("sends no span when the person typed the whole turn", async () => {
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    type("git status");

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));

    expect(lastSend().spans).toEqual([]);
  });

  it("forgets the range when the person edits inside the dictated words", async () => {
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    await dictateInsert();
    await waitFor(() => expect(input().value).toContain(DICTATED));
    type(DICTATED.replace("dictated", "dictateds"));

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));

    expect(lastSend().spans).toEqual([]);
  });
});
