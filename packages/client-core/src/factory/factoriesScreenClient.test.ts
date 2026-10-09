import { describe, it, expect, vi, afterEach } from "vitest";
import { gatewayErrorMessage, GatewayError } from "../api/client";
import {
  getFactoriesList,
  runFactoryOwnerAction,
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
