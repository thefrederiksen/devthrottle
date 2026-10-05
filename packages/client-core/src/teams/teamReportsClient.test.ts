import { describe, it, expect, vi, afterEach } from "vitest";
import {
  commentOnReport,
  getMyTeamReport,
  getMyTeamReports,
  getReportSentToMe,
  getReportsSentToMe,
  markReportRead,
  ownTeamReportApi,
  receivedReportApi,
  sendMyTeamReport,
} from "./teamReportsClient";

// A dev report sent to a member of the team (devthrottle_internal#2309), as the Cockpit reads it: every route under
// /teams/{teamId}/reports, the Gateway's words carried as sent, a 404 read as "not here" rather than an error, and the
// two data sources the ONE shared viewer is given - never a second viewer.

function respond(body: unknown, status = 200, contentType = "application/json", headers: Record<string, string> = {}): Response {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), { status, headers: { "Content-Type": contentType, ...headers } });
}

const RECEIVED = {
  count: 1,
  emptyText: "No reports sent to you yet.",
  showYourReports: false,
  reports: [{ id: "r1", title: "Signup page rewrite", status: "waiting-on-you", version: 2, from: "soren@example.com",
    sentAtUtc: "2026-10-02T09:00:00Z", read: false, readLabel: "New" }],
};

const OWN_DETAIL = {
  report: { id: "r1", title: "Signup page rewrite", status: "done", version: 3, publishedAtUtc: "2026-10-01T09:00:00Z", updatedAtUtc: "2026-10-02T09:00:00Z" },
  recipients: [],
  choices: [{ memberId: "m-mike", name: "mike@example.com", role: "Collaborator" }],
  sendNote: "The people you send this report to can read it and comment on it.",
  comments: [],
  commentsEmptyText: "No comments yet.",
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the recipient's routes", () => {
  it("GetReportsSentToMe_ReturnsTheGatewaysAnswerAsSent", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond(RECEIVED));
    vi.stubGlobal("fetch", fetchMock);

    expect(await getReportsSentToMe("t/1")).toEqual(RECEIVED);
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t%2F1/reports/sent-to-me");
  });

  it("GetReportSentToMe_NotSentToThem_IsNull", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "There is no report sent to you with that id.", code: "report_not_found" }, 404)));

    expect(await getReportSentToMe("t1", "r9")).toBeNull();
  });

  it("GetReportSentToMe_AnotherFailure_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "fault" }, 500)));

    await expect(getReportSentToMe("t1", "r1")).rejects.toThrow();
  });

  it("MarkReportRead_PostsToTheReadRoute", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond({ read: true, readLabel: "Read" }));
    vi.stubGlobal("fetch", fetchMock);

    await markReportRead("t1", "r1");

    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t1/reports/sent-to-me/r1/read");
    expect(fetchMock.mock.calls[0][1].method).toBe("POST");
  });

  it("CommentOnReport_PostsTheWordsExactly_AndReturnsTheStoredComment", async () => {
    const words = "  Can you check September?\nThanks  ";
    const fetchMock = vi.fn().mockResolvedValue(respond({ comment: { id: "c1", text: words, atUtc: "2026-10-02T10:00:00Z" } }));
    vi.stubGlobal("fetch", fetchMock);

    const comment = await commentOnReport("t1", "r1", words);

    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t1/reports/sent-to-me/r1/comments");
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ text: words });
    expect(comment.text).toBe(words);
  });

  it("CommentOnReport_Refused_ThrowsWithTheGatewaysSentence", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "The comment is empty. Write what you want the author to read.", code: "comment_empty" }, 400)));

    await expect(commentOnReport("t1", "r1", " ")).rejects.toMatchObject({ status: 400 });
  });
});

