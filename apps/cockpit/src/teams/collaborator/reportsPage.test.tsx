// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import type { DevReportSnapshot } from "@devthrottle/client-core/devreports/controller";
import type { OwnReportDetail, ReceivedReportDetail, ReceivedReports } from "@devthrottle/client-core/teams/teamReportsClient";

// THE TEAM'S REPORTS PAGE (screen S10, devthrottle_internal#2309). What the reader sees is the Gateway's: the rows, who
// each is from, New or Read, where a comment goes, who a report can still be sent to. Every word below is deliberately
// odd, so a label this page wrote for itself would fail. The report itself opens in the ONE shared viewer, fed from the
// team's routes - the controller is scripted here, so the test reads what the page gave the viewer and what it drew.

vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({
  useCurrentTeam: () => ({ current: { id: "team-dt", name: "DevThrottle", role: "Collaborator" } }),
}));

const client = vi.hoisted(() => ({
  received: null as unknown,
  own: null as unknown,
  receivedDetail: null as unknown,
  ownDetail: null as unknown,
  sendError: null as Error | null,
}));
vi.mock("@devthrottle/client-core/teams/teamReportsClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getReportsSentToMe: vi.fn(async () => client.received),
  getMyTeamReports: vi.fn(async () => client.own),
  // A fresh object per read, as a real answer parsed from the wire is: the page must not depend on object identity.
  getReportSentToMe: vi.fn(async () => (client.receivedDetail === null ? null : { ...(client.receivedDetail as object) })),
  getMyTeamReport: vi.fn(async () => client.ownDetail),
  markReportRead: vi.fn(async () => {}),
  commentOnReport: vi.fn(async (_t: string, _r: string, text: string) => {
    if (client.sendError) throw client.sendError;
    return { id: "c-new", text, atUtc: "2026-10-02T14:05:00" };
  }),
  sendMyTeamReport: vi.fn(async () => {
    if (client.sendError) throw client.sendError;
    return client.ownDetail;
  }),
}));

// The shared viewer's controller, scripted: the options the page gave it are kept, and its snapshot carries a record so
// the viewer draws the panel beside the report.
const viewer = vi.hoisted(() => ({ options: [] as { reportId: string; script: string; api: { getHtml: (id: string, v: number) => Promise<unknown> } }[] }));
vi.mock("@devthrottle/client-core/devreports/controller", () => ({
  DevReportController: class {
    private readonly snapshot: DevReportSnapshot;
    constructor(options: { reportId: string; script: string; api: { getHtml: (id: string, v: number) => Promise<unknown> } }) {
      viewer.options.push(options);
      this.snapshot = {
        detail: { report: { id: options.reportId, sessionId: "", key: "", title: "Odd title 41", status: "status~odd", version: 3,
          publishedAtUtc: "", updatedAtUtc: "", sessionEnded: false, openItems: 0 }, items: [], replies: [] },
        notFound: false,
        loadError: null,
        loadedVersion: 3,
        pageState: { queued: [], sent: [], replies: [], draft: null, answerDrafts: [], scroll: { x: 0, y: 0 } },
        connected: true,
        sending: false,
        sendError: null,
        noteMode: { picking: false, selectionQuote: null },
      };
    }
    subscribe = () => () => {};
    getSnapshot = () => this.snapshot;
    refresh = async () => {};
    sendQueued = async () => {};
    setNoteMode = () => {};
    dispose = () => {};
  },
}));

import { GatewayError } from "@devthrottle/client-core/api/client";
import { DEV_REPORT_ANSWERS_OFF_SCRIPT } from "@devthrottle/client-core/devreports/answersOffScript";
import {
  commentOnReport,
  getMyTeamReport,
  getMyTeamReports,
  getReportSentToMe,
  markReportRead,
  sendMyTeamReport,
} from "@devthrottle/client-core/teams/teamReportsClient";
import { ReportsPage } from "./ReportsPage";

const RECEIVED: ReceivedReports = {
  count: 2,
  emptyText: "Odd empty words 7",
  showYourReports: false,
  reports: [
    { id: "r-new", title: "Signup page rewrite", status: "waiting-on-you", version: 1, from: "soren@odd.example",
      sentAtUtc: "2026-10-02T09:00:00", read: false, readLabel: "Fresh~odd", questionsLabel: "Odd 1 question waits~q" },
    { id: "r-old", title: "September conversion numbers", status: "done", version: 2, from: "priya@odd.example",
      sentAtUtc: "2026-09-30T09:00:00", read: true, readLabel: "Seen~odd", questionsLabel: null },
  ],
};

