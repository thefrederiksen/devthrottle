import { describe, it, expect, vi, afterEach } from "vitest";
import { gatewayErrorMessage, GatewayError } from "../api/client";
import {
  getFactoriesList,
  getFactoryEmail,
  getFactoryManagerCard,
  getFactorySeatPage,
  takeScheduleOffSeat,
  resolveRunProblem,
  startFactoryManagerTalk,
  runFactoryOwnerAction,
  startProblemTalk,
  startFactoryTalk,
  talkRefusalReason,
  type FactoryOwnerAction,
} from "./factoriesScreenClient";

// The Factories screen's client. What is held down:
//   * a Talk refusal shows every sentence the Gateway wrote: the Talk route's `{ error }`, the session create's
//     problem-details `{ title, detail }`, and both when a body carries an error and a detail;
//   * a 2XX that is not JSON - a Gateway older than the screen answering with the app's HTML shell - is refused
//     with a sentence that says so, on the Talk request exactly as on the reads.

const TARGET = { label: "Talk to the boss", busyLabel: "Starting...", factoryId: "warmforward", seatId: "nora-hale" };

function answer(status: number, body: string, type = "application/json") {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(body, { status, headers: { "Content-Type": type } })));
}

afterEach(() => vi.unstubAllGlobals());

describe("talkRefusalReason", () => {
  it("reads the Talk route's own error", () => {
    expect(talkRefusalReason({ error: "No Director is running on SOREN_NORTH." })).toBe("No Director is running on SOREN_NORTH.");
  });

  it("shows a problem-details title and its detail", () => {
    expect(talkRefusalReason({ title: "Bad Gateway", status: 502, detail: "director not connected to the tunnel" })).toBe(
      "Bad Gateway. director not connected to the tunnel.",
    );
  });

  it("shows the detail as well as the error when a body carries both", () => {
    expect(talkRefusalReason({ error: "The talk was not started", detail: "the Director refused the folder" })).toBe(
      "The talk was not started. the Director refused the folder.",
    );
  });

  it("shows a detail with nothing beside it as it is", () => {
    expect(talkRefusalReason({ detail: "director not connected to the tunnel" })).toBe("director not connected to the tunnel");
  });
});

describe("startFactoryTalk", () => {
  it("returns the Gateway's answer", async () => {
    answer(201, JSON.stringify({ sessionId: "s-1", href: "/session/s-1" }));
    await expect(startFactoryTalk(TARGET)).resolves.toMatchObject({ href: "/session/s-1" });
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(
      "/gateway/factory-agents/factories/warmforward/seats/nora-hale/talk",
      expect.objectContaining({ method: "POST" }),
    );
  });

  it("carries a problem-details refusal's title and detail into the sentence the button shows", async () => {
    answer(502, JSON.stringify({ title: "Bad Gateway", status: 502, detail: "director not connected to the tunnel" }), "application/problem+json");
    const err = await startFactoryTalk(TARGET).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(GatewayError);
    expect(gatewayErrorMessage(err, "start the talk")).toContain("Bad Gateway. director not connected to the tunnel.");
  });

  it("refuses a 2XX web page instead of data, with the sentence the reads use", async () => {
    answer(200, "<!doctype html><html></html>", "text/html");
    await expect(startFactoryTalk(TARGET)).rejects.toThrow(/does not serve the Factories screen/);

    answer(200, "<!doctype html><html></html>", "text/html");
    await expect(getFactoriesList()).rejects.toThrow(/does not serve the Factories screen/);
  });
});

