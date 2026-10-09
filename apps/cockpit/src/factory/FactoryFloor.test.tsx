// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { FactoryFloorView } from "@devthrottle/client-core/factory/factoriesScreenClient";

// The factory floor (owner decision, 8 Oct 2026). Held down: the drawing is the Gateway's - lanes, office, bays,
// arrows, desk, legend and notes are rendered exactly as sent, a seat's bay opens the Seats tab, a source has no link,
// and a refusal shows the Gateway's sentence.

const screenClient = vi.hoisted(() => ({ getFactoryFloor: vi.fn() }));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

import { FactoryFloorPanel } from "./FactoryFloor";

const FLOOR: FactoryFloorView = {
  factoryId: "website-business",
  title: "Website Business",
  width: 1000,
  height: 400,
  office: { title: "Boss's office", sub: "Daily 01:00 - Today 01:05 - succeeded", tone: "ok", x: 166, y: 20, width: 600, height: 58 },
  lanes: [
    { name: "Find and sell", hue: 0, x: 20, y: 100, width: 760, height: 102 },
    { name: "Mailbox", hue: 1, x: 20, y: 214, width: 760, height: 102 },
  ],
  bays: [
    { id: "scout", kind: "seat", title: "Scout", sub: "Weekdays 02:00", tone: "red", toneText: "Today 02:05 - failed", dashed: false, x: 166, y: 126, width: 160, height: 50, href: "/factories/website-business/seats" },
    { id: "sender", kind: "seat", title: "Sender", sub: "Not scheduled", tone: "idle", toneText: "Not run yet", dashed: false, x: 370, y: 126, width: 160, height: 50, href: "/factories/website-business/seats" },
    { id: "mail", kind: "source", title: "Business email", sub: "(replies)", tone: "neutral", toneText: "Work arrives from here.", dashed: false, x: 166, y: 240, width: 160, height: 50, href: null },
  ],
  arrows: [
    { from: "scout", to: "sender", label: "succeeded: starts", tone: "ok", line: "solid", path: "M 326 151 C 338 151 358 151 362 151", head: "370,151 362,147.5 362,154.5", labelX: 348, labelY: 145 },
  ],
  desk: { title: "Your desk", sub: "approve, answer, read", x: 836, y: 100, width: 220, height: 216, items: [{ title: "Scout, today 02:05", text: "Meter read failed.", tone: "red", href: null }], emptyText: null, note: "Failures also reach you by email, from 3 seats." },
  legend: [{ text: "On success, starts the next", tone: "ok", line: "solid" }],
  notes: ["The arrows are the ones the factory published in its map, 8 Oct 2026."],
};

function renderPanel() {
  return render(
    <MemoryRouter>
      <FactoryFloorPanel factory="website-business" />
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  screenClient.getFactoryFloor.mockReset();
});

describe("The factory floor", () => {
  it("draws the Gateway's lanes, office, bays, arrows, desk, legend and notes as sent", async () => {
    screenClient.getFactoryFloor.mockResolvedValue(FLOOR);
    renderPanel();

    const floor = await screen.findByTestId("ff-floor");
    expect(screenClient.getFactoryFloor).toHaveBeenCalledWith("website-business", expect.anything());
    expect(floor.getAttribute("viewBox")).toBe("0 0 1000 400");
    expect(within(floor).getAllByTestId("ff-lane").map((l) => l.textContent)).toEqual(["Find and sell", "Mailbox"]);
    expect(within(floor).getByTestId("ff-office").textContent).toContain("Boss's office");
    expect(within(floor).getByTestId("ff-bay-scout").textContent).toContain("Weekdays 02:00");
    expect(within(floor).getByTestId("ff-bay-scout").querySelector("circle")?.getAttribute("class")).toContain("ff-tone-red");
    expect(within(floor).getByTestId("ff-arrow-scout-sender").querySelector("path")?.getAttribute("d")).toBe(FLOOR.arrows[0].path);
    expect(within(floor).getByText("succeeded: starts")).toBeTruthy();
    expect(within(floor).getByTestId("ff-desk").textContent).toContain("Failures also reach you by email, from 3 seats.");
    expect(screen.getByTestId("ff-legend").textContent).toBe("On success, starts the next");
    expect(screen.getByText(FLOOR.notes[0])).toBeTruthy();
  });

  it("links a seat's bay to the Seats tab, and leaves a source of work unlinked", async () => {
    screenClient.getFactoryFloor.mockResolvedValue(FLOOR);
    renderPanel();

    const scout = await screen.findByTestId("ff-bay-scout");
    expect(scout.closest("a")?.getAttribute("href")).toBe("/factories/website-business/seats");
    expect(screen.getByTestId("ff-bay-mail").closest("a")).toBeNull();
  });

  it("shows the Gateway's sentence when the floor cannot be read", async () => {
    screenClient.getFactoryFloor.mockRejectedValue(new Error("There is no registered factory 'nope'."));
    renderPanel();

    await screen.findByText((_, el) => el?.tagName === "P" && /floor/i.test(el.textContent ?? ""));
    expect(screen.queryByTestId("ff-floor")).toBeNull();
  });
});
