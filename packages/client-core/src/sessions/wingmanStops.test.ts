// The Wingman stops read (the Wingman inspector, phase 3): the request the route expects, the answer handed back as
// the Gateway sent it, and a refusal that carries the Gateway's own sentence rather than reading as "nothing judged".
import { afterEach, describe, expect, it, vi } from "vitest";
import { GatewayError } from "../api/client";
import { readWingmanStops } from "./wingmanStops";

const SID = "5b0c2e7a-0000-4000-8000-000000000010";

let calls: { url: string; init?: RequestInit }[] = [];

function fakeGateway(status: number, body: unknown) {
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, init });
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("readWingmanStops", () => {
  it("reads the session's stops with a GET and hands the answer back as sent", async () => {
    const body = {
      sessionId: SID,
      stops: [{ traceId: "t-1", outcome: "judged", outcomeText: "Judged" }],
      groups: [{ key: "all", label: "All", count: 1 }],
    };
    fakeGateway(200, body);

    const answer = await readWingmanStops(SID);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe(`/sessions/${SID}/wingman-stops`);
    expect(calls[0].init?.method).toBe("GET");
    expect(answer).toEqual(body);
  });

  it("escapes the session id into the path", async () => {
    fakeGateway(200, { sessionId: "a/b", stops: [], groups: [] });
    await readWingmanStops("a/b");
    expect(calls[0].url).toBe("/sessions/a%2Fb/wingman-stops");
  });

  it("throws a GatewayError carrying the Gateway's sentence when the read is refused", async () => {
    const sentence = "the Wingman's stops are served to the account's own devices only - never to a session key";
    fakeGateway(403, { error: sentence });

    const err = await readWingmanStops(SID).catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).status).toBe(403);
    expect((err as GatewayError).serverReason).toBe(sentence);
  });
});