describe("runFactoryOwnerAction", () => {
  const ARCHIVE: FactoryOwnerAction = {
    action: "archive",
    factoryId: "tallyhand",
    label: "Archive factory",
    busyLabel: "Archiving Tallyhand...",
    confirmTitle: "Archive Tallyhand?",
    confirmLines: [],
    confirmLabel: "Archive Tallyhand",
    danger: true,
    cutoffUtc: null,
    expectedCount: null,
    schedules: ["cj_a"],
  };

  it("posts what the confirm showed to the action's own route", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ text: "Tallyhand is archived.", marked: 0, schedulesSwitched: ["cj_a"] }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await runFactoryOwnerAction(ARCHIVE);

    expect(result.text).toBe("Tallyhand is archived.");
    const [path, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(path).toBe("/gateway/factories/tallyhand/archive");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ cutoffUtc: null, expectedCount: null, schedules: ["cj_a"] });
  });

  it("sends the bulk clear to its route with the cut-off and count", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ text: "Marked 2 items handled.", marked: 2, schedulesSwitched: [] }), {
        status: 201,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await runFactoryOwnerAction({ ...ARCHIVE, action: "handled-older", cutoffUtc: "2026-09-29T10:00:00Z", expectedCount: 2, schedules: [] });

    const [path, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(path).toBe("/gateway/factories/tallyhand/waiting/handled-older");
    expect(JSON.parse(init.body as string)).toEqual({ cutoffUtc: "2026-09-29T10:00:00Z", expectedCount: 2, schedules: [] });
  });

  it("keeps the Gateway's own sentence on a refusal", async () => {
    answer(409, JSON.stringify({ error: "The schedules changed after the confirm was shown. Nothing was done." }));
    const err = await runFactoryOwnerAction(ARCHIVE).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(GatewayError);
    expect(gatewayErrorMessage(err, "archive")).toContain("The schedules changed after the confirm was shown. Nothing was done.");
  });
});

// Handle the issue and Resolve (Factory Control, step 1b): the button opens the Gateway's briefed talk, and Resolve
// calls the same route as `cc-devthrottle run resolve`, sending the reason as typed so the Gateway is the one that
// refuses an empty one - in its own words.
const HANDLE = {
  label: "Handle the issue",
  busyLabel: "Starting the talk with the boss...",
  factoryId: "tallyhand",
  runId: "3f1c2b7a-0000-4000-8000-0000000000a1",
  available: true,
  unavailableText: null,
};

describe("startProblemTalk", () => {
  it("posts to the problem's handle route and returns the talk the Gateway started", async () => {
    answer(201, JSON.stringify({ sessionId: "s-9", href: "/session/s-9" }));
    await expect(startProblemTalk(HANDLE)).resolves.toMatchObject({ href: "/session/s-9" });
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(
      "/gateway/factory-agents/factories/tallyhand/problems/3f1c2b7a-0000-4000-8000-0000000000a1/handle",
      expect.objectContaining({ method: "POST" }),
    );
  });

  it("carries the Gateway's refusal sentence", async () => {
    answer(409, JSON.stringify({ error: "Tallyhand has no boss, so this goes to the Factory Manager, which is not built yet." }));
    const err = await startProblemTalk(HANDLE).catch((e: unknown) => e);
    expect(gatewayErrorMessage(err, "start the talk")).toContain("which is not built yet");
  });
});

