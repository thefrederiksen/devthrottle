// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { standing } from "./fixtures";

// THE OWNER'S LESSONS AND PREFERENCES ON THE FLEET MANAGER PAGE (issue #3559, part 4). "That was a mistake" keeps the
// owner's words exactly; the list shows the lessons first and the preferences second, each with its date, who kept it
// and whether it is confirmed; Confirm, Edit and Remove each make one Gateway call and then ask the page to read again.
// Every word on screen is the Gateway's.

const api = vi.hoisted(() => ({
  keep: vi.fn<(text: string) => Promise<void>>(),
  confirm: vi.fn<(id: string) => Promise<void>>(),
  edit: vi.fn<(id: string, text: string) => Promise<void>>(),
  remove: vi.fn<(id: string) => Promise<void>>(),
}));
vi.mock("@devthrottle/client-core/fleetmanager/standingClient", () => ({
  keepLesson: api.keep,
  confirmLesson: api.confirm,
  editStanding: api.edit,
  removeStanding: api.remove,
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  reportClientError: vi.fn(),
  describeAndReport: (_s: string, _a: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
}));

import { MistakeBox, StandingPanel } from "./StandingPanel";

beforeEach(() => {
  api.keep.mockReset();
  api.confirm.mockReset();
  api.edit.mockReset();
  api.remove.mockReset();
});
afterEach(() => cleanup());

describe("That was a mistake", () => {
  it("opens empty, keeps the owner's words exactly as typed, and reports it kept", async () => {
    api.keep.mockResolvedValue(undefined);
    const onKept = vi.fn();
    render(<MistakeBox standing={standing()} onKept={onKept} onClose={vi.fn()} />);
    const box = screen.getByRole("textbox", { name: "What should it never do again? (fake)" }) as HTMLTextAreaElement;
    expect(box.value).toBe("");
    expect(box.maxLength).toBe(500);
    expect(screen.getByText("Kept in your words, exactly. (fake)")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Keep (fake)" }) as HTMLButtonElement).disabled).toBe(true);

    const words = "  Never message every session when none is stuck.\nCheck first.  ";
    fireEvent.change(box, { target: { value: words } });
    fireEvent.click(screen.getByRole("button", { name: "Keep (fake)" }));

    await waitFor(() => expect(onKept).toHaveBeenCalledTimes(1));
    expect(api.keep).toHaveBeenCalledWith(words);
  });

  it("shows a refusal in the Gateway's words and keeps what the owner typed", async () => {
    api.keep.mockRejectedValue(new Error("an account holds at most 20 confirmed lessons (fake)"));
    const onKept = vi.fn();
    render(<MistakeBox standing={standing()} onKept={onKept} onClose={vi.fn()} />);
    const box = screen.getByRole("textbox") as HTMLTextAreaElement;
    fireEvent.change(box, { target: { value: "Check first." } });

    fireEvent.click(screen.getByRole("button", { name: "Keep (fake)" }));

    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("an account holds at most 20 confirmed lessons (fake)"));
    expect(box.value).toBe("Check first.");
    expect(onKept).not.toHaveBeenCalled();
  });
});

describe("the list of lessons and preferences", () => {
  it("shows the lessons first and the preferences second, each row's words, date, keeper and confirmation as sent", () => {
    render(<StandingPanel standing={standing()} onChanged={vi.fn()} />);

    const sections = screen.getAllByRole("region").map((s) => s.getAttribute("aria-label"));
    expect(sections).toEqual(["Lessons (fake)", "Standing preferences (fake)"]);

    const waiting = screen.getByTestId("fmp-st-lesson-waiting");
    expect(waiting.className).toContain("fmp-st-waiting");
    expect(waiting.textContent).toContain("Ask before a release.");
    expect(waiting.textContent).toContain("Kept 6 October 2026 by the Fleet Manager (fake)");
    expect(waiting.textContent).toContain("Kept by the Fleet Manager - confirm? (fake)");
    expect(within(waiting).getByRole("button", { name: "Confirm (fake)" })).toBeTruthy();

    const confirmed = screen.getByTestId("fmp-st-lesson-confirmed");
    expect(confirmed.querySelector(".fmp-st-text")!.textContent).toBe("Never message every session when none is stuck.\nCheck first.");
    expect(confirmed.textContent).toContain("What went wrong: messaged all 36 sessions (fake)");
    expect(confirmed.textContent).toContain("Confirmed 5 October 2026 (fake)");
    expect(within(confirmed).queryByRole("button", { name: /Confirm/ })).toBeNull();
    expect(within(confirmed).getByRole("button", { name: "Edit (fake)" })).toBeTruthy();
    expect(within(confirmed).getByRole("button", { name: "Remove (fake)" })).toBeTruthy();

    expect(screen.getByTestId("fmp-st-pref-1").textContent).toContain("Merge docs on green.");
  });

  it("confirms a lesson in one press and asks the page to read again", async () => {
    api.confirm.mockResolvedValue(undefined);
    const onChanged = vi.fn();
    render(<StandingPanel standing={standing()} onChanged={onChanged} />);

    fireEvent.click(within(screen.getByTestId("fmp-st-lesson-waiting")).getByRole("button", { name: "Confirm (fake)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(api.confirm).toHaveBeenCalledWith("lesson-waiting");
  });

  it("edits in a box prefilled with the words, saves the new words exactly, and can be cancelled", async () => {
    api.edit.mockResolvedValue(undefined);
    const onChanged = vi.fn();
    render(<StandingPanel standing={standing()} onChanged={onChanged} />);
    const row = screen.getByTestId("fmp-st-pref-1");

    // A confirmed lesson's edit says the running Fleet Manager is told the new words; a preference's says nothing.
    const confirmed = screen.getByTestId("fmp-st-lesson-confirmed");
    fireEvent.click(within(confirmed).getByRole("button", { name: "Edit (fake)" }));
    expect(confirmed.textContent).toContain("The Fleet Manager is told the new words. (fake)");
    fireEvent.click(within(confirmed).getByRole("button", { name: "Cancel (fake)" }));

    fireEvent.click(within(row).getByRole("button", { name: "Edit (fake)" }));
    const box = within(row).getByRole("textbox", { name: "Edit (fake)" }) as HTMLTextAreaElement;
    expect(box.value).toBe("Merge docs on green.");
    expect(box.maxLength).toBe(2000);
    fireEvent.change(box, { target: { value: "Merge docs on green, never code." } });
    fireEvent.click(within(row).getByRole("button", { name: "Cancel (fake)" }));
    expect(api.edit).not.toHaveBeenCalled();
    expect(row.textContent).toContain("Merge docs on green.");

    fireEvent.click(within(row).getByRole("button", { name: "Edit (fake)" }));
    fireEvent.change(within(row).getByRole("textbox"), { target: { value: "Merge docs on green, never code." } });
    fireEvent.click(within(row).getByRole("button", { name: "Save (fake)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(api.edit).toHaveBeenCalledWith("pref-1", "Merge docs on green, never code.");
    expect(within(row).queryByRole("textbox")).toBeNull();
  });

  it("removes only after the Gateway's question is answered", async () => {
    api.remove.mockResolvedValue(undefined);
    const onChanged = vi.fn();
    render(<StandingPanel standing={standing()} onChanged={onChanged} />);

    fireEvent.click(within(screen.getByTestId("fmp-st-lesson-confirmed")).getByRole("button", { name: "Remove (fake)" }));
    const dialog = screen.getByRole("alertdialog");
    expect(dialog.textContent).toContain("Remove this lesson? (fake)");
    expect(api.remove).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove (fake)" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(api.remove).toHaveBeenCalledWith("lesson-confirmed");
  });

  it("shows a refused confirm in the Gateway's words and does not refresh", async () => {
    api.confirm.mockRejectedValue(new Error("20 lessons are already confirmed (fake)"));
    const onChanged = vi.fn();
    render(<StandingPanel standing={standing()} onChanged={onChanged} />);
    const row = screen.getByTestId("fmp-st-lesson-waiting");

    fireEvent.click(within(row).getByRole("button", { name: "Confirm (fake)" }));

    await waitFor(() => expect(within(row).getByRole("alert").textContent).toBe("20 lessons are already confirmed (fake)"));
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("offers no Confirm the Gateway withheld, and shows its reason", () => {
    const s = standing();
    s.lessons.rows[0].confirm = { offered: false, label: "Confirm (fake)", busyLabel: "", note: "20 are confirmed. Remove one. (fake)" };
    render(<StandingPanel standing={s} onChanged={vi.fn()} />);
    const row = screen.getByTestId("fmp-st-lesson-waiting");

    expect(within(row).queryByRole("button", { name: "Confirm (fake)" })).toBeNull();
    expect(row.textContent).toContain("20 are confirmed. Remove one. (fake)");
  });
});
