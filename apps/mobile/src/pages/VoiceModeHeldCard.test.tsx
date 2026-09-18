// @vitest-environment jsdom
// THE HELD CARD, at the surface the owner actually looked at.
//
// On 2026-09-17 his fleet had twenty sessions. Eleven of them were held by a live session and were therefore - quite
// correctly - not voice sessions: VoiceModeAllSweep has never enrolled a held session, because those are read by their
// owner and not by him. Nothing said so. Each of their Voice tabs drew this screen's own card, "Voice mode is off for
// this session", with a "Switch to voice mode" button, underneath a banner reading "Every session on the Gateway
// narrates its turns".
//
// Two contradictory sentences on one screen, and a button that could not succeed: captured live, pressing it sent
// POST /sessions/{sid}/voice-mode and got 503 on an unreachable computer, and on a reachable one the off-direction
// sweep is meant to undo it. That is how a feature that was working read as "half the time it doesn't work".
//
// What these pin is only the RENDERING rule - the Gateway decides who is held (VoiceDisplayFold.HeldDisplay, applied
// in GatewayEndpoints.StampFleetRolesAndFold, proven in VoiceHeldSessionStampTests). The client is dumb: it shows the
// Gateway's words and offers nothing.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

const view = {
  voiceOn: false,
  speaking: false,
  voiceDisplay: null as unknown,
  pollDone: true,
  narrative: "",
  title: "112 a worker",
  name: "a worker",
  error: null,
  autoPlayBlocked: false,
  resumed: false,
  playing: false,
  pos: 0,
  dur: 0,
  enabling: false,
  regenerating: false,
  enableNote: "",
  responding: false,
  setResponding: vi.fn(),
  setPlaying: vi.fn(),
  clipUrl: null,
  clipPhase: "none",
  onSwitchOn: vi.fn(),
  onSwitchOff: vi.fn(),
  onGenerateNow: vi.fn(),
  setAudioEl: vi.fn(),
  onLoadedMeta: vi.fn(),
  onTimeUpdate: vi.fn(),
  onEndedAudio: vi.fn(),
  onSeek: vi.fn(),
  onRestart: vi.fn(),
  onTogglePlay: vi.fn(),
  onRespondSend: vi.fn(),
  onRespondSendAudio: vi.fn(),
  menuBlocked: null,
  clearMenuBlocked: vi.fn(),
};

vi.mock("@devthrottle/client-core/voice/useVoiceMode", () => ({
  useVoiceMode: () => view,
  formatClock: () => "0:00",
}));
vi.mock("../components/useSessionManage", () => ({
  useSessionManage: () => ({ onHold: false, busy: false, snooze: vi.fn(), unsnooze: vi.fn(), stop: vi.fn() }),
}));
vi.mock("@devthrottle/client-core/settings/snoozeOptions", () => ({ useSnoozeOptions: () => [] }));
vi.mock("@devthrottle/client-core/settings/snoozeMenu", () => ({ buildSnoozeMenu: () => ({ choices: [], primary: null }) }));
vi.mock("@devthrottle/client-core/voice/queueTouch", () => ({ touchQueue: vi.fn() }));

const { VoiceMode } = await import("./VoiceMode");

function show(voiceDisplay: unknown, voiceOn = false, speaking = false) {
  view.voiceDisplay = voiceDisplay;
  view.voiceOn = voiceOn;
  view.speaking = speaking;
  render(
    <MemoryRouter initialEntries={["/session/s1/voice"]}>
      <Routes>
        <Route path="/session/:sessionId/voice" element={<VoiceMode />} />
      </Routes>
    </MemoryRouter>,
  );
}

const held = {
  kind: "held",
  tone: "neutral",
  label: "Another session is running this one",
  message: "Voice mode narrates the sessions you own.",
  canPlay: false,
  canGenerate: false,
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("the Voice tab of a session a live session owns", () => {
  it("shows the Gateway's words and does NOT offer to switch voice on", () => {
    // REVERT PROOF: drop the showHeld guard from the OFF card and this goes red - the Switch button reappears.
    show(held);

    expect(screen.getByText("Another session is running this one")).toBeTruthy();
    expect(screen.getByText("Voice mode narrates the sessions you own.")).toBeTruthy();
    expect(screen.queryByText("Switch to voice mode")).toBeNull();
    expect(screen.queryByText("Voice mode is off for this session.")).toBeNull();
    expect(screen.queryByText("Generate narration now")).toBeNull();
  });

  it("still offers the switch on a session that is simply off and is the user's own", () => {
    // THE NEGATIVE CONTROL. Without it this change could be hiding the switch on every session in the fleet, and the
    // assertion above would still pass. An unheld session that is merely not in voice mode keeps its one clear action.
    show({ kind: "off", tone: "neutral", label: "Voice off", message: "", canPlay: false, canGenerate: false });

    expect(screen.getByText("Voice mode is off for this session.")).toBeTruthy();
    expect(screen.getByText("Switch to voice mode")).toBeTruthy();
  });

  it("does not play a clip for a held session even when one exists", () => {
    // A held session that was hand-marked into voice mode has audio made from a turn the user never asked to hear.
    // Saying who owns the session beats offering him that clip - the one place this screen takes audio away.
    show(held, /* voiceOn */ true, /* speaking */ true);

    expect(screen.getByText("Another session is running this one")).toBeTruthy();
    expect(screen.queryByText("Speaking")).toBeNull();
  });
});