describe("resolveRunProblem", () => {
  it("posts the reason to the same route the command line resolves through", async () => {
    answer(200, JSON.stringify({ runId: HANDLE.runId, state: "resolved" }));
    await resolveRunProblem(HANDLE.runId, "Re-ran it by hand; the feed is back.");
    const [path, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/cron/runs/3f1c2b7a-0000-4000-8000-0000000000a1/resolve");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ reason: "Re-ran it by hand; the feed is back." });
  });

  it("sends an empty reason as it is and shows the Gateway's refusal", async () => {
    answer(400, JSON.stringify({ error: "Resolving needs a one-line reason, and none was given. Nothing was recorded." }));
    const err = await resolveRunProblem(HANDLE.runId, "").catch((e: unknown) => e);
    expect(JSON.parse(vi.mocked(fetch).mock.calls[0][1]!.body as string)).toEqual({ reason: "" });
    expect(gatewayErrorMessage(err, "resolve it")).toContain("needs a one-line reason");
  });
});

describe("getFactoryEmail", () => {
  it("reads one kept email by id, with its HTML as sent", async () => {
    answer(200, JSON.stringify({ id: "e1", title: "Factories: 2 open problems", html: "<p>as sent</p>" }));
    const email = await getFactoryEmail("e1");
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe("/gateway/factory-emails/e1");
    expect(email.html).toBe("<p>as sent</p>");
  });

  it("shows the Gateway's refusal for an email the account does not have", async () => {
    answer(404, JSON.stringify({ error: "there is no factory email e1 in this account." }));
    const err = await getFactoryEmail("e1").catch((e: unknown) => e);
    expect(gatewayErrorMessage(err, "load the report")).toContain("there is no factory email e1");
  });

  it("refuses a 2XX that is not JSON, as on every other read", async () => {
    answer(200, "<!doctype html><html></html>", "text/html");
    const err = await getFactoryEmail("e1").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(GatewayError);
  });
});

describe("the Factory Manager", () => {
  it("reads its card from the Factory Manager route", async () => {
    answer(200, JSON.stringify({ title: "Factory Manager", actionLabel: "Talk to the Factory Manager" }));
    await expect(getFactoryManagerCard()).resolves.toMatchObject({ title: "Factory Manager" });
    expect(vi.mocked(fetch)).toHaveBeenCalledWith("/gateway/factory-agents/factory-manager", expect.objectContaining({ method: "GET" }));
  });

  it("sends the chosen computer and folder on a first start", async () => {
    answer(201, JSON.stringify({ sessionId: "s-1", href: "/session/s-1" }));
    await expect(startFactoryManagerTalk({ computer: "SOREN_NORTH", folder: "D:/factories" })).resolves.toMatchObject({ href: "/session/s-1" });
    const [path, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/gateway/factory-agents/factory-manager/talk");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ computer: "SOREN_NORTH", folder: "D:/factories" });
  });

  it("sends no body when the saved placement is used", async () => {
    answer(200, JSON.stringify({ sessionId: "s-1", href: "/session/s-1" }));
    await startFactoryManagerTalk(null);
    const [, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(init.body).toBeUndefined();
  });

  it("carries the Gateway's refusal sentence", async () => {
    answer(409, JSON.stringify({ error: "No Director is running on SOREN_NORTH, the computer the Factory Manager runs on." }));
    const err = await startFactoryManagerTalk(null).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(GatewayError);
    expect(gatewayErrorMessage(err, "start the Factory Manager")).toContain("No Director is running on SOREN_NORTH");
  });
});

// One seat's page and "Take off this seat" (Factory Seats mission, phase 1).
describe("getFactorySeatPage", () => {
  it("reads the seat's own address, with the factory and the seat escaped", async () => {
    answer(200, JSON.stringify({ seatId: "front desk", startsWord: "Talk only" }));
    await expect(getFactorySeatPage("warm forward", "front desk")).resolves.toMatchObject({ startsWord: "Talk only" });
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(
      "/gateway/factories/warm%20forward/seats/front%20desk",
      expect.objectContaining({ method: "GET" }),
    );
  });

  it("keeps the Gateway's sentence for a seat the factory does not have", async () => {
    answer(404, JSON.stringify({ error: "WarmForward has no seat 'owner-session'. Its seats are: boss, scout." }));
    await expect(getFactorySeatPage("warmforward", "owner-session")).rejects.toMatchObject({
      status: 404,
      message: expect.stringContaining("WarmForward has no seat 'owner-session'. Its seats are: boss, scout."),
    });
  });

  it("refuses a 2XX that is not data - an older Gateway answering with the app's page", async () => {
    answer(200, "<!doctype html><html></html>", "text/html");
    await expect(getFactorySeatPage("warmforward", "boss")).rejects.toBeInstanceOf(GatewayError);
  });
});

describe("takeScheduleOffSeat", () => {
  it("posts to the one unlink route and returns what the Gateway said it did", async () => {
    answer(200, JSON.stringify({ text: "Taken off Scout of WarmForward.", factory: "warmforward", seat: "scout", job: { id: "cj_scout" } }));
    await expect(takeScheduleOffSeat("cj_scout")).resolves.toMatchObject({ text: "Taken off Scout of WarmForward.", seat: "scout" });
    const [path, init] = vi.mocked(fetch).mock.calls[0];
    expect(path).toBe("/cron/jobs/cj_scout/unlink");
    expect(init).toMatchObject({ method: "POST" });
    // Never an update of the schedule: a blank factory on an update means "said nothing" and would keep the link.
    expect((init as RequestInit).body).toBeUndefined();
  });

  it("a refusal carries the Gateway's own sentence", async () => {
    const refusal = "this schedule belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change or run it";
    answer(403, JSON.stringify({ error: refusal, detail: "why" }));
    await expect(takeScheduleOffSeat("cj_scout")).rejects.toMatchObject({ status: 403, message: expect.stringContaining(refusal) });
  });

  it("refuses a 2XX that is not data, so an older Gateway never reads as taken off", async () => {
    answer(200, "<!doctype html><html></html>", "text/html");
    await expect(takeScheduleOffSeat("cj_scout")).rejects.toBeInstanceOf(GatewayError);
  });
});
