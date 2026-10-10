// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { TeamQuestion, TeamQuestions } from "@devthrottle/client-core/teams/teamQuestionsClient";

// THE QUESTIONS PAGE (screen S8, devthrottle_internal#2307). What the person sees is the Gateway's: who asked, the
// options and the recommended mark, where their words go, and what became of an answer. Every word below is
// deliberately odd, so a label this page wrote for itself would fail. The page sends the choice and the words to ONE
// route; where each goes is the Gateway's doing, proven in the Gateway's own tests.

vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({
  useCurrentTeam: () => ({ current: { id: "team-dt", name: "DevThrottle", role: "Collaborator" } }),
}));

// The poll, fired by hand: the page reads once on mount as before, and a test can read again at the moment it chooses.
const polling = vi.hoisted(() => ({ refresh: null as null | ((signal: AbortSignal) => unknown) }));
vi.mock("@devthrottle/client-core/polling/useVisiblePolling", async () => {
  const react = await import("react");
  return {
    useVisiblePolling: (refresh: (signal: AbortSignal) => unknown) => {
      polling.refresh = refresh;
      react.useEffect(() => {
        void refresh(new AbortController().signal);
      }, [refresh]);
    },
  };
});

async function poll() {
  await act(async () => {
    await polling.refresh!(new AbortController().signal);
  });
}

const client = vi.hoisted(() => ({
  questions: null as unknown,
  answered: null as unknown,
  answerError: null as Error | null,
}));
vi.mock("@devthrottle/client-core/teams/teamQuestionsClient", () => ({
  getMyQuestions: vi.fn(async () => client.questions),
  answerQuestion: vi.fn(async () => {
    if (client.answerError) throw client.answerError;
    return client.answered;
  }),
}));

// The rail's count beside Questions is read again after an answer (review F8): recorded here, proven in the shell's tests.
vi.mock("../useTeamPageCounts", () => ({ refreshTeamPageCounts: vi.fn() }));

import { GATEWAY_UNREACHABLE_MESSAGE, GatewayError } from "@devthrottle/client-core/api/client";
import { resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";
import { refreshTeamPageCounts } from "../useTeamPageCounts";
import { answerQuestion, getMyQuestions } from "@devthrottle/client-core/teams/teamQuestionsClient";
import { QuestionsPage } from "./QuestionsPage";

const WAITING: TeamQuestion = {
  reportId: "r-1",
  version: 2,
  reportTitle: "Odd pricing~t",
  questionId: "trial-length",
  question: "Odd question 14 or 30~q",
  options: [
    { value: "14", label: "Odd fourteen~o", recommended: true },
    { value: "30", label: "Odd thirty~o", recommended: false },
  ],
  recommendedLabel: "odd-recommended~r",
  askedBy: "soren@odd.example",
  askedAtUtc: "2026-10-05T10:00:00",
  canAnswer: true,
  canComment: true,
  commentPlaceholder: "Odd anything to add~p",
  commentNote: "Odd: your words go to soren@odd.example, only your choice to the agent~n",
  sendLabel: "Odd send answer~s",
  answer: null,
};

const ANSWERED: TeamQuestion = {
  ...WAITING,
  canAnswer: false,
  canComment: false,
  answer: {
    optionValue: "30",
    optionLabel: "Odd thirty~o",
    chosenLabel: "Odd you chose thirty~c",
    statusLabel: "Odd held for the agent~h",
    atUtc: "2026-10-05T10:05:00",
    yourComment: "My own words, as typed",
    yourCommentLabel: "Odd your words went to soren~w",
  },
};

const LIST: TeamQuestions = {
  count: 1,
  subtitle: "Odd one waits on you~st",
  emptyText: "Odd nothing waits~e",
  waiting: [WAITING],
  answered: [],
  answeredHeading: "Odd answered~ah",
};

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/questions"]}>
      <QuestionsPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  client.questions = LIST;
  client.answered = ANSWERED;
  client.answerError = null;
  vi.mocked(answerQuestion).mockClear();
  vi.mocked(getMyQuestions).mockClear();
  vi.mocked(refreshTeamPageCounts).mockClear();
});

afterEach(() => cleanup());

