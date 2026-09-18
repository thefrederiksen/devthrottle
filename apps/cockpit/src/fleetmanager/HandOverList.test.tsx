// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { HandOverOutcome } from "@devthrottle/client-core/fleetmanager/handOverClient";
import { emptyPage, morningPage } from "./fixtures";

// HAND OVER FROM THE PAGE (the Fleet Manager mission, step 8). The count's control opens the Gateway's list of the
// sessions that ask the owner directly; each row's button hands that session over and shows the Gateway's sentence,
// whichever way it went; a success asks the page to read the Gateway again.

const hand = vi.hoisted(() => ({ run: vi.fn<(sid: string, to: string) => Promise<HandOverOutcome>>() }));
vi.mock("@devthrottle/client-core/fleetmanager/handOverClient", () => ({ runHandOver: hand.run }));

import { FleetPanel } from "./FleetPanel";

function renderPanel(page = morningPage(), openHandOver = false, onChanged = vi.fn()) {
  render(
    <MemoryRouter>
      <FleetPanel state={{ data: page, error: null, loading: false }} openHandOver={openHandOver} onChanged={onChanged} />
    </MemoryRouter>,
  );
  return onChanged;
}

beforeEach(() => hand.run.mockReset());
afterEach(() => cleanup());

describe("the hand-over list on the Fleet Manager page", () => {
  it("is closed until its control is pressed, then lists every session with its action, as sent", () => {
    renderPanel();
    expect(screen.queryByTestId("fmp-handover-list")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Hand sessions to the Fleet Manager... (fake)" }));

    const list = screen.getByTestId("fmp-handover-list");
    expect(within(list).getByRole("heading").textContent).toBe("Sessions that ask you directly (fake) 2");
    expect(list.textContent).toContain("Hand a session over and the Fleet Manager owns it. (fake)");
    const first = screen.getByTestId("fmp-ho-sess-loose-1");
    expect(within(first).getByRole("link", { name: "Invoice export - waiting on a question" }).getAttribute("href")).toBe("/session/sess-loose-1");
    expect(first.textContent).toContain("Needs you - started 2h (fake)");
    expect(first.querySelector(".fmp-dot-red")).not.toBeNull();
    expect(within(first).getByRole("button", { name: "Hand to the Fleet Manager (fake)" }).getAttribute("title")).toBe(
      "The Fleet Manager owns it from now on. (fake)",
    );
    expect(screen.getByRole("button", { name: "Hide the list (fake)" })).toBeTruthy();
  });

  it("opens straight away when the session list linked here", () => {
    renderPanel(morningPage(), true);
    expect(screen.getByTestId("fmp-handover-list")).toBeTruthy();
  });

  it("hands one session over, shows the Gateway's sentence and asks the page to read again", async () => {
    let finish: (o: HandOverOutcome) => void = () => {};
    hand.run.mockReturnValue(new Promise((r) => (finish = r)));
    const onChanged = renderPanel(morningPage(), true);
    const row = screen.getByTestId("fmp-ho-sess-loose-2");

    fireEvent.click(within(row).getByRole("button", { name: "Hand to the Fleet Manager (fake)" }));

    expect(hand.run).toHaveBeenCalledWith("sess-loose-2", "fleet-manager");
    expect((within(row).getByRole("button", { name: "Handing it over... (fake)" }) as HTMLButtonElement).disabled).toBe(true);
    finish({ ok: true, sentence: "Docs site is now the Fleet Manager's. (fake)" });
    await waitFor(() => expect(within(row).getByRole("status").textContent).toBe("Docs site is now the Fleet Manager's. (fake)"));
    expect(within(row).queryByRole("button")).toBeNull();
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it("shows a refusal in the Gateway's words, keeps the button, and does not refresh", async () => {
    hand.run.mockResolvedValue({ ok: false, error: "It is owned by another running session. (fake)" });
    const onChanged = renderPanel(morningPage(), true);
    const row = screen.getByTestId("fmp-ho-sess-loose-1");

    fireEvent.click(within(row).getByRole("button", { name: "Hand to the Fleet Manager (fake)" }));

    await waitFor(() => expect(within(row).getByRole("alert").textContent).toBe("It is owned by another running session. (fake)"));
    expect(within(row).getByRole("button", { name: "Hand to the Fleet Manager (fake)" })).toBeTruthy();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("offers no control when the Gateway lists nothing", () => {
    renderPanel(emptyPage());
    expect(screen.getByTestId("fmp-notmine").querySelector("button")).toBeNull();
  });

  it("draws a listed session with no action as a row with no button", () => {
    const page = morningPage();
    page.notMine.sessions[0].action = null;
    renderPanel(page, true);
    expect(within(screen.getByTestId("fmp-ho-sess-loose-1")).queryByRole("button")).toBeNull();
  });
});
