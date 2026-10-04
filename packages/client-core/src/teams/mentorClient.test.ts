import { describe, it, expect, vi, afterEach } from "vitest";
import {
  getMentorPage,
  isoWeekOf,
  MENTOR_NOT_OFFERED_REASON,
  MENTOR_REFUSED_REASON,
  weekAfter,
  weekBefore,
} from "./mentorClient";

// GET /teams/{teamId}/mentor?week=YYYY-Www (devthrottle_internal#2305) read against its contract,
// docs/proof/teams-2305/contract.md. The 200 body below is the contract's own example, with the readers the Gateway
// adds; the client must hand it over verbatim - above all the quoted prompt, which is the person's own words.

const APP_SHELL = '<!doctype html><html><head><title>DevThrottle Cockpit</title></head><body><div id="root"></div></body></html>';

const CONTRACT_EXAMPLE = {
  teamId: "6f0c",
  week: "2026-W40",
  weekStart: "2026-09-28",
  weekEnd: "2026-10-04",
  timeZone: "Europe/Copenhagen",
  scope: "everyone",
  written: true,
  blocks: [
    {
      personSubject: "a1b2",
      personEmail: "rob@example.com",
      role: "Developer",
      tone: "hard",
      toneLabel: "a hard week",
      workedOn: "The new signup page and two bug fixes in the installer.",
      howItWent: null,
      wentBadlyAndWhy:
        "On Tuesday the same task was restarted four times. The first instruction didn't say which file to change, so the agent guessed differently each time.",
      quotes: [{ promptId: "p_3f9a", at: "2026-09-29T09:14:03Z", text: "fix the signup thing so it doesnt break on mobile" }],
      oneThingToTry: "Name the file and the result you expect in the first line, before asking for the change.",
      writtenAtUtc: "2026-10-05T00:20:11Z",
    },
  ],
  readers: [{ email: "priya@example.com", role: "Manager" }],
};

function respond(body: string, contentType: string, status = 200): Response {
  return new Response(body, { status, headers: { "Content-Type": contentType } });
}

function json(body: unknown, status = 200): Response {
  return respond(JSON.stringify(body), "application/json; charset=utf-8", status);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getMentorPage", () => {
  it("GetMentorPage_ContractExample_ReturnsEveryFieldVerbatim", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(CONTRACT_EXAMPLE));
    vi.stubGlobal("fetch", fetchMock);

    const answer = await getMentorPage("6f0c", "2026-W40");

    expect(answer).toEqual({ kind: "page", page: CONTRACT_EXAMPLE });
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/6f0c/mentor?week=2026-W40");
  });

  it("GetMentorPage_QuoteText_IsTheResponseTextByteForByte", async () => {
    // Quotes are typed by people: odd spacing, no punctuation, symbols. Not one character may change on the way.
    const raw = "  fix  the signup thing so it doesnt break on mobile!!  <b>not bold</b> é ";
    const body = structuredClone(CONTRACT_EXAMPLE);
    body.blocks[0].quotes[0].text = raw;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(body)));

    const answer = await getMentorPage("6f0c");

    if (answer.kind !== "page") throw new Error(`expected a page, got ${answer.kind}`);
    expect(answer.page.blocks[0].quotes[0].text).toBe(raw);
  });

  it("GetMentorPage_NoWeek_AsksForTheGatewaysDefaultWeek", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(CONTRACT_EXAMPLE));
    vi.stubGlobal("fetch", fetchMock);

    await getMentorPage("team id/with space");

    expect(fetchMock.mock.calls[0][0]).toBe("/teams/team%20id%2Fwith%20space/mentor");
  });

  it("GetMentorPage_WrittenWithNoBlocks_IsAPageWithNoBlocks", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ ...CONTRACT_EXAMPLE, blocks: [] })));

    const answer = await getMentorPage("6f0c");

    expect(answer).toEqual({ kind: "page", page: { ...CONTRACT_EXAMPLE, blocks: [] } });
  });

  it("GetMentorPage_Collaborator403_IsRefusedWithTheGatewaysSentence", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json({ error: "A Collaborator has no Mentor page.", code: "team_action_refused" }, 403)),
    );

    expect(await getMentorPage("6f0c")).toEqual({ kind: "refused", reason: "A Collaborator has no Mentor page." });
  });

  it("GetMentorPage_403WithNoSentence_IsRefused", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond("", "text/plain", 403)));

    expect(await getMentorPage("6f0c")).toEqual({ kind: "refused", reason: MENTOR_REFUSED_REASON });
  });

  it("GetMentorPage_NotAMember404_IsNotOffered", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "There is no such team." }, 404)));

    expect(await getMentorPage("6f0c")).toEqual({ kind: "not-offered", reason: "There is no such team." });
  });

  it("GetMentorPage_TeamsDarkAppShell_IsNotOffered", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(APP_SHELL, "text/html; charset=utf-8")));

    expect(await getMentorPage("6f0c")).toEqual({ kind: "not-offered", reason: MENTOR_NOT_OFFERED_REASON });
  });

  it("GetMentorPage_InvalidWeek400_ThrowsTheGatewaysSentence", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "'2026-W99' is not an ISO week." }, 400)));

    await expect(getMentorPage("6f0c", "2026-W99")).rejects.toMatchObject({
      status: 400,
      message: "'2026-W99' is not an ISO week.",
    });
  });

  it("GetMentorPage_BlockMissingItsText_Throws", async () => {
    const body = structuredClone(CONTRACT_EXAMPLE) as unknown as { blocks: Array<Record<string, unknown>> };
    delete body.blocks[0].workedOn;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(body)));

    await expect(getMentorPage("6f0c")).rejects.toMatchObject({ status: 502 });
  });

  it("GetMentorPage_NoReadersList_Throws", async () => {
    const body: Record<string, unknown> = { ...CONTRACT_EXAMPLE };
    delete body.readers;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(body)));

    await expect(getMentorPage("6f0c")).rejects.toMatchObject({ status: 502 });
  });

  it("GetMentorPage_QuoteWithNothingWentBadly_Throws", async () => {
    const body = structuredClone(CONTRACT_EXAMPLE) as unknown as { blocks: Array<Record<string, unknown>> };
    body.blocks[0].wentBadlyAndWhy = null;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(body)));

    await expect(getMentorPage("6f0c")).rejects.toMatchObject({ status: 502 });
  });
});

describe("the week chooser's arithmetic", () => {
  it("IsoWeekOf_KnownDates_MatchTheIsoCalendar", () => {
    expect(isoWeekOf("2026-09-28")).toBe("2026-W40");
    expect(isoWeekOf("2026-10-04")).toBe("2026-W40");
    // 1 January 2027 is a Friday, so it belongs to the last week of 2026, which has 53 weeks.
    expect(isoWeekOf("2027-01-01")).toBe("2026-W53");
    // 29 December 2025 is a Monday whose Thursday is in 2026: week 1 of 2026.
    expect(isoWeekOf("2025-12-29")).toBe("2026-W01");
  });

  it("WeekBefore_And_WeekAfter_StepOneWeekAcrossAYear", () => {
    expect(weekBefore("2026-09-28")).toBe("2026-W39");
    expect(weekAfter("2026-09-28")).toBe("2026-W41");
    expect(weekBefore("2025-12-29")).toBe("2025-W52");
    expect(weekAfter("2026-12-28")).toBe("2027-W01");
  });
});