const RECEIVED_DETAIL: ReceivedReportDetail = {
  report: RECEIVED.reports[0],
  comments: [{ id: "c1", text: "Earlier words of mine", atUtc: "2026-10-02T10:00:00", aboutLabel: null }],
  canComment: true,
  commentsNote: "Odd note: your comments go to soren@odd.example and never to an agent.",
  notesOpen: false,
  answersOpen: false,
};

const OWN_DETAIL: OwnReportDetail = {
  report: { id: "y1", title: "My report", status: "done", version: 3, publishedAtUtc: "2026-10-01T09:00:00", updatedAtUtc: "2026-10-02T09:00:00" },
  recipients: [{ memberId: "m-mike", name: "mike@odd.example", sentAtUtc: "2026-10-02T09:00:00", sentVersion: 2,
    versionLabel: "Odd holds 2 of 3", read: true, readLabel: "Opened~odd" }],
  recipientsEmptyText: "Odd nobody yet 4",
  choices: [
    { memberId: "m-nina", name: "nina@odd.example", role: "Collaborator", heldLabel: null },
    { memberId: "m-bob", name: "bob@odd.example", role: "Developer", heldLabel: null },
    { memberId: "m-mike", name: "mike@odd.example", role: "Collaborator", heldLabel: "Odd gets version 3" },
  ],
  sendNote: "Odd send note 3",
  comments: [{ id: "c9", from: "mike@odd.example", text: "A comment from Mike, for you", atUtc: "2026-10-02T11:00:00",
    aboutLabel: "Odd about the trial~q" }],
  commentsEmptyText: "Odd no comments 5",
  notesOpen: false,
  answersOpen: false,
};

function Where() {
  const l = useLocation();
  return <div data-testid="where">{l.pathname + l.search}</div>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Where />
      <ReportsPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  client.received = RECEIVED;
  client.own = { count: 0, reports: [], emptyText: "Odd none of yours 2" };
  client.receivedDetail = RECEIVED_DETAIL;
  client.ownDetail = OWN_DETAIL;
  client.sendError = null;
  viewer.options = [];
  vi.mocked(markReportRead).mockClear();
  vi.mocked(commentOnReport).mockClear();
  vi.mocked(sendMyTeamReport).mockClear();
  vi.mocked(getMyTeamReports).mockClear();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setVisibility("visible");
});

// The page reads the Gateway again on its poll, and also when it becomes visible again after being hidden - which is
// how a test asks for the next poll without waiting five seconds.
let visibility: DocumentVisibilityState = "visible";
Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
function setVisibility(next: DocumentVisibilityState) {
  visibility = next;
}
async function nextPoll() {
  await act(async () => {
    setVisibility("hidden");
    document.dispatchEvent(new Event("visibilitychange"));
    setVisibility("visible");
    document.dispatchEvent(new Event("visibilitychange"));
  });
}

