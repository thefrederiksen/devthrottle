// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import { ConfirmDialog } from "./ConfirmDialog";

// A CONFIRMATION THAT NEEDS A CHOICE IN ITS BODY STAYS SHUT UNTIL THE CHOICE IS MADE (confirmDisabled). The Licenses
// page uses it so that Switch now is never sent without a target; this holds the property on the dialog itself, so
// it stays covered whatever the pages that use it do.

afterEach(cleanup);

function confirmButton(): HTMLButtonElement {
  return screen.getByRole("button", { name: "Switch now" }) as HTMLButtonElement;
}

describe("ConfirmDialog: confirmDisabled", () => {
  it("shuts the confirm button and runs nothing when it is pressed, while Cancel still works", () => {
    const onConfirm = vi.fn();
    const onClose = vi.fn();
    render(<ConfirmDialog open title="Switch now?" message="Pick a target." confirmLabel="Switch now" confirmDisabled onConfirm={onConfirm} onClose={onClose} />);

    expect(confirmButton().disabled).toBe(true);
    fireEvent.click(confirmButton());
    expect(onConfirm).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("is open to be confirmed when the property is left out, as every other caller leaves it", () => {
    const onConfirm = vi.fn();
    render(<ConfirmDialog open title="Switch now?" message="Sure?" confirmLabel="Switch now" onConfirm={onConfirm} onClose={() => undefined} />);

    expect(confirmButton().disabled).toBe(false);
    fireEvent.click(confirmButton());
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });
});
