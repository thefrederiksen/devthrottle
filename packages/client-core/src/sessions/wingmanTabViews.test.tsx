// Which view the Wingman tab shows, and what it does when a read of Now fails.
//
// What these prove: Now is the default view and is fed by GET /sessions/{sid}/wingman-now; EVERY refusal is shown as
// the Gateway's own sentence and never as an older screen, including a 404 - there is no bridge left that could read
// a genuine "no such session" as "no Now here"; History is reachable beside Now at all times, including while the Now
// read is failing; a failed BACKGROUND refresh keeps the last good screen and everything typed into it; and the
// actions the shell hands in reach the view.
//
// A tab that made Now the second view, that swallowed a refusal, that dropped History, or that let one bad poll wipe
// the screen and the owner's half-typed reply goes red here.
import { afterEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { WingmanTab } from "./WingmanTab";
import type { WingmanNow } from "./wingmanNowRead";

const SID = "9a1c4d20-0000-4000-8000-000000000050";

// The shell's ONE message box, stood in for: a box that keeps what is typed into it, which is all these tests need
// of it. The real one is the Cockpit's composer, and the draft it keeps lives further up still - in the page - so it
// survives strictly more than this does. What is proven here is that the TAB does not throw the box away under him.
function StandInBox({ placeholder }: { placeholder: string }) {
  const [text, setText] = useState("");
  return (
    <textarea
      aria-label="Your reply to this session"
      placeholder={placeholder}
      value={text}
      onChange={(e) => setText(e.target.value)}
    />
  );
}

const replyBox = (placeholder: string) => <StandInBox placeholder={placeholder} />;

const NOW: WingmanNow = {
  sessionId: SID,
  sessionLine: "112 - Cube Data and Projects - Architect",
  state: "needs-you",
  pillText: "Needs you",
  pillColour: "red",
  pillColourHex: "#ef4444",
  unsure: false,
  unsureTag: null,
  unsureLine: null,
  when: { lead: "Stopped at", atUtc: "2026-09-17T11:12:00Z", showAgo: true, elapsedOnly: false },
  showWhyColour: true,
  headline: "Merge pull request #3002, or allow me to merge it",
  story: "The release notes are pushed.",
  agentSaid: null,
  wholeReply: null,
  needs: null,
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
    // The Now route is what feeds the open view. It is not asserted to be the FIRST request the tab makes:
    // the tab also asks once whether this account is staff, to decide whether a Debug view is offered at all.
    expect(urls).toContain(`/sessions/${SID}/wingman-now`);

    const views = screen.getAllByRole("tab");
    expect(views.map((v) => v.textContent)).toEqual(["Now", "History"]);
    expect(views[0].getAttribute("aria-selected")).toBe("true");
  });

  it("offers no Debug view to an account that is not staff, and none when the staff question cannot be answered", async () => {
    // The status route answers, and says this account is not staff.
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS], "account/status": [200, { hosted: true, staff: false }] });
    render(<WingmanTab sessionId={SID} />);
    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());
    expect(screen.getAllByRole("tab").map((v) => v.textContent)).toEqual(["Now", "History"]);

    cleanup();

    // And when the question cannot be answered at all, the view is CLOSED rather than opened: a debug view that
    // appears because a status call failed would be the failure this gate exists to stop.
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS], "account/status": [500, { error: "no" }] });
    render(<WingmanTab sessionId={SID} />);
    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());
    expect(screen.getAllByRole("tab").map((v) => v.textContent)).toEqual(["Now", "History"]);
  });

  it("offers the Debug view to a staff account, beside Now and History", async () => {
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS], "account/status": [200, { hosted: true, staff: true }] });
    render(<WingmanTab sessionId={SID} />);
    await waitFor(() => expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy());

    await waitFor(() => expect(screen.getAllByRole("tab")).toHaveLength(3));
    expect(screen.getAllByRole("tab").map((v) => v.textContent)).toEqual(["Now", "History", "Debug"]);
    // It is offered, not opened: the tab still comes up on Now.
    expect(screen.getAllByRole("tab")[0].getAttribute("aria-selected")).toBe("true");
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

  it("shows a refusal as the Gateway's own sentence, never as an older screen", async () => {
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

  // THE BRIDGE IS GONE. It read a bare 404 as "this Gateway does not serve Now yet" and quietly showed the version 1
  // list. The route's own genuine 404s - another account's session, an unknown one - would have taken that same path,
  // so the owner would have been shown an older screen instead of being told the session could not be found.
  it("shows a 404 as the Gateway's own sentence too, rather than quietly showing the version 1 list", async () => {
    fakeGateway({
      "wingman-now": [404, { error: "That session could not be found." }],
      "wingman-stops": [200, STOPS],
    });
    render(<WingmanTab sessionId={SID} />);

    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("That session could not be found."));
    expect(screen.queryByText("The Wingman has not judged this session in the last seven days.")).toBeNull();
  });

  it("keeps History reachable while the Now read is failing", async () => {
    fakeGateway({
      "wingman-now": [403, { error: "This session belongs to another account." }],
      "wingman-stops": [200, STOPS],
    });
    render(<WingmanTab sessionId={SID} />);
    await waitFor(() => expect(screen.getByRole("alert")).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "History" }));
    await waitFor(() =>
      expect(screen.getByText("The Wingman has not judged this session in the last seven days.")).toBeTruthy(),
    );
  });

  it("keeps the last good screen, and the owner's half-typed reply, when a background refresh fails", async () => {
    vi.useFakeTimers();
    try {
      // The Now route answers well once, then starts failing - a deploy, a dropped connection, anything.
      let answer: [number, unknown] = [200, NOW];
      vi.stubGlobal(
        "fetch",
        vi.fn(async (url: string) => {
          const [status, body] = url.includes("wingman-now") ? answer : [200, STOPS];
          return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
        }),
      );

      render(<WingmanTab sessionId={SID} replyBox={replyBox} />);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(0);
      });

      const box = screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement;
      fireEvent.change(box, { target: { value: "allow the merge, tag straight after it" } });

      answer = [502, { error: "The Gateway is restarting." }];
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });

      // The screen is still the last good one, the draft is still in the box, and the failure is one quiet line.
      expect(screen.getByText("Merge pull request #3002, or allow me to merge it")).toBeTruthy();
      expect((screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement).value).toBe(
        "allow the merge, tag straight after it",
      );
      expect(screen.getByRole("status").textContent).toContain("The Gateway is restarting.");

      // And the next good refresh takes the line away again - and still does not touch the draft. A refresh of the
      // same session must never remount the screen the owner is typing into.
      answer = [200, NOW];
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(screen.queryByRole("status")).toBeNull();
      expect((screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement).value).toBe(
        "allow the merge, tag straight after it",
      );
    } finally {
      vi.useRealTimers();
    }
  });

  it("never carries a draft over to another session", async () => {
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS] });
    const { rerender } = render(<WingmanTab sessionId={SID} replyBox={replyBox} />);
    await waitFor(() => expect(screen.getByLabelText("Your reply to this session")).toBeTruthy());

    fireEvent.change(screen.getByLabelText("Your reply to this session"), {
      target: { value: "meant for the first session" },
    });

    rerender(<WingmanTab sessionId="4c7e1b90-0000-4000-8000-000000000051" replyBox={replyBox} />);
    await waitFor(() =>
      expect((screen.getByLabelText("Your reply to this session") as HTMLTextAreaElement).value).toBe(""),
    );
  });

  it("says it is loading rather than showing a blank tab", () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(() => {})));
    render(<WingmanTab sessionId={SID} />);
    expect(screen.getByRole("status").textContent).toBe("Loading this session's live stop...");
  });

  it("hands the shell's actions to Now, chosen from the answer, so what the shell did not wire is not drawn", async () => {
    const onOpenTerminal = vi.fn();
    // The shell is handed the Gateway's answer and decides from it. Proving it that way round - rather than with a
    // fixed set of actions - is what makes a state-fitting quick action possible at all.
    const actions = vi.fn((now: WingmanNow) => ({ onOpenTerminal: now.state === "needs-you" ? onOpenTerminal : undefined }));
    fakeGateway({ "wingman-now": [200, NOW], "wingman-stops": [200, STOPS] });
    render(<WingmanTab sessionId={SID} actions={actions} replyBox={replyBox} />);

    await waitFor(() => expect(screen.getByText("Open the terminal")).toBeTruthy());
    expect(actions).toHaveBeenCalledWith(expect.objectContaining({ state: "needs-you" }));
    fireEvent.click(screen.getByText("Open the terminal"));
    expect(onOpenTerminal).toHaveBeenCalled();

    // Nothing wired the snooze or the colour explanation, so neither is offered.
    expect(screen.queryByText("Snooze this session")).toBeNull();
    expect(screen.queryByText("Why this colour?")).toBeNull();
  });
});
