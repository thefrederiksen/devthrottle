// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// A transcript path the browser would not copy is shown on the card to copy by hand. The refusal is still a failure
// the person hit, so it is reported (the step 3 rulings, R6 and R12) - as the browser's refusal, never as an
// unreachable Gateway.

const reported = vi.hoisted(() => vi.fn((_s: string, _a: string, err: unknown) => String(err)));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({ describeAndReport: reported }));
vi.mock("@devthrottle/client-core/recordings/recordingsClient", () => ({
  deleteRecording: vi.fn(),
  getAgentInfo: vi.fn(),
  getTranscript: vi.fn(async () => "The words of the transcript (fake)."),
  promoteRecording: vi.fn(),
  recordingAudioUrl: vi.fn(() => ""),
  updateRecordingMeta: vi.fn(),
  getRecordings: vi.fn(async () => [
    {
      recordingId: "rec-1",
      title: "Morning notes (fake)",
      startedAt: "2026-10-10T08:00:00Z",
      state: "transcribed",
      segments: 1,
      durationMs: 1000,
      hasTranscript: true,
      transcriptPath: "C:/vault/morning-notes.md",
      inVault: false,
    },
  ]),
}));

import { TranscriptsView } from "./TranscriptsView";
import { ClipboardRefusedError } from "../components/clipboardFailure";

describe("TranscriptsView copy path", () => {
  beforeEach(() => {
    reported.mockClear();
    // A page the browser does not trust has no clipboard API at all.
    vi.stubGlobal("navigator", { ...navigator, clipboard: undefined });
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("copyPath_ClipboardRefused_ShowsThePathAndReportsTheRefusal", async () => {
    render(
      <MemoryRouter>
        <TranscriptsView />
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByText("Morning notes (fake)"));
    fireEvent.click(await screen.findByText("Copy path"));

    expect(await screen.findByText(/C:\/vault\/morning-notes\.md/)).toBeTruthy();
    expect(reported).toHaveBeenCalledTimes(1);
    expect(reported).toHaveBeenCalledWith("cockpit-transcripts", "copy the transcript path", expect.any(ClipboardRefusedError));
  });
});
