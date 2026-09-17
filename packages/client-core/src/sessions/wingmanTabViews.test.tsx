// Which view the Wingman tab shows, and the temporary bridge while the Now route is still being built.
//
// What these prove: Now is the default view and is fed by GET /sessions/{sid}/wingman-now; a 404 from that route
// leaves the version 1 stops list on screen ALONE, with no view buttons, so nothing is worse than it was; any other
// refusal is shown as the Gateway's own sentence and never as an older screen; History is reachable beside Now once
// the route answers; and the actions the shell hands in reach the view.
//
// A tab that made Now the second view, that swallowed a real refusal into the bridge, or that dropped History when
// the route landed goes red here.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { WingmanTab } from "./WingmanTab";
import type { WingmanNow } from "./wingmanNowRead";

const SID = "9a1c4d20-0000-4000-8000-000000000050";

const NOW: WingmanNow = {
  state: "needs-you",
  pillText: "Needs you",
  pillColour: "red",
  pillColourHex: "#ef4444",
  unsure: false,
  unsureTag: null,
  unsureLine: null,
  when: { lead: "Stopped at", atUtc: "2026-09-17T11:12:00Z", showAgo: true },
  showWhyColour: true,
  headline: "Merge pull request #3002, or allow me to merge it",
  story: "The release notes are pushed.",
  agentSaid: null,
  wholeReply: null,
  needs: null,
  canAnswerByOption: false,
  replyPlaceholder: "Answer the session directly.",
  calmCard: null,
  carryingOnDeadline: null,
  lastWords: null,
  failedHeadline: null,
  failedStory: null,
  lastGood: null,
  switchedOff: null,
  lastAsked: null,
  answered: null,
  lastStop: null,
  nextNeedsYou: null,
  voice: { kind: "none", label: null, afterTurnOnText: null },
};

const STOPS = {
  sessionId: SID,
  stops: [],
  groups: [{ key: "all", label: "All", count: 0 }],
};

let urls: string[] = [];

/** Answers each route with its own status and body, so one read can 404 while the other succeeds. */
function fakeGateway(routes: Record<string, [number, unknown]>) {
  urls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      urls.push(url);
      const hit = Object.keys(routes).find((k) => url.includes(k));
      const [status, body] = hit ? routes[hit] : [500, { error: "no route in this test" }];
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Wingman tab's views", () => {
  it("opens on Now, fed by the Now route, with History beside it", async () => {
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS] });
    render(<WingmanTab sessionId={SID} />);

    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());
    expect(urls[0]).toBe(`/sessions/${SID}/wingman-now`);

    const views = screen.getAllByRole("tab");
    expect(views.map((v) => v.textContent)).toEqual(["Now", "History"]);
    expect(views[0].getAttribute("aria-selected")).toBe("true");
  });

  it("shows History when it is chosen, and comes back to Now", async () => {
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS] });
    render(<WingmanTab sessionId={SID} />);
    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "History" }));
    await waitFor(() =>
      expect(screen.getByText("The Wingman has not judged this session in the last seven days.")).toBeTruthy(),
    );
    expect(screen.queryByText("Merge pull request #3002, or allow me to merge it")).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: "Now" }));
    expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy();
  });

  it("comes back to Now when the screen moves to another session, rather than staying on History", async () => {
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS] });
    const { rerender } = render(<WingmanTab sessionId={SID} />);
    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "History" }));
    await waitFor(() =>
      expect(screen.getByText("The Wingman has not judged this session in the last seven days.")).toBeTruthy(),
    );

    rerender(<WingmanTab sessionId="4c7e1b90-0000-4000-8000-000000000051" />);
    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());
    expect(screen.getByRole("tab", { name: "Now" }).getAttribute("aria-selected")).toBe("true");
  });

  // THE BRIDGE. Delete this test with the bridge itself when the Now route ships everywhere.
  it("leaves the version 1 stops list on screen alone while the Gateway does not serve Now yet", async () => {
    fakeGateway({ "wingman-now": [404, { error: "not found" }], "wingman-stops": [200, STOPS] });
    render(<WingmanTab sessionId={SID} />);

    await waitFor(() =>
      expect(screen.getByText("The Wingman has not judged this session in the last seven days.")).toBeTruthy(),
    );
    // No view buttons: the tab is exactly the screen that shipped, not a half-built version 3.
    expect(screen.queryAllByRole("tab")).toEqual([]);
    expect(urls).toContain(`/sessions/${SID}/wingman-stops`);
  });

  it("shows a refusal that is NOT a missing route as the Gateway's own sentence, never as an older screen", async () => {
    fakeGateway({
      "wingman-now": [403, { error: "This session belongs to another account." }],
      "wingman-stops": [200, STOPS],
    });
    render(<WingmanTab sessionId={SID} />);

    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe("This session belongs to another account."),
    );
    expect(screen.queryByText("The Wingman has not judged this session in the last seven days.")).toBeNull();
  });

  it("says it is loading rather than showing a blank tab", () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(() => {})));
    render(<WingmanTab sessionId={SID} />);
    expect(screen.getByRole("status").textContent).toBe("Loading this session's live stop...");
  });

  it("hands the shell's actions to Now, so what the shell did not wire is not drawn", async () => {
    const onSendReply = vi.fn();
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS] });
    render(<WingmanTab sessionId={SID} actions={{ onSendReply }} />);

    await waitFor(() => expect(screen.getByLabelText("Your reply to this session")).toBeTruthy());
    fireEvent.change(screen.getByLabelText("Your reply to this session"), { target: { value: "go ahead" } });
    fireEvent.click(screen.getByText("Send"));
    expect(onSendReply).toHaveBeenCalledWith("go ahead");

    // Nothing wired the terminal or the colour explanation, so neither is offered.
    expect(screen.queryByText("Open the terminal")).toBeNull();
    expect(screen.queryByText("Why this colour?")).toBeNull();
  });
});
