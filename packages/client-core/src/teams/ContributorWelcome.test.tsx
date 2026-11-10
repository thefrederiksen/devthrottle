import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import type { ContributorWelcomePage } from "./contributorWelcomeClient";

// The contributor's welcome (Team implementation piece 4.2) against a Gateway answer whose words are deliberately odd: a
// page that drew any word of its own instead of the Gateway's would fail here. The two routes and the push path are
// stood in for, because WHEN each is called is what is under test: the page is marked seen once it has drawn, the button
// opens the Gateway's write path, and permission to notify is asked for only when the line is pressed - never on load.

const gateway = vi.hoisted(() => ({
  page: null as ContributorWelcomePage | null,
  get: vi.fn(),
  seen: vi.fn(),
}));
vi.mock("./contributorWelcomeClient", () => ({
  getContributorWelcome: (teamId: string) => {
    gateway.get(teamId);
    if (gateway.page === null) throw new Error("no welcome answer staged");
    return Promise.resolve(gateway.page);
  },
  markContributorWelcomeSeen: (teamId: string) => {
    gateway.seen(teamId);
    return Promise.resolve({ seen: true });
  },
}));

const push = vi.hoisted(() => ({ enablePush: vi.fn() }));
vi.mock("../push/register", () => ({ enablePush: push.enablePush }));

import { ContributorWelcomeView } from "./ContributorWelcome";

const ODD: ContributorWelcomePage = {
  teamId: "team-owl",
  teamLabel: "Owl~team",
  title: "Hoot~title",
  lead: "Hoot~lead",
  steps: [
    { number: 1, title: "S1~title", text: "S1~text", yours: true, watch: false },
    { number: 2, title: "S2~title", text: "S2~text", yours: false, watch: false },
    { number: 3, title: "S3~title", text: "S3~text", yours: false, watch: false },
    { number: 4, title: "S4~title", text: "S4~text", yours: false, watch: true },
    { number: 5, title: "S5~title", text: "S5~text", yours: false, watch: false },
  ],
  watchLabel: "Watch~label",
  notifications: "Notify~line",
  question: "Ask~question",
  button: { label: "Request~button", href: "/contributor/inbox?team=team-owl" },
};

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function renderWelcome() {
  return render(
    <MemoryRouter initialEntries={["/contributor/welcome"]}>
      <Routes>
        <Route path="/contributor/welcome" element={<ContributorWelcomeView teamId="team-owl" />} />
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  gateway.page = ODD;
  gateway.get.mockClear();
  gateway.seen.mockClear();
  push.enablePush.mockReset();
});

afterEach(() => cleanup());

describe("ContributorWelcomeView", () => {
  it("Render_TheGatewaysAnswer_DrawsEveryWordVerbatimInOrder", async () => {
    renderWelcome();

    await screen.findByText("Hoot~title");
    expect(screen.getByTestId("contributor-welcome-team").textContent).toBe("Owl~team");
    expect(screen.getByText("Hoot~lead")).toBeTruthy();
    const steps = screen.getAllByTestId("contributor-welcome-step");
    expect(steps.map((s) => s.textContent)).toEqual([
      "1S1~titleS1~text",
      "2S2~titleS2~text",
      "3S3~titleS3~text",
      "4S4~titleS4~textWatch~label",
      "5S5~titleS5~text",
    ]);
    // The person's own step is the one drawn as theirs.
    expect(steps.map((s) => s.classList.contains("contributor-welcome-step-yours"))).toEqual([true, false, false, false, false]);
    expect(screen.getByTestId("contributor-welcome-notify").textContent).toBe("Notify~line");
    expect(screen.getByText("Ask~question")).toBeTruthy();
    expect(screen.getByTestId("contributor-welcome-request").textContent).toBe("Request~button");
    expect(gateway.get).toHaveBeenCalledWith("team-owl");
  });

  it("Render_OnceDrawn_MarksTheWelcomeSeenOnce", async () => {
    renderWelcome();

    await screen.findByText("Hoot~title");

    await waitFor(() => expect(gateway.seen).toHaveBeenCalledTimes(1));
    expect(gateway.seen).toHaveBeenCalledWith("team-owl");
  });

  it("SendARequest_Pressed_OpensTheGatewaysWritePath", async () => {
    renderWelcome();

    fireEvent.click(await screen.findByTestId("contributor-welcome-request"));

    expect(screen.getByTestId("where").textContent).toBe("/contributor/inbox?team=team-owl");
  });

  it("Notifications_OnLoad_NothingAsksForPermission", async () => {
    renderWelcome();

    await screen.findByText("Hoot~title");
    await waitFor(() => expect(gateway.seen).toHaveBeenCalled());

    expect(push.enablePush).not.toHaveBeenCalled();
  });

  it("Notifications_Pressed_AsksForPermissionOnceAndShowsTheAnswersSentence", async () => {
    push.enablePush.mockResolvedValue({ state: "granted", message: "Granted~sentence" });
    renderWelcome();

    fireEvent.click(await screen.findByTestId("contributor-welcome-notify"));

    expect((await screen.findByTestId("contributor-welcome-notify-answer")).textContent).toBe("Granted~sentence");
    expect(push.enablePush).toHaveBeenCalledTimes(1);
  });

  it("Notifications_PressedAndBlocked_ShowsWhyInTheGatewayPushPathsOwnWords", async () => {
    push.enablePush.mockResolvedValue({ state: "blocked", message: "Blocked~sentence" });
    renderWelcome();

    fireEvent.click(await screen.findByTestId("contributor-welcome-notify"));

    expect((await screen.findByTestId("contributor-welcome-notify-answer")).textContent).toBe("Blocked~sentence");
  });
});
