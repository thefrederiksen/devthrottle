import { describe, it, expect, vi, afterEach } from "vitest";
import { getMentorPage, isoWeekOf, shiftWeek } from "./mentorClient";

// GET /teams/{teamId}/mentor?week=YYYY-Www (devthrottle_internal#2305) read against its contract,
// docs/proof/teams-2305/contract.md in the Gateway worktree. CONTRACT_EXAMPLE is the contract's own 200 example, copied
// field for field (as it stands on 4 October 2026); the client must hand it over verbatim - above all the quoted
// prompt, which is the person's own words. An answer that breaks the contract is thrown, never drawn as a guess.

const APP_SHELL = '<!doctype html><html><head><title>DevThrottle Cockpit</title></head><body><div id="root"></div></body></html>';

const CONTRACT_EXAMPLE = {
  teamId: "6f0c...",
  week: "2026-W40",
  weekStart: "2026-09-28",
  weekEnd: "2026-10-04",
  timeZone: "Europe/Copenhagen",
  scope: "everyone",
  written: true,
  readers: [
    { email: "olivia@example.com", role: "Owner" },
    { email: "priya@example.com", role: "Manager" },
  ],
  blocks: [
    {
      personSubject: "a1b2...",
      personEmail: "rob@example.com",
      role: "Developer",
      tone: "hard",
      toneLabel: "a hard week",
      workedOn: "The new signup page and two bug fixes in the installer.",
      howItWent: null,
      wentBadlyAndWhy:
        "On Tuesday they restarted the same task four times. Their first instruction didn't say which file to change, so the agent guessed differently each time.",
      quotes: [
        { promptId: "p_3f9a...", at: "2026-09-29T09:14:03Z", text: "fix the signup thing so it doesnt break on mobile" },
      ],
      oneThingToTry: "Name the file and the result you expect in the first line, before asking for the change.",
      writtenAtUtc: "2026-10-05T00:20:11Z",
    },
  ],
};

type Body = Record<string, unknown> & { blocks: Array<Record<string, unknown>>; readers: Array<Record<string, unknown>> };

function example(): Body {
  return structuredClone(CONTRACT_EXAMPLE) as unknown as Body;
}

function respond(body: string, contentType: string, status = 200): Response {
  return new Response(body, { status, headers: { "Content-Type": contentType } });
}

function json(body: unknown, status = 200): Response {
  return respond(JSON.stringify(body), "application/json; charset=utf-8", status);
}

