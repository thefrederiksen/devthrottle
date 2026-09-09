// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionStopOutcome } from "@devthrottle/client-core/api/client";
import type { SessionManage } from "./useSessionManage";

// The phone's reason box submits on Enter, exactly as the Cockpit's does. CLAUDE.md rule 8's reasoning
// is that the two surfaces may differ in LAYOUT and not in behaviour, and "Enter sends it on the desktop
// and does nothing on the phone" is a behaviour difference, not a layout one.
//
// The empty-reason rule holds on this path too: Enter on an empty box sends nothing, the same as the
// disabled button.

const navigateMock = vi.fn();
vi.mock("react-router-dom", () => ({
  useNavigate: () => navigateMock,
  useParams: () => ({ sessionId: "9c41e7a2" }),
}));

import { SessionAppBar } from "./SessionAppBar";

const stopSessionMock = vi.fn();

function manage(): SessionManage {
  return {
    onHold: false,
    held: false,
    deferred: false,
    snoozed: false,
    holdCountdown: null,
    deliveryNotice: null,
    busy: false,
    error: null,
    setError: () => {},
    toggleHold: () => Promise.resolve(true),
    holdFor: () => Promise.resolve(true),
    stopSession: (reason: string) => stopSessionMock(reason) as Promise<SessionStopOutcome>,
  };
}

function openStopSheet() {
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
}

function reasonBox() {
  return screen.getByPlaceholderText("Spawned into the wrong mode");
}

beforeEach(() => {
  stopSessionMock.mockReset();
  navigateMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("the phone reason box submits on Enter, like the Cockpit's", () => {
  it("sends the stop when Enter is pressed with a reason in the box", async () => {
    stopSessionMock.mockResolvedValue({ headline: "the Gateway said so", details: [] });
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledWith("spawned into the wrong mode"));
    expect(await screen.findByText("the Gateway said so")).toBeTruthy();
  });

  it("sends nothing when Enter is pressed with an empty box", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.keyDown(reasonBox(), { key: "Enter" });

    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("sends nothing when Enter is pressed with only whitespace", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "   " } });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });

    expect(stopSessionMock).not.toHaveBeenCalled();
  });
});
