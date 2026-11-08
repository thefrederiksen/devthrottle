// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { ScheduleRowMenu } from "./ScheduleRowMenu";

// The "..." beside Run now on a Schedule row (Web UI Polish, fix 7): Edit and Delete, or Edit in factory.

afterEach(() => cleanup());

function renderMenu(props: Parameters<typeof ScheduleRowMenu>[0]) {
  return render(
    <MemoryRouter>
      <ScheduleRowMenu {...props} />
    </MemoryRouter>,
  );
}

describe("ScheduleRowMenu", () => {
  it("ScheduleRowMenu_Closed_ShowsOnlyTheButton", () => {
    renderMenu({ jobName: "Morning sweep", onEdit: vi.fn(), onDelete: vi.fn() });
    expect(screen.getByRole("button", { name: "More actions for Morning sweep" }).getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("ScheduleRowMenu_EditChosen_CallsEditAndCloses", () => {
    const onEdit = vi.fn();
    const onDelete = vi.fn();
    renderMenu({ jobName: "Morning sweep", onEdit, onDelete });
    fireEvent.click(screen.getByRole("button", { name: "More actions for Morning sweep" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Edit" }));
    expect(onEdit).toHaveBeenCalledTimes(1);
    expect(onDelete).not.toHaveBeenCalled();
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("ScheduleRowMenu_DeleteChosen_CallsDelete", () => {
    const onDelete = vi.fn();
    renderMenu({ jobName: "Morning sweep", onEdit: vi.fn(), onDelete });
    fireEvent.click(screen.getByRole("button", { name: "More actions for Morning sweep" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Delete" }));
    expect(onDelete).toHaveBeenCalledTimes(1);
  });

  it("ScheduleRowMenu_FactorySchedule_OffersOnlyEditInFactory", () => {
    renderMenu({ jobName: "Funnel run", factoryHref: "/factories/clickfunnels/seats" });
    fireEvent.click(screen.getByRole("button", { name: "More actions for Funnel run" }));
    const link = screen.getByRole("menuitem", { name: "Edit in factory" });
    expect(link.getAttribute("href")).toBe("/factories/clickfunnels/seats");
    expect(screen.queryByRole("menuitem", { name: "Edit" })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "Delete" })).toBeNull();
  });

  it("ScheduleRowMenu_OutsideClick_Closes", () => {
    renderMenu({ jobName: "Morning sweep", onEdit: vi.fn(), onDelete: vi.fn() });
    fireEvent.click(screen.getByRole("button", { name: "More actions for Morning sweep" }));
    expect(screen.getByRole("menu")).toBeTruthy();
    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("ScheduleRowMenu_Opened_FocusesTheFirstItemAndArrowsMove", () => {
    renderMenu({ jobName: "Morning sweep", onEdit: vi.fn(), onDelete: vi.fn() });
    fireEvent.click(screen.getByRole("button", { name: "More actions for Morning sweep" }));
    const edit = screen.getByRole("menuitem", { name: "Edit" });
    const del = screen.getByRole("menuitem", { name: "Delete" });
    expect(document.activeElement).toBe(edit);

    const menu = screen.getByRole("menu");
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(del);
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(edit);
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(document.activeElement).toBe(del);
  });

  it("ScheduleRowMenu_Escape_ClosesAndReturnsFocusToTheButton", () => {
    renderMenu({ jobName: "Morning sweep", onEdit: vi.fn(), onDelete: vi.fn() });
    const button = screen.getByRole("button", { name: "More actions for Morning sweep" });
    fireEvent.click(button);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(button);
  });

  it("ScheduleRowMenu_ItemChosen_ReturnsFocusToTheButton", () => {
    renderMenu({ jobName: "Morning sweep", onEdit: vi.fn(), onDelete: vi.fn() });
    const button = screen.getByRole("button", { name: "More actions for Morning sweep" });
    fireEvent.click(button);
    fireEvent.click(screen.getByRole("menuitem", { name: "Edit" }));
    expect(document.activeElement).toBe(button);
  });
});
