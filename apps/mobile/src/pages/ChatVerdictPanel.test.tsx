// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useNavigate } from "react-router-dom";

// THE PHONE MOUNTS THE VERDICT PANEL (the Wingman-on-every-turn mission, slice E). The panel lives once in
// client-core and the phone session screen only mounts it, fed by the roster poll useSessionManage already runs.
// This drives the REAL Chat page and the REAL hook against a roster holding a judged row, so removing either the
// mount or the hook's session goes red.

const SID = "3a8e5d21-0000-4000-8000-000000000003";

const judged = {
  sessionId: SID,
  name: "scratch - migration",
  activityState: "WaitingForInput",
  effectiveColor: "red",
  stateLabel: "Apply the migration now?",
  triageBucket: "needsYou",
  agent: "Codex",
  agentToolDisplay: "Codex",
  verdictState: "judged",
  turnVerdict: {
    verdictId: "tv-phone-1",
    verdict: "needed-you",
    confidence: "high",
    evidence: "Apply the migration to the local database now?",
    label: "Apply the migration now?",
    summary: "The session is asking before it changes the database.",
    answerVia: "keys",
    menu: { question: "Apply the migration now?", selectionMode: "single", submit: "" },
    options: [
      { key: "Yes, apply it", send: "1", recommended: true, note: "Changes the local database." },
      { key: "No, leave it", send: "2", recommended: false, note: "Nothing changes." },
    ],
    risk: "spends-money",
  },
};

// What the next roster read answers. A test swaps it to hold a read pending, fail it, or leave a session out.
let rosterRead: () => Promise<unknown[]> = () => Promise.resolve([judged]);
const gatewayFetchMock = vi.fn();

vi.mock("@devthrottle/client-core/api/client", () => ({
  listSessions: () => rosterRead(),
  holdSession: vi.fn(),
  stopSession: vi.fn(),
  gatewayErrorMessage: (err: unknown) => String(err),
  gatewayFetch: (...args: unknown[]) => gatewayFetchMock(...args),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: () => ({
    bubbles: [],
    emptyText: "No conversation yet.",
    staleNotice: null,
    loadFailed: false,
    filter: { showToolCalls: false, showToolResults: false, showThinking: false },
    setFilter: () => {},
  }),
}));

// The screen's other regions reach for a microphone, a socket or the navigation drawer and are not the subject.
vi.mock("@devthrottle/client-core/dictation/DictationStatusStrip", () => ({ DictationStatusStrip: () => null }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => <div /> }));
vi.mock("../components/SessionControls", () => ({ SessionControls: () => <div /> }));
vi.mock("../components/ViewTabs", () => ({ ViewTabs: () => <div /> }));

import { Chat } from "./Chat";

afterEach(() => {
  cleanup();
  rosterRead = () => Promise.resolve([judged]);
  gatewayFetchMock.mockReset();
});

const A = SID;
const B = "3a8e5d21-0000-4000-8000-000000000004";

function judgedFor(sessionId: string, verdictId: string) {
  return { ...judged, sessionId, turnVerdict: { ...judged.turnVerdict, verdictId } };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

// The SAME Chat instance moves between routes, exactly as a tap on another session does.
let navigateTo: ((to: string) => void) | null = null;
function NavigationHandle() {
  navigateTo = useNavigate();
  return null;
}

function renderChatAt(sessionId: string) {
  return render(
    <MemoryRouter initialEntries={[`/session/${sessionId}`]}>
      <NavigationHandle />
      <Routes>
        <Route path="/session/:sessionId" element={<Chat />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("the phone session screen", () => {
  it("mounts the shared verdict panel for a judged row from the roster poll", async () => {
    render(
      <MemoryRouter initialEntries={[`/session/${SID}`]}>
        <Routes>
          <Route path="/session/:sessionId" element={<Chat />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByRole("region", { name: "Wingman verdict" })).toBeTruthy();
    expect(screen.getByText("Codex said")).toBeTruthy();
    expect(screen.getByRole("note").textContent).toBe("Risk: spends-money");
    expect(screen.getByRole("button", { name: "Yes, apply it" })).toBeTruthy();
  });

  // The inspector's browser probe for finding 2, kept as the test. It rendered A, moved the same Chat to B while B's
  // roster read was pending, pressed the option still on the screen, and saw the answer posted to A's session id.
  it("drops A's panel the moment the route moves to B while B's read is pending, and answers B with B's own id", async () => {
    rosterRead = () => Promise.resolve([judgedFor(A, "tv-a")]);
    renderChatAt(A);
    expect(await screen.findByRole("button", { name: "Yes, apply it" })).toBeTruthy();

    const pending = deferred<unknown[]>();
    rosterRead = () => pending.promise;
    act(() => navigateTo!(`/session/${B}`));

    expect(screen.queryByRole("region", { name: "Wingman verdict" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Yes, apply it" })).toBeNull();

    gatewayFetchMock.mockResolvedValue(
      new Response(JSON.stringify({ accepted: true, code: "owner-answered", reason: "Sent to the session." }), { status: 200 }),
    );
    await act(async () => {
      pending.resolve([judgedFor(A, "tv-a"), judgedFor(B, "tv-b")]);
    });
    fireEvent.click(await screen.findByRole("button", { name: "Yes, apply it" }));

    await waitFor(() => expect(gatewayFetchMock).toHaveBeenCalledTimes(1));
    expect(gatewayFetchMock.mock.calls[0][0]).toBe(`/sessions/${B}/turn-verdict/answer`);
    expect(JSON.parse(String((gatewayFetchMock.mock.calls[0][1] as RequestInit).body))).toEqual({
      verdictId: "tv-b",
      optionIndexes: [0],
    });
  });

  it("shows no panel and says why when the roster read for the new route fails", async () => {
    rosterRead = () => Promise.resolve([judgedFor(A, "tv-a")]);
    renderChatAt(A);
    expect(await screen.findByRole("button", { name: "Yes, apply it" })).toBeTruthy();

    rosterRead = () => Promise.reject(new Error("The Gateway did not answer."));
    await act(async () => {
      navigateTo!(`/session/${B}`);
    });

    expect(await screen.findByText(/Could not read the roster.*The Gateway did not answer\./)).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Wingman verdict" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Yes, apply it" })).toBeNull();
  });

  it("shows no panel and says why when the roster does not hold this session", async () => {
    rosterRead = () => Promise.resolve([judgedFor(A, "tv-a")]);
    renderChatAt(B);

    expect(await screen.findByText("This session is not on the roster right now, so there is nothing here to answer.")).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Wingman verdict" })).toBeNull();
  });
});
