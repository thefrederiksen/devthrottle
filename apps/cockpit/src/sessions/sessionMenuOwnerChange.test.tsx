// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { HandOverOutcome } from "@devthrottle/client-core/fleetmanager/handOverClient";

// THE CHANGE OF OWNER IN THE SESSION MENU (the Fleet Manager mission, step 8). The menu offers exactly what the
// Gateway offers on the row (SessionDto.ownerChange) - "Hand back to me" on a Fleet Manager's session - sends that
// direction, and shows the Gateway's sentence whichever way it went. A row the Gateway offers nothing on has no item.

const hand = vi.hoisted(() => ({ run: vi.fn<(sid: string, to: string) => Promise<HandOverOutcome>>() }));
vi.mock("@devthrottle/client-core/fleetmanager/handOverClient", () => ({ runHandOver: hand.run }));
vi.mock("@devthrottle/client-core/api/client", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/api/client");
  return { ...actual, holdSession: vi.fn(), getHandover: vi.fn() };
});
vi.mock("@devthrottle/client-core/settings/snoozeOptions", () => ({ useSnoozeOptions: () => null }));
vi.mock("./StopSessionProvider", () => ({ useStopSession: () => ({ openStop: vi.fn() }) }));

import { SessionMenu } from "./SessionMenu";

function session(ownerChange: SessionDto["ownerChange"]): SessionDto {
  return { sessionId: "sess-owned-1", name: "Owned by the Fleet Manager", ownerChange } as unknown as SessionDto;
}

const handBack = {
  to: "owner",
  label: "Hand back to me (fake)",
  title: "It asks you directly again. (fake)",
  busyLabel: "Handing it back... (fake)",
};

beforeEach(() => hand.run.mockReset());
afterEach(() => cleanup());

function openMenu() {
  fireEvent.click(screen.getByRole("button", { name: "Session menu" }));
}

describe("the session menu's change of owner", () => {
  it("offers the Gateway's hand back, sends it, and shows the Gateway's sentence", async () => {
    let finish: (o: HandOverOutcome) => void = () => {};
    hand.run.mockReturnValue(new Promise((r) => (finish = r)));
    render(<SessionMenu session={session(handBack)} variant="rail" />);
    openMenu();

    const item = screen.getByRole("menuitem", { name: "Hand back to me (fake)" });
    expect(item.getAttribute("title")).toBe("It asks you directly again. (fake)");
    fireEvent.click(item);

    expect(hand.run).toHaveBeenCalledWith("sess-owned-1", "owner");
    expect(screen.getByRole("status").textContent).toBe("Handing it back... (fake)");
    finish({ ok: true, sentence: "It is yours again. (fake)" });
    await waitFor(() => expect(screen.getByRole("status").textContent).toBe("It is yours again. (fake)"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("shows a refusal in the Gateway's words", async () => {
    hand.run.mockResolvedValue({ ok: false, error: "Its Director is older than hand over. (fake)" });
    render(<SessionMenu session={session(handBack)} variant="rail" />);
    openMenu();

    fireEvent.click(screen.getByRole("menuitem", { name: "Hand back to me (fake)" }));

    await waitFor(() => expect(screen.getByText("Its Director is older than hand over. (fake)")).toBeTruthy());
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("offers nothing when the Gateway offers nothing", () => {
    render(<SessionMenu session={session(null)} variant="rail" />);
    openMenu();

    expect(screen.queryByRole("menuitem", { name: /Hand (back|to)/ })).toBeNull();
    expect(screen.getByRole("menuitem", { name: "Handover info" })).toBeTruthy();
  });
});