describe("the list", () => {
  it("List_ShowsWhatWasSent_InTheGatewaysOrder_WithFromWhenAndReadAsSent", async () => {
    renderAt("/reports");

    const rows = await screen.findAllByTestId("team-report-row");
    expect(rows.map((r) => r.getAttribute("data-report-id"))).toEqual(["r-new", "r-old"]);
    expect(rows[0].textContent).toContain("Signup page rewrite");
    expect(rows[0].textContent).toContain("From soren@odd.example");
    expect(rows[0].textContent).toContain("2 Oct");
    expect(within(rows[0]).getByTestId("team-report-read").textContent).toBe("Fresh~odd");
    expect(within(rows[1]).getByTestId("team-report-read").textContent).toBe("Seen~odd");
  });

  it("List_SaysHowManyQuestionsWaitOnTheReader_InTheGatewaysWords_OnlyWhereTheGatewaySaysSo", async () => {
    renderAt("/reports");

    const rows = await screen.findAllByTestId("team-report-row");
    expect(within(rows[0]).getByTestId("team-report-questions-label").textContent).toBe("Odd 1 question waits~q");
    expect(within(rows[1]).queryByTestId("team-report-questions-label")).toBeNull();
  });

  it("List_NothingSent_SaysTheGatewaysWords", async () => {
    client.received = { ...RECEIVED, count: 0, reports: [] };
    renderAt("/reports");

    expect(await screen.findByText("Odd empty words 7")).toBeTruthy();
  });

  it("List_TheGatewaySaysNoReportsOfYourOwn_ShowsNoneAndAsksForNone", async () => {
    renderAt("/reports");

    await screen.findAllByTestId("team-report-row");
    expect(screen.queryByTestId("team-reports-own")).toBeNull();
    expect(getMyTeamReports).not.toHaveBeenCalled();
  });

  it("List_TheGatewaySaysYouHaveReports_ListsThemWithWhoTheyWentTo", async () => {
    client.received = { ...RECEIVED, showYourReports: true };
    client.own = {
      count: 1,
      emptyText: "unused",
      reports: [{ id: "y1", title: "My report", status: "done", version: 3, updatedAtUtc: "2026-10-02T09:00:00",
        sentToLabel: "Odd sent to 2", commentsLabel: "Odd 1 comment" }],
    };
    renderAt("/reports");

    const own = await screen.findByTestId("team-reports-own");
    expect(own.textContent).toContain("Your reports in this team");
    expect(own.textContent).toContain("Odd sent to 2");
    expect(own.textContent).toContain("Odd 1 comment");
  });

  it("List_AFailedRead_SaysWhy", async () => {
    const { getReportsSentToMe } = await import("@devthrottle/client-core/teams/teamReportsClient");
    vi.mocked(getReportsSentToMe).mockRejectedValueOnce(new GatewayError(500, "fault", { reason: "the Gateway fell over" }));
    renderAt("/reports");

    expect(await screen.findByText(/the Gateway fell over/)).toBeTruthy();
  });
});

