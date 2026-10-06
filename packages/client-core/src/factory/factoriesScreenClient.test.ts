import { describe, it, expect, vi, afterEach } from "vitest";
import { gatewayErrorMessage, GatewayError } from "../api/client";
import { getFactoriesList, startFactoryTalk, talkRefusalReason } from "./factoriesScreenClient";

// The Factories screen's client. What is held down:
//   * a Talk refusal shows every sentence the Gateway wrote: the Talk route's `{ error }`, the session create's
//     problem-details `{ title, detail }`, and both when a body carries an error and a detail;
//   * a 2XX that is not JSON - a Gateway older than the screen answering with the app's HTML shell - is refused
//     with a sentence that says so, on the Talk request exactly as on the reads.

const TARGET = { label: "Talk to Nora Hale", busyLabel: "Starting...", factoryId: "warmforward", seatId: "nora-hale" };

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