function answering(body: unknown) {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(body)));
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
    // Quotes are typed by people: odd spacing, no punctuation, markup-looking text, accents. Not one character may
    // change on the way.
    const raw = "  fix  the signup thing so it doesnt break on mobile!!  <b>not bold</b> \u00e9 ";
    const body = example();
    (body.blocks[0].quotes as Array<{ text: string }>)[0].text = raw;
    answering(body);

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
    answering({ ...CONTRACT_EXAMPLE, blocks: [] });

    expect(await getMentorPage("6f0c")).toEqual({ kind: "page", page: { ...CONTRACT_EXAMPLE, blocks: [] } });
  });

  it("GetMentorPage_NullEmails_AreAccepted", async () => {
    // The contract: a person or a reader with no email on record carries `null`. That must not cost the whole team
    // its page (review F1).
    const body = example();
    body.blocks[0].personEmail = null;
    body.readers[1].email = null;
    answering(body);

    const answer = await getMentorPage("6f0c");

    if (answer.kind !== "page") throw new Error(`expected a page, got ${answer.kind}`);
    expect(answer.page.blocks[0].personEmail).toBeNull();
    expect(answer.page.readers[1]).toEqual({ email: null, role: "Manager" });
  });

  it("GetMentorPage_Collaborator403_IsRefused", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json({ error: "A Collaborator has no Mentor page.", code: "team_action_refused" }, 403)),
    );

    expect(await getMentorPage("6f0c")).toEqual({ kind: "refused" });
  });

  it("GetMentorPage_NotAMember404_IsNotOffered", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "There is no such team." }, 404)));

    expect(await getMentorPage("6f0c")).toEqual({ kind: "not-offered" });
  });

  it("GetMentorPage_TeamsDarkAppShell_IsNotOffered", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(APP_SHELL, "text/html; charset=utf-8")));

    expect(await getMentorPage("6f0c")).toEqual({ kind: "not-offered" });
  });

  it("GetMentorPage_InvalidWeek400_ThrowsTheGatewaysSentence", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "'2026-W99' is not an ISO week." }, 400)));

    await expect(getMentorPage("6f0c", "2026-W99")).rejects.toMatchObject({
      status: 400,
      message: "'2026-W99' is not an ISO week.",
    });
  });

  it("GetMentorPage_AnswerForAnotherWeek_Throws", async () => {
    // Asked for week 39, answered with the contract's week 40.
    answering(CONTRACT_EXAMPLE);

    await expect(getMentorPage("6f0c", "2026-W39")).rejects.toMatchObject({ status: 502 });
  });

  it("GetMentorPage_BodyThatIsNotJson_ThrowsAGatewayErrorNotAParseError", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond("{ not json", "application/json")));

    await expect(getMentorPage("6f0c")).rejects.toMatchObject({ name: "GatewayError", status: 502 });
  });

  // Every way an answer can break the contract is thrown as unreadable (review F3 and F4).
  const broken: Array<[string, (b: Body) => void]> = [
    ["a block missing its text", (b) => delete b.blocks[0].workedOn],
    ["no readers list", (b) => delete (b as Record<string, unknown>).readers],
    ["an empty readers list", (b) => (b.readers = [])],
    ["a quote with nothing went badly", (b) => (b.blocks[0].wentBadlyAndWhy = null)],
    ["went badly with no quote", (b) => (b.blocks[0].quotes = [])],
    [
      "went badly with three quotes",
      (b) => {
        const q = (b.blocks[0].quotes as unknown[])[0];
        b.blocks[0].quotes = [q, q, q];
      },
    ],
    ["an unknown tone", (b) => (b.blocks[0].tone = "terrible")],
    ["a week start that is not a date", (b) => (b.weekStart = "next Monday")],
    ["a week end that is not a real date", (b) => (b.weekEnd = "2026-02-30")],
    ["blocks in a week not written", (b) => (b.written = false)],
    ["an unknown scope", (b) => (b.scope = "team")],
    [
      "two blocks on a person's own page",
      (b) => {
        b.scope = "own";
        b.blocks = [b.blocks[0], { ...b.blocks[0], personSubject: "other" }];
      },
    ],
    ["a reader with no role", (b) => delete b.readers[0].role],
    ["a person email that is an empty string", (b) => (b.blocks[0].personEmail = "")],
    ["a reader email that is an empty string", (b) => (b.readers[0].email = "")],
    ["a block role that is an empty string", (b) => (b.blocks[0].role = "")],
    ["a reader role that is only spaces", (b) => (b.readers[1].role = "  ")],
    ["a week start that is not a Monday", (b) => (b.weekStart = "2026-09-29")],
    ["a week end that is not the Sunday after", (b) => (b.weekEnd = "2026-10-05")],
    ["a week that is not the one its dates fall in", (b) => (b.week = "2026-W41")],
  ];
  it.each(broken)("GetMentorPage_ContractBroken_%s_Throws", async (_name, breakIt) => {
    const body = example();
    breakIt(body);
    answering(body);

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

  it("ShiftWeek_StepsOneWeekEitherWayAcrossAYear", () => {
    expect(shiftWeek("2026-09-28", -1)).toEqual({ week: "2026-W39", weekStart: "2026-09-21" });
    expect(shiftWeek("2026-09-28", 1)).toEqual({ week: "2026-W41", weekStart: "2026-10-05" });
    expect(shiftWeek("2025-12-29", -1)).toEqual({ week: "2025-W52", weekStart: "2025-12-22" });
    expect(shiftWeek("2026-12-28", 1)).toEqual({ week: "2027-W01", weekStart: "2027-01-04" });
  });
});