describe("a report sent to you", () => {
  it("Open_StaysOnThePageAddress_AndUsesTheSharedViewerFedFromTheRecipientsRoute", async () => {
    renderAt("/reports");
    fireEvent.click((await screen.findAllByTestId("team-report-row"))[0]);

    expect(screen.getByTestId("where").textContent).toBe("/reports?report=r-new");
    expect(await screen.findByTestId("dev-report-viewer")).toBeTruthy();
    expect(viewer.options.at(-1)!.reportId).toBe("r-new");
    // The viewer's bytes come from the recipient's own route - the frame host is the shared one.
    const fetchMock = vi.fn().mockResolvedValue(new Response("<p>x</p>", { status: 200, headers: { "X-Dev-Report-Version": "3" } }));
    vi.stubGlobal("fetch", fetchMock);
    await viewer.options.at(-1)!.api.getHtml("r-new", 3);
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/team-dt/reports/sent-to-me/r-new/html?version=3");
  });

  it("Open_QuestionsWaitOnTheReader_TheGatewaysLineLinksToTheQuestionsPage", async () => {
    renderAt("/reports?report=r-new");

    const line = await screen.findByTestId("team-report-questions-waiting");
    expect(line.textContent).toBe("Odd 1 question waits~q");
    expect(within(line).getByRole("link").getAttribute("href")).toBe("/questions");
  });

  it("Open_NoQuestionsWait_DrawsNoQuestionsLine", async () => {
    client.receivedDetail = { ...RECEIVED_DETAIL, report: RECEIVED.reports[1] };
    renderAt("/reports?report=r-old");

    await screen.findByTestId("team-report-comments-note");
    expect(screen.queryByTestId("team-report-questions-waiting")).toBeNull();
  });

  it("Open_ANewReport_IsMarkedReadOnce_AndShowsWhereCommentsGo", async () => {
    renderAt("/reports?report=r-new");

    expect((await screen.findByTestId("team-report-comments-note")).textContent).toBe(RECEIVED_DETAIL.commentsNote);
    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(1));
    // The read names the version the page showed - the version this person was sent.
    expect(vi.mocked(markReportRead).mock.calls[0].slice(0, 3)).toEqual(["team-dt", "r-new", 1]);
    expect(screen.getByTestId("team-report-comments").textContent).toContain("Earlier words of mine");
  });

  it("Open_AVersionSentWhileOpen_IsMarkedWhenShown_AfterTheGatewayRefusedTheOlderOne", async () => {
    // The page read version 1; by the time its read lands, the author has sent version 2. The Gateway refuses the read
    // with its own sentence; the page shows it, and its next poll brings version 2 - which it then marks.
    vi.mocked(getReportSentToMe).mockResolvedValueOnce(RECEIVED_DETAIL);
    client.receivedDetail = { ...RECEIVED_DETAIL, report: { ...RECEIVED.reports[0], version: 2 } };
    vi.mocked(markReportRead).mockRejectedValueOnce(new GatewayError(409, "moved", { reason: "Odd newer one sent 7" }));
    renderAt("/reports?report=r-new");

    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId("team-report-comments").textContent).toContain("Odd newer one sent 7"));
    await nextPoll();
    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(2));
    expect(vi.mocked(markReportRead).mock.calls.map((c) => c[2])).toEqual([1, 2]);
  });

  it("Open_AVersionSentWhileOpen_AfterTheOlderOneWasMarked_IsMarkedToo", async () => {
    // Delta review D5 with the older read ACCEPTED: version 1 is marked, then the author sends version 2 while the
    // report is still open. The page remembers WHICH version it marked, so version 2 is marked when the poll shows it.
    renderAt("/reports?report=r-new");
    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(1));
    await nextPoll();
    expect(markReportRead).toHaveBeenCalledTimes(1);

    client.receivedDetail = { ...RECEIVED_DETAIL, report: { ...RECEIVED.reports[0], version: 2 } };
    await nextPoll();
    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(2));
    expect(vi.mocked(markReportRead).mock.calls.map((c) => c[2])).toEqual([1, 2]);
  });

  it("Open_AReadThatFailed_IsTriedAgainForTheSameVersion_OnTheNextPoll_AndNotAfterItSucceeded", async () => {
    // Round-3 review R3: a version is marked done only once the Gateway accepted the read. A failure - here one the
    // Gateway says may be retried - leaves it unmarked, and the next poll tries the SAME version again.
    vi.mocked(markReportRead).mockRejectedValueOnce(new GatewayError(503, "down", { reason: "Odd try again 9" }));
    renderAt("/reports?report=r-new");

    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId("team-report-comments").textContent).toContain("Odd try again 9"));
    await nextPoll();
    await waitFor(() => expect(markReportRead).toHaveBeenCalledTimes(2));
    expect(vi.mocked(markReportRead).mock.calls.map((c) => c[2])).toEqual([1, 1]);

    // It succeeded: a later poll, still answering the same version, does not post it a third time.
    await nextPoll();
    await nextPoll();
    expect(markReportRead).toHaveBeenCalledTimes(2);
  });

  it("Open_AReportAlreadyRead_IsNotMarkedAgain", async () => {
    client.receivedDetail = { ...RECEIVED_DETAIL, report: RECEIVED.reports[1] };
    renderAt("/reports?report=r-old");

    await screen.findByTestId("team-report-comments-note");
    expect(markReportRead).not.toHaveBeenCalled();
  });

  it("Comment_SendsTheWordsAsTyped_AndShowsThem", async () => {
    renderAt("/reports?report=r-new");
    const box = await screen.findByTestId("team-report-comment-box");
    const send = screen.getByTestId("team-report-comment-send") as HTMLButtonElement;
    expect(send.disabled).toBe(true);

    fireEvent.change(box, { target: { value: "  The number is low.\nCheck it?  " } });
    await act(async () => {
      fireEvent.click(send);
    });

    expect(vi.mocked(commentOnReport).mock.calls[0].slice(0, 3)).toEqual(["team-dt", "r-new", "  The number is low.\nCheck it?  "]);
    const comments = screen.getAllByTestId("team-report-comment");
    expect(comments.at(-1)!.textContent).toContain("The number is low.");
    expect((screen.getByTestId("team-report-comment-box") as HTMLTextAreaElement).value).toBe("");
  });

  it("Comment_Refused_ShowsTheGatewaysSentence_AndKeepsTheWords", async () => {
    client.sendError = new GatewayError(400, "refused", { reason: "Odd refusal sentence 9" });
    renderAt("/reports?report=r-new");
    const box = await screen.findByTestId("team-report-comment-box");

    fireEvent.change(box, { target: { value: "kept" } });
    await act(async () => {
      fireEvent.click(screen.getByTestId("team-report-comment-send"));
    });

    expect(screen.getByTestId("team-report-comment-error").textContent).toContain("Odd refusal sentence 9");
    expect((screen.getByTestId("team-report-comment-box") as HTMLTextAreaElement).value).toBe("kept");
  });

  it("Open_AReportNotSentToYou_SaysItIsNotHere_AndOffersTheWayBack", async () => {
    client.receivedDetail = null;
    renderAt("/reports?report=someone-elses");

    expect(await screen.findByTestId("team-report-missing")).toBeTruthy();
    fireEvent.click(screen.getByText("All reports"));
    expect(screen.getByTestId("where").textContent).toBe("/reports");
  });

  it("Open_TheGatewaySaysNotesAndAnswersAreOff_TheFrameGetsOnlyTheScriptThatDisablesTheReportsOwnControls", async () => {
    renderAt("/reports?report=r-new");

    expect(await screen.findByTestId("dev-report-viewer")).toBeTruthy();
    // The viewer is opened only after the Gateway answered, with that answer: no notes script, and the report's own
    // answer controls disabled (round-3 review R1; what that script does to them is proven in answersOffScript.test).
    expect(viewer.options).toHaveLength(1);
    expect(viewer.options[0].script).toBe(DEV_REPORT_ANSWERS_OFF_SCRIPT);
  });

  it("Open_TheGatewaySaysAnswersAreOpen_TheReportsOwnControlsAreLeftLive", async () => {
    // The flag is the Gateway's, so a later piece that lets a reader answer (devthrottle_internal#2307) turns it on
    // there and this page follows; it never decides it.
    client.receivedDetail = { ...RECEIVED_DETAIL, answersOpen: true };
    renderAt("/reports?report=r-new");

    expect(await screen.findByTestId("dev-report-viewer")).toBeTruthy();
    expect(viewer.options[0].script).toBe("");
  });

  it("Open_TheGatewaySaysNotesAreOn_TheViewerGetsTheNotesScript", async () => {
    // The page passes the Gateway's flag through; it never decides it. On is the owner's viewer, unchanged.
    client.receivedDetail = { ...RECEIVED_DETAIL, notesOpen: true };
    renderAt("/reports?report=r-new");

    expect(await screen.findByTestId("dev-report-viewer")).toBeTruthy();
    expect(viewer.options[0].script.length).toBeGreaterThan(1000);
  });

  it("Open_TheAuthorCanNoLongerReadComments_SaysSo_AndOffersNoBox", async () => {
    client.receivedDetail = { ...RECEIVED_DETAIL, canComment: false, commentsNote: "Odd closed sentence 8" };
    renderAt("/reports?report=r-new");

    expect((await screen.findByTestId("team-report-comments-note")).textContent).toBe("Odd closed sentence 8");
    expect(screen.queryByTestId("team-report-comment-box")).toBeNull();
    expect(screen.queryByTestId("team-report-comment-send")).toBeNull();
    expect(screen.getByTestId("team-report-comments").textContent).toContain("Earlier words of mine");
  });

  it("Comment_RefusedBecauseTheCommentsClosedMeanwhile_ReadsTheGatewayAgain_AndTheBoxGoes", async () => {
    client.sendError = new GatewayError(409, "closed", { reason: "Odd closed now 6" });
    renderAt("/reports?report=r-new");
    fireEvent.change(await screen.findByTestId("team-report-comment-box"), { target: { value: "too late" } });
    client.receivedDetail = { ...RECEIVED_DETAIL, canComment: false, commentsNote: "Odd closed sentence 8" };

    await act(async () => {
      fireEvent.click(screen.getByTestId("team-report-comment-send"));
    });

    expect(screen.getByTestId("team-report-comment-error").textContent).toContain("Odd closed now 6");
    await waitFor(() => expect(screen.queryByTestId("team-report-comment-box")).toBeNull());
    expect(screen.getByTestId("team-report-comments-note").textContent).toBe("Odd closed sentence 8");
  });

  it("AllReports_GoesBackToTheList", async () => {
    renderAt("/reports?report=r-new");
    await screen.findByTestId("dev-report-viewer");
    fireEvent.click(screen.getByTestId("team-report-back"));

    expect(screen.getByTestId("where").textContent).toBe("/reports");
    expect(await screen.findAllByTestId("team-report-row")).toHaveLength(2);
  });
});

