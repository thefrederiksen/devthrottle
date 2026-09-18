// @vitest-environment jsdom
// A REPLACEMENT CLIP MUST NOT PULL THE BYTES OUT FROM UNDER A LISTENER.
//
// A narration arrives in two stages: the judge's short words are synthesised at once so there is something to hear,
// and the fuller narration call replaces the clip when it answers. Both describe the SAME turn, so the replacement is
// correct - but ensureClip freed the old object URL with a bare revokeObjectURL, including when an <audio> element
// was mid-sentence through it. Revoking a blob URL a live element is streaming from pulls its bytes away: playback
// dies partway and the screen then hands over the new, longer clip, which reads as the voice jumping or restarting
// by itself.
//
// Issue #1322's rule - never pull the rug on a listener - is exactly this. The revoke waits for the element.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../api/client", () => ({
  fetchWingmanVoiceAudio: vi.fn(async () => new ArrayBuffer(8)),
  listSessions: vi.fn(async () => []),
}));

const api = await import("../api/client");
const clips = await import("./clips");

const SID = "3f0b8f52-0000-4000-8000-000000000009";
const FIRST = "2026-09-18T09:00:00Z";   // the judge's short words
const SECOND = "2026-09-18T09:00:08Z";  // the fuller narration, eight seconds later, SAME turn

let revoked: string[] = [];
let created = 0;
let playing: FakeAudio | null = null;

class FakeAudio {
  paused = true;
  private readonly handlers = new Map<string, (() => void)[]>();
  constructor(public readonly src: string) { playing = this; }
  addEventListener(name: string, fn: () => void) {
    const list = this.handlers.get(name) ?? [];
    list.push(fn);
    this.handlers.set(name, list);
  }
  play() { this.paused = false; return Promise.resolve(); }
  pause() { this.paused = true; this.fire("pause"); }
  /** The clip reached its end on its own - the listener heard all of it. */
  finish() { this.paused = true; this.fire("ended"); }
  private fire(name: string) { for (const fn of this.handlers.get(name) ?? []) fn(); }
}

beforeEach(() => {
  revoked = [];
  created = 0;
  playing = null;
  vi.stubGlobal("URL", {
    createObjectURL: () => `blob:clip-${++created}`,
    revokeObjectURL: (u: string) => void revoked.push(u),
  });
  vi.stubGlobal("Audio", FakeAudio as unknown as typeof Audio);
  vi.stubGlobal("caches", undefined);
  (api.fetchWingmanVoiceAudio as unknown as ReturnType<typeof vi.fn>).mockClear();
});

afterEach(() => {
  clips.stopPlayback();
  vi.unstubAllGlobals();
});

describe("a clip that is replaced while it is being listened to", () => {
  it("keeps its bytes until the listener has finished with them", async () => {
    // REVERT PROOF: put `URL.revokeObjectURL(current.url)` back in ensureClip and this goes red - the URL the
    // element is streaming from is revoked while it is still playing.
    await clips.ensureClip(SID, FIRST);
    const first = clips.getClipState(SID).url;
    expect(first).not.toBeNull();

    expect(clips.playClip(SID)).toBe(true);
    expect(playing?.paused).toBe(false);

    // The narration call answers: a new clip for the same turn supersedes the one being played.
    await clips.ensureClip(SID, SECOND);
    expect(clips.getClipState(SID).generatedAt).toBe(SECOND);

    // THE POINT. The listener is still mid-sentence, so the bytes they are hearing are still theirs.
    expect(revoked).not.toContain(first);

    // And once the clip has played out, the superseded URL is freed - this does not leak.
    playing?.finish();
    expect(revoked).toContain(first);
  });

  it("frees a superseded clip at once when nobody is listening", async () => {
    // THE NEGATIVE CONTROL. Without it the change above could simply never revoke anything and the first test would
    // still pass. With no element streaming a URL, a superseded clip is freed immediately, as it always was.
    await clips.ensureClip(SID, FIRST);
    const first = clips.getClipState(SID).url;

    await clips.ensureClip(SID, SECOND);

    expect(revoked).toContain(first);
  });
});
