// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { FactoryManagerCard as CardView } from "@devthrottle/client-core/factory/factoriesScreenClient";

// The Factory Manager card (Factory Control, step 5). What is held down:
//   * the card shows the Gateway's words verbatim - title, purpose, state, button, placement text and name check;
//   * before the first start it asks for a computer (from the Gateway's list) and a folder, and sends both;
//   * once a placement is saved, it sends neither, and shows where it runs;
//   * a press shows the busy label at once and opens the session the Gateway answered with;
//   * a refusal shows the Gateway's own sentence beside the button;
//   * the name check's findings are listed, each in the Gateway's sentence.

const screenClient = vi.hoisted(() => ({
  getFactoryManagerCard: vi.fn(),
  startFactoryManagerTalk: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

import { FactoryManagerCard } from "./FactoryManagerCard";

const NOT_STARTED: CardView = {
  title: "Factory Manager",
  purpose: "The one seat over all your factories.",
  stateText: "Not started yet",
  stateTone: "grey",
  sessionHref: null,
  actionLabel: "Talk to the Factory Manager",
  busyLabel: "Starting the Factory Manager...",
  needsPlacement: true,
  computers: ["DEVLINUX", "SOREN_NORTH"],
  placementText: "Choose the computer it runs on and a folder there for it to work in.",
  // Not the words the Gateway sends today, on purpose: a label written in the Cockpit would not match these.
  computerLabel: "Runs on this computer",
  folderLabel: "Works in this folder",
  placementSavedText: null,
  nameCheck: {
    count: 2,
    factoriesChecked: 3,
    headingText: "Name check: 2 seats flagged",
    ruleText: "A seat is flagged when its id, name or role says CEO.",
    passText: null,
    findings: [
      { factory: "warmforward", factoryTitle: "WarmForward", seatId: "ceo", kind: "ceo",
        text: "WarmForward: the seat 'ceo' (named 'Boss', role 'Boss') says CEO. A factory has a boss, not a CEO." },
      { factory: "tallyhand", factoryTitle: "Tallyhand", seatId: "boss", kind: "not-a-role-word",
        text: "Tallyhand: the seat 'boss' is named 'Nora Hale', which is not a role word of this factory (its role is Boss)." },
    ],
  },
};

const RUNNING: CardView = {
  ...NOT_STARTED,
  stateText: "Running on SOREN_NORTH",
  stateTone: "ok",
  sessionHref: "/session/s-1",
  actionLabel: "Open the Factory Manager",
  needsPlacement: false,
  computers: [],
  placementText: null,
  computerLabel: null,
  folderLabel: null,
  placementSavedText: "Runs on SOREN_NORTH in D:\\ReposFred\\factories",
  nameCheck: { count: 0, factoriesChecked: 3, headingText: "Name check: nothing flagged", ruleText: "rule", passText: "Every factory passes.", findings: [] },
};

function Where() {
  return <div data-testid="where">{useLocation().pathname}</div>;
}

function show() {
  render(
    <MemoryRouter initialEntries={["/factories"]}>
      <Routes>
        <Route path="/factories" element={<FactoryManagerCard />} />
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("FactoryManagerCard", () => {
  it("shows the Gateway's words, the placement form and every name-check finding", async () => {
    screenClient.getFactoryManagerCard.mockResolvedValue(NOT_STARTED);
    show();

    expect(await screen.findByText("Factory Manager")).toBeTruthy();
    expect(screen.getByText("Not started yet")).toBeTruthy();
    expect(screen.getByText("The one seat over all your factories.")).toBeTruthy();
    expect(screen.getByText(NOT_STARTED.placementText!)).toBeTruthy();
    expect(screen.getByTestId("fa-fm-computer")).toBeTruthy();
    expect(screen.getByText("Name check: 2 seats flagged")).toBeTruthy();
    expect(screen.getByTestId("fa-fm-finding-warmforward-ceo").textContent).toBe(NOT_STARTED.nameCheck.findings[0].text);
    expect(screen.getByTestId("fa-fm-finding-tallyhand-boss").textContent).toBe(NOT_STARTED.nameCheck.findings[1].text);
  });

  it("labels the computer and the folder with the Gateway's words, and shows no path of its own in the folder field", async () => {
    screenClient.getFactoryManagerCard.mockResolvedValue(NOT_STARTED);
    show();

    const computer = await screen.findByTestId("fa-fm-computer");
    const folder = screen.getByTestId("fa-fm-folder");

    expect(screen.getByLabelText("Runs on this computer")).toBe(computer);
    expect(screen.getByLabelText("Works in this folder")).toBe(folder);
    expect(screen.queryByText("Computer")).toBeNull();
    expect(screen.queryByText("Folder")).toBeNull();
    expect(folder.getAttribute("placeholder")).toBeNull();
  });

  it("sends the chosen computer and folder on the first start, shows busy at once, and opens the session", async () => {
    screenClient.getFactoryManagerCard.mockResolvedValue(NOT_STARTED);
    let finish: (v: unknown) => void = () => {};
    screenClient.startFactoryManagerTalk.mockReturnValue(new Promise((r) => (finish = r)));
    show();

    fireEvent.change(await screen.findByTestId("fa-fm-computer"), { target: { value: "SOREN_NORTH" } });
    fireEvent.change(screen.getByTestId("fa-fm-folder"), { target: { value: "D:\\ReposFred\\factories" } });
    fireEvent.click(screen.getByTestId("fa-fm-talk"));

    expect(screen.getByTestId("fa-fm-talk").textContent).toBe("Starting the Factory Manager...");
    expect(screenClient.startFactoryManagerTalk).toHaveBeenCalledWith({ computer: "SOREN_NORTH", folder: "D:\\ReposFred\\factories" });
    finish({ sessionId: "s-9", href: "/session/s-9" });
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/session/s-9"));
  });

  it("once placed, sends no placement and shows where it runs", async () => {
    screenClient.getFactoryManagerCard.mockResolvedValue(RUNNING);
    screenClient.startFactoryManagerTalk.mockResolvedValue({ sessionId: "s-1", href: "/session/s-1" });
    show();

    expect(await screen.findByText("Runs on SOREN_NORTH in D:\\ReposFred\\factories")).toBeTruthy();
    expect(screen.queryByTestId("fa-fm-placement")).toBeNull();
    expect(screen.getByText("Every factory passes.")).toBeTruthy();
    fireEvent.click(screen.getByText("Open the Factory Manager"));

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/session/s-1"));
    expect(screenClient.startFactoryManagerTalk).toHaveBeenCalledWith(null);
  });

  it("shows the Gateway's refusal beside the button and lets the owner press again", async () => {
    screenClient.getFactoryManagerCard.mockResolvedValue(RUNNING);
    const sentence = "No Director is running on SOREN_NORTH, the computer the Factory Manager runs on, so it was not started.";
    screenClient.startFactoryManagerTalk.mockRejectedValue(new GatewayError(409, sentence, { reason: sentence }));
    show();

    fireEvent.click(await screen.findByTestId("fa-fm-talk"));

    expect((await screen.findByRole("alert")).textContent).toContain(sentence);
    expect(screen.getByTestId("fa-fm-talk").textContent).toBe("Open the Factory Manager");
  });
});