describe("one of your own reports", () => {
  it("Own_ShowsTheCommentsFromPeople_WhoItWentTo_AndWhoItCanStillGoTo", async () => {
    renderAt("/reports?yours=y1");

    const comment = await screen.findByTestId("team-report-comment-from-person");
    expect(comment.textContent).toContain("mike@odd.example");
    expect(comment.textContent).toContain("A comment from Mike, for you");
    // A comment written with an answer on the Questions page says which question, in the Gateway's words.
    expect(within(comment).getByTestId("team-report-comment-about").textContent).toBe("Odd about the trial~q");
    expect(screen.getByTestId("team-report-recipient").textContent).toContain("Opened~odd");
    expect(screen.getByTestId("team-report-send").textContent).toContain("Odd send note 3");
    expect(screen.getAllByTestId("team-report-choice").map((c) => (c as HTMLInputElement).value)).toEqual(["m-nina", "m-bob", "m-mike"]);
    // The viewer is the shared one, fed from the author's own route.
    expect(viewer.options.at(-1)!.reportId).toBe("y1");
  });

  it("Own_ShowsWhichVersionEachPersonHolds_AndWhatSendingDoesForThem_InTheGatewaysWords", async () => {
    renderAt("/reports?yours=y1");

    expect((await screen.findByTestId("team-report-recipient-version")).textContent).toContain("Odd holds 2 of 3");
    expect(screen.getByTestId("team-report-send").textContent).toContain("Odd gets version 3");
    expect(viewer.options[0].script).toBe(DEV_REPORT_ANSWERS_OFF_SCRIPT);
  });

  it("Own_SentToNobody_SaysTheGatewaysWords", async () => {
    client.ownDetail = { ...OWN_DETAIL, recipients: [] };
    renderAt("/reports?yours=y1");

    expect(await screen.findByText("Odd nobody yet 4")).toBeTruthy();
  });

  it("Own_NoComments_SaysTheGatewaysWords", async () => {
    client.ownDetail = { ...OWN_DETAIL, comments: [] };
    renderAt("/reports?yours=y1");

    expect((await screen.findByTestId("team-report-no-comments")).textContent).toBe("Odd no comments 5");
  });

  it("Send_SendsExactlyTheChosenMembers", async () => {
    renderAt("/reports?yours=y1");
    const button = (await screen.findByTestId("team-report-send-button")) as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    fireEvent.click(screen.getAllByTestId("team-report-choice")[1]);
    await act(async () => {
      fireEvent.click(button);
    });

    // The send names the version the page is showing, so what is sent is what the author read.
    expect(vi.mocked(sendMyTeamReport).mock.calls[0].slice(0, 4)).toEqual(["team-dt", "y1", ["m-bob"], 3]);
  });

  it("Send_RefusedBecauseANewerVersionArrived_ShowsTheGatewaysSentence_ReadsAgain_AndTheNextSendIsTheNewerOne", async () => {
    renderAt("/reports?yours=y1");
    fireEvent.click((await screen.findAllByTestId("team-report-choice"))[1]);
    client.sendError = new GatewayError(409, "newer", { reason: "Odd newer version 6" });
    client.ownDetail = { ...OWN_DETAIL, report: { ...OWN_DETAIL.report, version: 4 } };
    const readsBefore = vi.mocked(getMyTeamReport).mock.calls.length;

    await act(async () => {
      fireEvent.click(screen.getByTestId("team-report-send-button"));
    });

    expect(screen.getByTestId("team-report-send-error").textContent).toContain("Odd newer version 6");
    await waitFor(() => expect(vi.mocked(getMyTeamReport).mock.calls.length).toBeGreaterThan(readsBefore));
    client.sendError = null;
    // The choice is kept after a refusal, so the same person is sent the newer version.
    await act(async () => {
      fireEvent.click(screen.getByTestId("team-report-send-button"));
    });
    expect(vi.mocked(sendMyTeamReport).mock.calls.map((c) => c[3])).toEqual([3, 4]);
  });

  it("Send_Refused_ShowsTheGatewaysSentence", async () => {
    client.sendError = new GatewayError(400, "refused", { reason: "Odd not a member 4" });
    renderAt("/reports?yours=y1");
    fireEvent.click((await screen.findAllByTestId("team-report-choice"))[0]);
    await act(async () => {
      fireEvent.click(screen.getByTestId("team-report-send-button"));
    });

    expect(screen.getByTestId("team-report-send-error").textContent).toContain("Odd not a member 4");
  });

  it("Own_NobodyLeftToSendTo_ShowsNoSendBox", async () => {
    client.ownDetail = { ...OWN_DETAIL, choices: [] };
    renderAt("/reports?yours=y1");

    await screen.findByTestId("team-report-recipient");
    expect(screen.queryByTestId("team-report-send")).toBeNull();
  });
});
