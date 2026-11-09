// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ContributorNoticeCard } from "./ContributorNoticeCard";

// HOW A CONTRIBUTOR IS TOLD THAT SOMETHING IS WAITING (Team implementation piece 4.4): the card prints the Gateway's words
// as they are, shows the hour only when the Gateway says so, saves a choice with one call, and is absent where the
// Gateway has no such setting or says it does not apply.
const card = {
  applies: true,
  title: "Email when something is sent to you",
  explanation: "When something is sent to you in a team, DevThrottle can tell you by email.",
  mode: "now",
  options: [
    { value: "now", label: "Email me right away", selected: true },
    { value: "daily", label: "Once a day, in one email", selected: false },
    { value: "never", label: "Never - I will check myself", selected: false },
  ],
  showHour: false,
  hourLabel: "Send the daily email at",
  dailyHour: 8,
  hours: Array.from({ length: 24 }, (_, h) => ({ value: h, label: `${String(h).padStart(2, "0")}:00` })),
  timeZoneLine: "Times are in your own time zone, Europe/Copenhagen.",
  note: "The email only says that something is waiting, and in which team, with a link. What was sent stays on DevThrottle.",
  savedLine: null,
};

const daily = {
  ...card,
  mode: "daily",
  options: card.options.map((o) => ({ ...o, selected: o.value === "daily" })),
  showHour: true,
  savedLine: "Saved. You get one email a day at 08:00, only when something is waiting.",
};

function stubGateway(first: object | { status: number }, put?: object) {
  const bodies: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (url !== "/teams/notifications") return new Response("not stubbed", { status: 500 });
      if ((init?.method ?? "GET") === "PUT") {
        bodies.push(String(init?.body));
        return new Response(JSON.stringify(put), { status: 200 });
      }
      if ("status" in first && !("title" in first))
        return new Response(JSON.stringify({ error: "Not found" }), { status: (first as { status: number }).status });
      return new Response(JSON.stringify(first), { status: 200 });
    }),
  );
  return bodies;
}

describe("ContributorNoticeCard", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("prints the Gateway's title, choices and note, with no hour for right away", async () => {
    stubGateway(card);
    render(<ContributorNoticeCard />);

    expect(await screen.findByText("Email when something is sent to you")).toBeTruthy();
    expect(screen.getByLabelText("Email me right away")).toHaveProperty("checked", true);
    expect(screen.getByLabelText("Once a day, in one email")).toHaveProperty("checked", false);
    expect(screen.getByLabelText("Never - I will check myself")).toBeTruthy();
    expect(screen.getByTestId("contributor-notice-note").textContent).toBe(card.note);
    expect(screen.queryByTestId("contributor-notice-hour")).toBeNull();
  });

  it("saves once a day with the hour it shows, then prints the Gateway's saved line and the hour", async () => {
    const bodies = stubGateway(card, daily);
    render(<ContributorNoticeCard />);

    fireEvent.click(await screen.findByLabelText("Once a day, in one email"));

    await waitFor(() => expect(screen.getByText(daily.savedLine)).toBeTruthy());
    expect(JSON.parse(bodies[0])).toEqual({ mode: "daily", dailyHour: 8 });
    expect(screen.getByTestId("contributor-notice-hour")).toBeTruthy();
    expect(screen.getByText("Times are in your own time zone, Europe/Copenhagen.")).toBeTruthy();
  });

  it("shows nothing where the Gateway says it does not apply", async () => {
    stubGateway({ ...card, applies: false });
    const { container } = render(<ContributorNoticeCard />);

    await waitFor(() => expect(screen.queryByText("Loading...")).toBeNull());
    expect(container.innerHTML).toBe("");
  });

  it("shows nothing on a Gateway that has no such setting", async () => {
    stubGateway({ status: 404 });
    const { container } = render(<ContributorNoticeCard />);

    await waitFor(() => expect(screen.queryByText("Loading...")).toBeNull());
    expect(container.innerHTML).toBe("");
  });

  it("shows the Gateway's own sentence when the read fails", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({ error: "DevThrottle could not do this just now." }), { status: 500 })),
    );
    render(<ContributorNoticeCard />);

    expect(await screen.findByText(/Could not load your email choice/)).toBeTruthy();
  });
});
