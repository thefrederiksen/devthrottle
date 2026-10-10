// The wingman menu guard, at the surface the person actually touches (issue #2193).
//
// The property under test is not "a flag is set" - it is that a spoken reply aimed at a session sitting on
// a menu NEVER starts its delivery, and that the person is told why. Both halves matter. Before this guard
// existed the reply was typed into the picker and the Enter that rides every voice send confirmed whatever
// option happened to be highlighted, so the failure mode was not "nothing happened" but "something you did
// not choose happened, silently".
//
// The audio Send is the one worth pinning here rather than in the Gateway: it is fire-and-forget, so if the
// check were placed wrongly the recording would either be delivered into the menu anyway or dropped without
// a word. The text Send's refusal is enforced by the Gateway (proven in WingmanMenuGuardProofTests) - what
// is checked here is that the hook surfaces it instead of pretending the reply landed.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";

vi.mock("../api/client", () => ({
  GatewayError: class GatewayError extends Error {
    status: number;
    constructor(status: number, message: string) { super(message); this.status = status; }
  },
  getWaitingScreen: vi.fn(),
  sendVoicePrompt: vi.fn(),
  getWingmanVoice: vi.fn(async () => null),
  fetchWingmanVoiceAudio: vi.fn(async () => new ArrayBuffer(0)),
  listSessions: vi.fn(async () => []),
  markVoiceAndExplain: vi.fn(async () => ({})),
  setVoiceMode: vi.fn(async () => {}),
  stopWingmanVoice: vi.fn(async () => {}),
}));

// The show-and-report boundary (issue #3675): returns the error's own text, as the screen showed before.
vi.mock("../errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  describeReadAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  reportShownError: (_surface: string, _action: string, message: string) => message,
  reportShownErrorWhenNew: (_memory: unknown, _surface: string, _action: string, message: string) => message,
}));

vi.mock("../dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend: vi.fn(),
}));

const api = await import("../api/client");
const bg = await import("../dictation/backgroundSend");
const { useVoiceMode } = await import("./useVoiceMode");

const waitingScreenMock = api.getWaitingScreen as unknown as ReturnType<typeof vi.fn>;
const voicePromptMock = api.sendVoicePrompt as unknown as ReturnType<typeof vi.fn>;
const deliverMock = bg.backgroundTranscribeAndSend as unknown as ReturnType<typeof vi.fn>;

const SID = "6f0b8f52-0000-4000-8000-000000000001";

type View = ReturnType<typeof useVoiceMode>;

function Probe({ onReady }: { onReady: (v: View) => void }) {
  const v = useVoiceMode(SID);
  onReady(v);
  return <span data-testid="menu-blocked">{v.menuBlocked ?? ""}</span>;
}

/** Render the hook and hand back its latest view object. */
function mount(): { view: () => View } {
  let latest: View | null = null;
  render(<Probe onReady={(v) => { latest = v; }} />);
  return { view: () => latest as unknown as View };
}

const captured = { blob: new Blob(["x"]), recordedMs: 1200 } as never;

