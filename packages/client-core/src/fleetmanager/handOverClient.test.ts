import { afterEach, describe, expect, it, vi } from "vitest";
import { GatewayError } from "../api/client";
import { handOverSession, runHandOver } from "./handOverClient";

afterEach(() => vi.unstubAllGlobals());

describe("handOverSession", () => {
  it("posts the session and the direction, and returns the Gateway's answer", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ sessionId: "s1", to: "fleet-manager", sentence: "It is the Fleet Manager's now. (fake)" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await handOverSession("s1", "fleet-manager");

    expect(result.sentence).toBe("It is the Fleet Manager's now. (fake)");
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/gateway/fleet-manager/hand-over");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ session: "s1", to: "fleet-manager" });
  });

  it("throws the Gateway's refusal sentence", async () => {
    vi.stubGlobal("fetch", vi.fn(async () =>
      new Response(JSON.stringify({ error: "It is owned by another running session. (fake)" }), { status: 409 }),
    ));

    const err = await handOverSession("s1", "fleet-manager").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).message).toBe("It is owned by another running session. (fake)");
  });
});

describe("runHandOver", () => {
  it("reports the success sentence", async () => {
    const out = await runHandOver("s1", "owner", async () => ({ sessionId: "s1", to: "owner", sentence: "Yours again. (fake)" }));
    expect(out).toEqual({ ok: true, sentence: "Yours again. (fake)" });
  });

  it("reports a refusal as the Gateway's words, never throwing", async () => {
    const out = await runHandOver("s1", "owner", async () => {
      throw new GatewayError(409, "Already yours. (fake)");
    });
    expect(out).toEqual({ ok: false, error: "Already yours. (fake)" });
  });
});