describe("the author's routes", () => {
  it("GetMyTeamReports_ReadsTheMineList", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond({ count: 0, reports: [], emptyText: "None yet." }));
    vi.stubGlobal("fetch", fetchMock);

    expect((await getMyTeamReports("t1")).emptyText).toBe("None yet.");
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t1/reports/mine");
  });

  it("GetMyTeamReport_NotTheirs_IsNull", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "no" }, 404)));

    expect(await getMyTeamReport("t1", "r1")).toBeNull();
  });

  it("SendMyTeamReport_PostsTheMemberIds_AndReturnsTheNewDetail", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond(OWN_DETAIL));
    vi.stubGlobal("fetch", fetchMock);

    expect(await sendMyTeamReport("t1", "r1", ["m-mike", "m-nina"])).toEqual(OWN_DETAIL);
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t1/reports/mine/r1/recipients");
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ memberIds: ["m-mike", "m-nina"] });
  });
});

describe("the shared viewer's data sources", () => {
  it("ReceivedReportApi_ReadsTheRecipientsOwnRoutes_AndCarriesNoSessionAndNoConversation", async () => {
    const fetchMock = vi.fn(async (url: string) =>
      url.endsWith("/html?version=2")
        ? respond("<p>bytes</p>", 200, "text/plain", { "X-Dev-Report-Version": "2" })
        : respond({ report: RECEIVED.reports[0], comments: [], commentsNote: "Your comments go to soren@example.com." }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const api = receivedReportApi("t1");

    const detail = await api.getDetail("r1");
    const page = await api.getHtml("r1", 2);

    expect(detail?.report).toMatchObject({ id: "r1", title: "Signup page rewrite", status: "waiting-on-you", version: 2, sessionId: "", key: "" });
    expect(detail?.items).toEqual([]);
    expect(detail?.replies).toEqual([]);
    expect(page).toEqual({ html: "<p>bytes</p>", version: 2 });
    expect(fetchMock.mock.calls.map((c) => c[0])).toEqual(["/teams/t1/reports/sent-to-me/r1", "/teams/t1/reports/sent-to-me/r1/html?version=2"]);
  });

  it("ReceivedReportApi_HtmlWithoutAVersionHeader_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond("<p>bytes</p>", 200, "text/plain")));

    await expect(receivedReportApi("t1").getHtml("r1", 1)).rejects.toThrow(/X-Dev-Report-Version/);
  });

  it("ReceivedReportApi_NotSentToThem_IsNull", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "no" }, 404)));

    expect(await receivedReportApi("t1").getDetail("r1")).toBeNull();
    expect(await receivedReportApi("t1").getHtml("r1", 1)).toBeNull();
  });

  it("BothApis_SendNothing_AndASendIsADefectThatSaysSo", async () => {
    // The page carries no notes for a team reader (the Gateway's notesOpen is false), so a send cannot be asked for.
    // If one ever is, nothing goes to the Gateway and the failure names why - never a made-up "refused" answer.
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const items = [{ id: "n-1", kind: "note" as const, text: "a note", anchor: { type: "text" as const, selector: "p", quote: "x" } }];

    for (const api of [receivedReportApi("t1"), ownTeamReportApi("t1")]) {
      await expect(api.send("r1", items)).rejects.toThrow(/notes are off/);
    }
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("OwnTeamReportApi_ReadsTheAuthorsOwnRoutes", async () => {
    const fetchMock = vi.fn(async (url: string) =>
      url.includes("/html") ? respond("<p>mine</p>", 200, "text/plain", { "X-Dev-Report-Version": "3" }) : respond(OWN_DETAIL),
    );
    vi.stubGlobal("fetch", fetchMock);
    const api = ownTeamReportApi("t1");

    expect((await api.getDetail("r1"))?.report).toMatchObject({ title: "Signup page rewrite", version: 3, updatedAtUtc: "2026-10-02T09:00:00Z" });
    expect(await api.getHtml("r1", 3)).toEqual({ html: "<p>mine</p>", version: 3 });
    expect(fetchMock.mock.calls.map((c) => c[0])).toEqual(["/teams/t1/reports/mine/r1", "/teams/t1/reports/mine/r1/html?version=3"]);
  });
});