beforeEach(() => {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
  });
  // The refusal is SPOKEN as well as shown - voice mode is hands-free, so a screen-only notice is no
  // notice at all. Stubbed so the assertion can see it was said.
  vi.stubGlobal("speechSynthesis", { cancel: vi.fn(), speak: vi.fn() });
  vi.stubGlobal("SpeechSynthesisUtterance", class { constructor(public text: string) {} });
  waitingScreenMock.mockReset();
  voicePromptMock.mockReset();
  deliverMock.mockReset();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the wingman menu guard on a voice reply", () => {
  it("does not start the audio delivery when a menu owns the screen, and says so out loud", async () => {
    waitingScreenMock.mockResolvedValue({
      kind: "menu",
      canType: false,
      spoken: "This session is waiting on a menu.",
      // The language those words are in, sent beside them by the Gateway (issue #1031). The hook builds a
      // SpokenUtterance from the pair and cannot build one without this.
      spokenLanguage: "en",
      message: "Open it and pick an option.",
    });

    const { view } = mount();
    await act(async () => { view().onRespondSendAudio(captured); });

    // THE INVARIANT: the recording never entered the delivery pipeline, so nothing reaches the picker.
    expect(deliverMock).not.toHaveBeenCalled();
    expect(view().menuBlocked).toBe("Open it and pick an option.");
    const synth = (globalThis as unknown as { speechSynthesis: { speak: ReturnType<typeof vi.fn> } }).speechSynthesis;
    expect(synth.speak).toHaveBeenCalled();
  });

  it("delivers the audio normally on an ordinary composer", async () => {
    waitingScreenMock.mockResolvedValue({ kind: "text", canType: true, spoken: "", message: "" });

    const { view } = mount();
    await act(async () => { view().onRespondSendAudio(captured); });

    expect(deliverMock).toHaveBeenCalledTimes(1);
    expect(view().menuBlocked).toBeNull();
  });

  it("still delivers when the screen cannot be read at all", async () => {
    // Phase 1 refuses ONLY on a recognized menu. A screen read that fails outright is uncertainty, not a
    // menu, and swallowing the recording there would be a far worse regression than the gap it closes.
    waitingScreenMock.mockRejectedValue(new Error("gateway unreachable"));

    const { view } = mount();
    await act(async () => { view().onRespondSendAudio(captured); });

    expect(deliverMock).toHaveBeenCalledTimes(1);
    expect(view().menuBlocked).toBeNull();
  });

  it("surfaces the Gateway's refusal of a typed reply instead of reporting it as sent", async () => {
    voicePromptMock.mockResolvedValue({
      sent: false,
      blockedByMenu: true,
      spoken: "This session is waiting on a menu.",
      // The language those words are in, sent beside them by the Gateway (issue #1031). The hook builds a
      // SpokenUtterance from the pair and cannot build one without this.
      spokenLanguage: "en",
      message: "Open it and pick an option.",
    });

    const { view } = mount();
    let sent: boolean | undefined;
    await act(async () => { sent = await view().onRespondSend("yes go ahead"); });

    // Reported as NOT sent: a caller that navigates away on success must stay put, and the notice must be
    // on a screen the person is still looking at.
    expect(sent).toBe(false);
    expect(view().menuBlocked).toBe("Open it and pick an option.");
  });

  it("clears the notice when Respond is opened again", async () => {
    voicePromptMock.mockResolvedValue({
      sent: false, blockedByMenu: true, spoken: "spoken", spokenLanguage: "en",
      message: "Open it and pick an option.",
    });

    const { view } = mount();
    await act(async () => { await view().onRespondSend("yes"); });
    expect(view().menuBlocked).toBe("Open it and pick an option.");

    // They have read it - and may well have gone and answered the menu - so a stale notice would now be
    // contradicting a screen that has moved on.
    await act(async () => { view().setResponding(true); });
    expect(view().menuBlocked).toBeNull();
  });
});

// ---- A REFUSAL TO SPEAK IS NOT PERMISSION TO SEND (audit 4, finding F2) ----------------------------------
//
// The screen read and the spoken refusal used to share one broad try block, so when the sink refused an
// utterance - which it does, correctly, for a language this build does not know - the catch read that as a
// FAILED SCREEN READ, cleared the menu notice, and delivered the recording anyway. Measured on the real hook:
// nothing spoken, one send, no notice.
//
// That is the guard inverted, not a missing announcement. This path carries a trailing Enter, so a delivery into
// a chooser can activate whatever option was highlighted - a selection nobody made and nobody is told about. So
// the property under test is about the RECORDING, not about the speech.
describe("a menu that names a language we cannot speak", () => {
  it("keeps the menu blocked and sends NOTHING when the refusal cannot be spoken", async () => {
    vi.stubGlobal("speechSynthesis", { cancel: vi.fn(), speak: vi.fn(), getVoices: () => [] });
    vi.stubGlobal("SpeechSynthesisUtterance", class { lang = ""; voice: unknown = null; constructor(public text: string) {} });
    // A healthy 200 from the Gateway - a real menu - naming a language this build does not know.
    waitingScreenMock.mockResolvedValue({
      kind: "menu",
      canType: false,
      spoken: "Cette session attend une reponse dans un menu.",
      spokenLanguage: "not-a-language",
      message: "Open it and pick an option.",
    });

    const { view } = mount();
    await act(async () => { view().onRespondSendAudio(captured); });

    // THE INVARIANT, and it is a safety one: the recording never entered the delivery pipeline.
    expect(deliverMock).not.toHaveBeenCalled();
    // And the notice stays up - the guard's real output is the on-screen refusal, which must survive a speech
    // failure rather than be undone by it.
    expect(view().menuBlocked).toBe("Open it and pick an option.");
  });

  // The same refusal on the typed path already behaved correctly, and it stays that way: reported as not sent.
  it("still reports a typed reply as not sent when the refusal cannot be spoken", async () => {
    vi.stubGlobal("speechSynthesis", { cancel: vi.fn(), speak: vi.fn(), getVoices: () => [] });
    vi.stubGlobal("SpeechSynthesisUtterance", class { lang = ""; voice: unknown = null; constructor(public text: string) {} });
    voicePromptMock.mockResolvedValue({
      sent: false,
      blockedByMenu: true,
      spoken: "Cette session attend une reponse dans un menu.",
      spokenLanguage: "not-a-language",
      message: "Open it and pick an option.",
    });

    const { view } = mount();
    let sent: boolean | undefined;
    await act(async () => { sent = await view().onRespondSend("oui vas-y"); });

    expect(sent).toBe(false);
    expect(view().menuBlocked).toBe("Open it and pick an option.");
  });
});
