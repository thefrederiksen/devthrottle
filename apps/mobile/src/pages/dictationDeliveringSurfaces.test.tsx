// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";
import {
  allDictationStatuses,
  clearDictationStatus,
  publishDictationStatus,
} from "@devthrottle/client-core/dictation/status";
import { SessionRow } from "./Home";

// The phone's two dictation surfaces for the phase 2 states (voice delivery, #3398): the status strip the
// Terminal, Chat and Voice screens mount (<DictationStatusStrip sessionId=... />, exactly as here), and the
// roster card's badge. The status store is real; each status is published exactly as the driver
// publishes it.
//
//   - Still delivering: the words may already be in the session, so it is calm (never red, never the amber
//     "still sending"), and nothing on it can send a second copy - no "Send anyway", no fresh-id "Retry".
//   - Too old: the words come back with "Send anyway" and the age wording.
//   - Could not confirm (phase 2, change 1): the words come back with the label and Dismiss only, because the
//     Gateway says a second copy might double them.

vi.mock("@devthrottle/client-core/restart/RestartRequestsPanel", () => ({
  RestartRequestsPanel: () => null,
}));

afterEach(() => {
  cleanup();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
});

const SESSION_ID = "session-one";

function session(): SessionDto {
  return {
    sessionId: SESSION_ID,
    directorId: "director-one",
    machineName: "SOREN_NORTH",
    name: "testing pi",
    agent: "Pi",
    agentToolDisplay: "Pi",
    currentModel: "gpt-5.6-sol",
    modelDisplay: { kind: "reported", text: "gpt-5.6-sol", modelId: "gpt-5.6-sol", tooltip: "gpt-5.6-sol", isAbsent: false },
    activityState: "Working",
    status: "Running",
    effectiveColor: "blue",
    effectiveColorHex: "#3b82f6",
    triageBucket: "active",
    stateLabel: "Working",
  } as SessionDto;
}

function publishDelivering(): void {
  publishDictationStatus({
    sessionId: SESSION_ID,
    uploadId: "up-delivering",
    phase: "held",
    retryable: true,
    delivering: true,
    error: "Still delivering - checking that your words reached the session. They will not be sent twice.",
  });
}

describe("phone: Still delivering", () => {
  it("the session screen's strip is calm and offers neither Send anyway nor a fresh-id Retry", () => {
    publishDelivering();
    render(<DictationStatusStrip sessionId={SESSION_ID} />);

    const strip = screen.getByText(/Still delivering/).closest(".dictate-strip") as HTMLElement;
    expect(strip.className).toContain("dictate-strip-delivering");
    expect(strip.className).not.toContain("dictate-strip-failed");
    expect(strip.className).not.toContain("dictate-strip-held");
    expect(strip.getAttribute("role")).toBe("status");
    expect(within(strip).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(strip).queryByRole("button", { name: "Retry" })).toBeNull();
    // The Gateway drives this delivery itself (phase 5): the one button reads what it ruled.
    expect(within(strip).getByRole("button", { name: "Check now" })).toBeTruthy();
    expect(within(strip).queryByRole("button", { name: "Upload now" })).toBeNull();
  });

  it("the roster card says Still delivering, calmly, not Saved - still sending", () => {
    publishDelivering();
    render(
      <MemoryRouter>
        <SessionRow session={session()} />
      </MemoryRouter>,
    );

    const badge = screen.getByText("Still delivering");
    expect(badge.className).toContain("row-dictate-busy");
    expect(screen.queryByText("Saved - still sending")).toBeNull();
    expect(screen.queryByText("Not sent - tap to open")).toBeNull();
  });
});

describe("phone: too old", () => {
  it("the strip shows the words back with Send anyway and the age wording", () => {
    publishDictationStatus({
      sessionId: SESSION_ID,
      uploadId: "up-too-old",
      phase: "dropped",
      retryable: false,
      recoverableText: "the words from six minutes ago",
      offerSendAnyway: true,
      error: "This recording is more than 5 minutes old, so it was not sent automatically. Here is what you said - send it?",
    });
    render(<DictationStatusStrip sessionId={SESSION_ID} />);

    const strip = screen.getByText(/more than 5 minutes old/).closest(".dictate-strip") as HTMLElement;
    expect(within(strip).getByText("the words from six minutes ago")).toBeTruthy();
    expect(within(strip).getByRole("button", { name: "Send anyway" })).toBeTruthy();
  });
});

describe("phone: could not confirm it arrived (phase 2, change 1)", () => {
  it("the strip shows the words, the label and Dismiss, and no Send anyway or Retry", async () => {
    publishDictationStatus({
      sessionId: SESSION_ID,
      uploadId: "up-unconfirmed",
      phase: "dropped",
      retryable: false,
      offerSendAnyway: false,
      recoverableText: "the words nobody confirmed",
      error: "We could not confirm this arrived. Here is what you said.",
    });
    render(<DictationStatusStrip sessionId={SESSION_ID} />);

    const strip = screen.getByText("We could not confirm this arrived. Here is what you said.").closest(".dictate-strip") as HTMLElement;
    expect(within(strip).getByText("the words nobody confirmed")).toBeTruthy();
    expect(within(strip).getByRole("button", { name: "Dismiss" })).toBeTruthy();
    expect(within(strip).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(strip).queryByRole("button", { name: "Retry" })).toBeNull();
    expect(within(strip).queryByRole("button", { name: "Upload now" })).toBeNull();

    fireEvent.click(within(strip).getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(screen.queryByText("the words nobody confirmed")).toBeNull());
  });

  it("a shown-back status that does not carry the Gateway's offer offers no second send", () => {
    publishDictationStatus({
      sessionId: SESSION_ID,
      uploadId: "up-no-offer",
      phase: "dropped",
      retryable: false,
      recoverableText: "words with no verdict",
      error: "This recording wasn't sent automatically. Here is what you said - send it?",
    });
    render(<DictationStatusStrip sessionId={SESSION_ID} />);

    const strip = screen.getByText("words with no verdict").closest(".dictate-strip") as HTMLElement;
    expect(within(strip).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(strip).getByRole("button", { name: "Dismiss" })).toBeTruthy();
  });
});