describe("the questions waiting", () => {
  it("Card_ShowsTheGatewaysWords_WhoAskedTheOptionsAndWhereTheWordsGo", async () => {
    renderPage();

    const card = await screen.findByTestId("team-question");
    expect(screen.getByText("Odd one waits on you~st")).toBeTruthy();
    expect(card.textContent).toContain("Odd pricing~t");
    expect(card.textContent).toContain("by soren@odd.example");
    expect(card.textContent).toContain("Odd question 14 or 30~q");
    const options = within(card).getAllByTestId("team-question-option") as HTMLInputElement[];
    expect(options.map((o) => o.value)).toEqual(["14", "30"]);
    expect(card.textContent).toContain("Odd fourteen~o");
    expect(card.textContent).toContain("Odd thirty~o");
    expect(within(card).getAllByText("odd-recommended~r")).toHaveLength(1);
    expect(within(card).getByTestId("team-question-note").textContent).toBe(WAITING.commentNote);
    expect((within(card).getByTestId("team-question-comment") as HTMLTextAreaElement).placeholder).toBe("Odd anything to add~p");
    expect(within(card).getByTestId("team-question-send").textContent).toBe("Odd send answer~s");
  });

  it("Card_TheRecommendedOptionIsPicked_UntilThePersonPicksAnother", async () => {
    renderPage();

    const options = (await screen.findAllByTestId("team-question-option")) as HTMLInputElement[];
    expect(options.map((o) => o.checked)).toEqual([true, false]);
    fireEvent.click(options[1]);
    expect(options.map((o) => o.checked)).toEqual([false, true]);
  });

  it("Send_SendsTheChosenOptionAndTheWordsAsTyped_ForTheVersionShown_AndShowsWhatTheGatewayAnswered", async () => {
    renderPage();
    const options = (await screen.findAllByTestId("team-question-option")) as HTMLInputElement[];
    fireEvent.click(options[1]);
    fireEvent.change(screen.getByTestId("team-question-comment"), { target: { value: "  We trialled 14 days.\nIt was short.  " } });
    fireEvent.click(screen.getByTestId("team-question-send"));

    await waitFor(() => expect(answerQuestion).toHaveBeenCalledTimes(1));
    const [teamId, q, option, comment] = vi.mocked(answerQuestion).mock.calls[0];
    expect(teamId).toBe("team-dt");
    expect({ reportId: q.reportId, questionId: q.questionId, version: q.version }).toEqual({ reportId: "r-1", questionId: "trial-length", version: 2 });
    expect(option).toBe("30");
    expect(comment).toBe("  We trialled 14 days.\nIt was short.  ");
    const answer = await screen.findByTestId("team-question-answer");
    expect(answer.textContent).toContain("Odd you chose thirty~c");
    expect(within(answer).getByTestId("team-question-status").textContent).toBe("Odd held for the agent~h");
    expect(screen.queryByTestId("team-question-send")).toBeNull();
    // The rail's count is read again at once, so it never says 1 beside a page that says none.
    expect(refreshTeamPageCounts).toHaveBeenCalledTimes(1);
    // The page reads the Gateway again, so the lists are the Gateway's after an answer.
    await waitFor(() => expect(vi.mocked(getMyQuestions).mock.calls.length).toBeGreaterThanOrEqual(2));
  });

  it("Send_WithNoWords_SendsAnEmptyComment", async () => {
    renderPage();
    fireEvent.click(await screen.findByTestId("team-question-send"));

    await waitFor(() => expect(answerQuestion).toHaveBeenCalledTimes(1));
    expect(vi.mocked(answerQuestion).mock.calls[0].slice(2, 4)).toEqual(["14", ""]);
  });

  it("Send_Refused_ShowsTheGatewaysSentence_KeepsTheWords_AndReadsAgain", async () => {
    client.answerError = new GatewayError(409, "conflict", { reason: "Odd: the session that asked has ended~x" });
    renderPage();
    fireEvent.change(await screen.findByTestId("team-question-comment"), { target: { value: "keep me" } });
    fireEvent.click(screen.getByTestId("team-question-send"));

    expect((await screen.findByTestId("team-question-error")).textContent).toContain("Odd: the session that asked has ended~x");
    expect((screen.getByTestId("team-question-comment") as HTMLTextAreaElement).value).toBe("keep me");
    await waitFor(() => expect(vi.mocked(getMyQuestions).mock.calls.length).toBeGreaterThanOrEqual(2));
  });

  it("Card_TheGatewaySaysNoComment_OffersNoBox_AndSaysWhy", async () => {
    client.questions = { ...LIST, waiting: [{ ...WAITING, canComment: false, commentNote: "Odd: the asker cannot read words~cn" }] };
    renderPage();

    const card = await screen.findByTestId("team-question");
    expect(within(card).queryByTestId("team-question-comment")).toBeNull();
    expect(within(card).getByTestId("team-question-note").textContent).toBe("Odd: the asker cannot read words~cn");
    expect((within(card).getByTestId("team-question-send") as HTMLButtonElement).disabled).toBe(false);
  });

  it("Card_TheGatewaySaysItCannotBeAnswered_TheSendIsOff", async () => {
    client.questions = { ...LIST, waiting: [{ ...WAITING, canAnswer: false }] };
    renderPage();

    expect(((await screen.findByTestId("team-question-send")) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("nothing waiting, and what was answered", () => {
  it("Empty_SaysTheGatewaysWords", async () => {
    client.questions = { ...LIST, count: 0, subtitle: "Odd none~st0", waiting: [] };
    renderPage();

    expect(await screen.findByText("Odd nothing waits~e")).toBeTruthy();
    expect(screen.getByText("Odd none~st0")).toBeTruthy();
    expect(screen.queryByTestId("team-question-send")).toBeNull();
  });

  it("Answered_ShowsTheChoiceItsStateAndTheWordsReadBackToTheirWriter", async () => {
    client.questions = { ...LIST, count: 0, waiting: [], answered: [ANSWERED] };
    renderPage();

    const answered = await screen.findByTestId("team-questions-answered");
    expect(answered.textContent).toContain("Odd answered~ah");
    expect(answered.textContent).toContain("Odd you chose thirty~c");
    expect(within(answered).getByTestId("team-question-status").textContent).toBe("Odd held for the agent~h");
    const words = within(answered).getByTestId("team-question-your-comment");
    expect(words.textContent).toContain("Odd your words went to soren~w");
    expect(words.textContent).toContain("My own words, as typed");
    expect(within(answered).queryByTestId("team-question-option")).toBeNull();
  });

  it("Poll_AHeldAnswerThatBecomesDelivered_ShowsTheGatewaysNewState", async () => {
    client.questions = { ...LIST, count: 0, waiting: [], answered: [ANSWERED] };
    renderPage();
    expect((await screen.findByTestId("team-question-status")).textContent).toBe("Odd held for the agent~h");

    const delivered = { ...ANSWERED, answer: { ...ANSWERED.answer!, statusLabel: "Odd delivered to the agent~d" } };
    client.questions = { ...LIST, count: 0, waiting: [], answered: [delivered] };
    await poll();

    expect(screen.getByTestId("team-question-status").textContent).toBe("Odd delivered to the agent~d");
  });

  it("Poll_ANewerVersionOfTheQuestion_StartsAFreshCard_NoChoiceOrWordsCarriedOver", async () => {
    renderPage();
    const before = (await screen.findAllByTestId("team-question-option")) as HTMLInputElement[];
    fireEvent.click(before[1]);
    fireEvent.change(screen.getByTestId("team-question-comment"), { target: { value: "words for version 2" } });

    const v3: TeamQuestion = {
      ...WAITING,
      version: 3,
      options: [
        { value: "7", label: "Odd seven~o", recommended: true },
        { value: "14", label: "Odd fourteen~o", recommended: false },
      ],
    };
    client.questions = { ...LIST, waiting: [v3] };
    await poll();

    const after = screen.getAllByTestId("team-question-option") as HTMLInputElement[];
    expect(after.map((o) => [o.value, o.checked])).toEqual([["7", true], ["14", false]]);
    expect((screen.getByTestId("team-question-comment") as HTMLTextAreaElement).value).toBe("");
    fireEvent.click(screen.getByTestId("team-question-send"));
    await waitFor(() => expect(answerQuestion).toHaveBeenCalledTimes(1));
    const [, q, option, comment] = vi.mocked(answerQuestion).mock.calls[0];
    expect([q.version, option, comment]).toEqual([3, "7", ""]);
  });

  it("List_AFailedRead_SaysWhy", async () => {
    vi.mocked(getMyQuestions).mockRejectedValueOnce(new GatewayError(500, "fault", { reason: "the Gateway fell over" }));
    renderPage();

    expect(await screen.findByText(/the Gateway fell over/)).toBeTruthy();
  });
});

describe("the poll during an outage (the Error Logging mission, issue #3675, rulings R1 and R8)", () => {
  // The page reads every few seconds while it is open. A failure that does not change is one report per outage, not
  // one per round, and the page keeps the shared retrying line; a good read ends the outage, so the next failure is
  // reported again.
  const posted: string[] = [];

  beforeEach(() => {
    posted.length = 0;
    resetReportingForTests();
    setReportingComponent("cockpit");
    vi.stubGlobal(
      "fetch",
      vi.fn(async (_url: unknown, init?: RequestInit) => {
        posted.push(String(init?.body));
        return new Response(null, { status: 204 });
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetReportingForTests();
  });

  it("poll_SameFailureEveryRound_ReportedOncePerOutage_AndAgainAfterAGoodRead", async () => {
    const outage = new GatewayError(502, "GET failed: 502");
    vi.mocked(getMyQuestions).mockRejectedValue(outage);
    renderPage();

    expect(await screen.findByText(GATEWAY_UNREACHABLE_MESSAGE)).toBeTruthy();
    await poll();
    await poll();
    await vi.waitFor(() => expect(posted).toHaveLength(1));
    expect(JSON.parse(posted[0])).toMatchObject({ surface: "cockpit-team-questions", action: "load your questions", user_visible: true });

    vi.mocked(getMyQuestions).mockResolvedValue(LIST);
    await poll();
    vi.mocked(getMyQuestions).mockRejectedValue(outage);
    await poll();
    await poll();

    await vi.waitFor(() => expect(posted).toHaveLength(2));
    await new Promise((r) => setTimeout(r, 20));
    expect(posted).toHaveLength(2);
  });
});
