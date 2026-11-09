// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// THE CONTRIBUTOR'S EMAIL CHOICE ON THE PHONE (Team implementation piece 4.4; CLAUDE.md rule 8): the phone's own Settings
// screen, on its Notifications tab, shows the shared card with the Gateway's words as they are - the same card the
// Cockpit shows. Only the frame is the phone's.

vi.mock("../components/AppBarAlerts", () => ({ AppBarAlerts: () => null }));

import { Settings } from "./Settings";

const card = {
  applies: true,
  title: "Email when something is sent to you",
  explanation: "When something is sent to you in a team, DevThrottle can tell you by email.",
  mode: "daily",
  options: [
    { value: "now", label: "Email me right away", selected: false },
    { value: "daily", label: "Once a day, in one email", selected: true },
    { value: "never", label: "Never - I will check myself", selected: false },
  ],
  showHour: true,
  hourLabel: "Send the daily email at",
  dailyHour: 17,
  hours: Array.from({ length: 24 }, (_, h) => ({ value: h, label: `${String(h).padStart(2, "0")}:00` })),
  timeZoneLine: "Times are in your own time zone, America/Toronto.",
  note: "The email only says that something is waiting, and in which team, with a link. What was sent stays on DevThrottle.",
  savedLine: null,
};

describe("phone Settings - the contributor's email choice", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the Gateway's card on the Notifications tab, word for word", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) =>
        url === "/teams/notifications"
          ? new Response(JSON.stringify(card), { status: 200 })
          : new Response(JSON.stringify({ error: "not part of this test" }), { status: 503 }),
      ),
    );

    render(
      <MemoryRouter initialEntries={["/settings?tab=notifications"]}>
        <Settings />
      </MemoryRouter>,
    );

    expect(await screen.findByText("Email when something is sent to you")).toBeTruthy();
    expect(screen.getByLabelText("Once a day, in one email")).toHaveProperty("checked", true);
    expect((screen.getByLabelText("Send the daily email at") as HTMLSelectElement).value).toBe("17");
    expect(screen.getByText("Times are in your own time zone, America/Toronto.")).toBeTruthy();
    expect(screen.getByTestId("contributor-notice-note").textContent).toBe(card.note);
  });
});
