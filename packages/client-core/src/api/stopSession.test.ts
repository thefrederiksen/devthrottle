import { afterEach, describe, expect, it, vi } from "vitest";

// stopSession is THE way any surface ends a session (mission "Stop a session", Ruling 5). What is pinned
// here is the hop itself: the route and body it sends, the fact that every verdict the Gateway can send
// comes back as a resolved answer rather than a thrown error, and that the Gateway's own refusal sentence
// survives the hop instead of being replaced by a status number.
//
// The connection-health module is mocked so the shared transport does not touch a real signal, exactly as
// hold.test.ts does.
const health = vi.hoisted(() => ({ reachable: vi.fn(), unreachable: vi.fn() }));
vi.mock("../connection/health", () => ({
  reportGatewayReachable: () => health.reachable(),
  reportGatewayUnreachable: () => health.unreachable(),
}));

import { GatewayError, stopSession } from "./client";

function jsonResponse(body: unknown, status = 200): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    // The shared transport reads a header on the 5xx path to tell a Gateway fault from a Director
    // fault, so the stub carries a real header bag rather than nothing.
    headers: new Headers(),
    json: async () => body,
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

// A complete Gateway answer, in the shape SessionStopResponse serialises to.
function answer(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    verdict: "stopped",
    headline: "stopped 9c41e7a2 - process 51884 ended, row removed",
    details: ["reason: spawned into the wrong mode"],
    sessionId: "9c41e7a2-0000-4000-8000-000000000000",
    shortId: "9c41e7a2",
    processId: 51884,
    processEnded: true,
    rowRemoved: true,
    worktreePath: null,
    worktreeHadUncommittedChanges: null,
    reason: "spawned into the wrong mode",
    stoppedBy: "session 0022aa52",
    killed: true,
    removed: true,
    ...over,
  };
}

describe("stopSession posts the reason to the stop route", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it("calls POST /sessions/{sid}/stop and sends the reason in the body", async () => {
    let seenUrl = "";
    let seenMethod = "";
    let seenBody = "";
    globalThis.fetch = ((input: unknown, init?: RequestInit) => {
      seenUrl = String(input);
      seenMethod = String(init?.method ?? "");
      seenBody = String(init?.body ?? "");
      return Promise.resolve(jsonResponse(answer()));
    }) as unknown as typeof fetch;

    await stopSession("9c41e7a2", "spawned into the wrong mode");

    expect(seenUrl).toContain("/sessions/9c41e7a2/stop");
    expect(seenMethod).toBe("POST");
    expect(JSON.parse(seenBody)).toEqual({ reason: "spawned into the wrong mode" });
  });

  it("escapes an identifier that needs it, so a name with a space still reaches one route", async () => {
    let seenUrl = "";
    globalThis.fetch = ((input: unknown) => {
      seenUrl = String(input);
      return Promise.resolve(jsonResponse(answer({ verdict: "notOnFleet" })));
    }) as unknown as typeof fetch;

    await stopSession("Stop a session", "typed a name by mistake");

    expect(seenUrl).toContain("/sessions/Stop%20a%20session/stop");
  });

  it("returns the Gateway's words and facts, and never invents the unknown worktree state", async () => {
    globalThis.fetch = (() =>
      Promise.resolve(
        jsonResponse(
          answer({
            details: [
              "the worktree D:\\Repos\\scratch was left untouched - whether it has uncommitted changes could not be determined",
              "reason: spawned into the wrong mode",
            ],
            worktreePath: "D:\\Repos\\scratch",
            worktreeHadUncommittedChanges: null,
          }),
        ),
      )) as unknown as typeof fetch;

    const outcome = await stopSession("9c41e7a2", "spawned into the wrong mode");

    expect(outcome.headline).toBe("stopped 9c41e7a2 - process 51884 ended, row removed");
    expect(outcome.details).toEqual([
      "the worktree D:\\Repos\\scratch was left untouched - whether it has uncommitted changes could not be determined",
      "reason: spawned into the wrong mode",
    ]);
    expect(outcome.processId).toBe(51884);
    expect(outcome.processEnded).toBe(true);
    // "could not be determined" is NOT "clean". A false here would tell every reader downstream that the
    // tree is clean, which is the one thing a failed probe does not know.
    expect(outcome.worktreeHadUncommittedChanges).toBeNull();
  });
});

describe("every verdict the Gateway can send is a success", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  // Including a word this client has never heard of: a fifth verdict added on the Gateway must arrive as
  // an ordinary answer, not as a failure, because no client is allowed to know how many there are.
  const verdicts = ["stopped", "alreadyStopped", "notOnFleet", "stoppedNotDescribed", "someVerdictInvented2027"];

  for (const verdict of verdicts) {
    it(`resolves for "${verdict}" rather than throwing`, async () => {
      globalThis.fetch = (() =>
        Promise.resolve(jsonResponse(answer({ verdict, headline: `the Gateway said something about ${verdict}` })))) as unknown as typeof fetch;

      const outcome = await stopSession("9c41e7a2", "a reason");

      expect(outcome.verdict).toBe(verdict);
      expect(outcome.headline).toBe(`the Gateway said something about ${verdict}`);
    });
  }
});

describe("stopSession carries the Gateway's own sentence out of a refusal", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it("throws the Gateway's refusal sentence for a stop with no reason (400)", async () => {
    // The Gateway's actual sentence, from SessionStopFold.ReasonMissing.
    const refusal =
      "A stop needs a reason. Say why this session is being stopped - it is recorded with the stop, and "
      + "it is how anyone reading the trail later knows what happened.";
    globalThis.fetch = (() =>
      Promise.resolve(jsonResponse({ error: refusal }, 400))) as unknown as typeof fetch;

    const err = await stopSession("9c41e7a2", " ").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).status).toBe(400);
    // The sentence itself, not "POST failed: 400". This is the whole reason the throw goes through
    // GatewayError.from rather than building an error by hand.
    expect((err as GatewayError).message).toBe(refusal);
  });

  it("throws on an ordinary failure", async () => {
    globalThis.fetch = (() =>
      Promise.resolve(jsonResponse({ error: "the Director on SORENLAPTOP could not be reached" }, 502))) as unknown as typeof fetch;

    const err = await stopSession("9c41e7a2", "a reason").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).message).toContain("the Director on SORENLAPTOP could not be reached");
  });

  it("fails loudly when a 200 carries no headline, rather than showing an empty answer", async () => {
    globalThis.fetch = (() =>
      Promise.resolve(jsonResponse({ verdict: "stopped", details: [] }))) as unknown as typeof fetch;

    const err = await stopSession("9c41e7a2", "a reason").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    // It says the ANSWER could not be read - it does not claim the stop failed, because it did not.
    expect((err as GatewayError).message).toContain("without the words that say what happened");
  });

  // THE FINDING (inspection 1, I5), PINNED. This message used to open "The session was stopped, but..."
  // and a 2xx does not establish that: notOnFleet arrives on the same status and stopped nothing at
  // all, no machine having been asked. The one field that would have told them apart is the field that
  // is missing, so the client was asserting the very fact whose absence it was reporting.
  //
  // The body deliberately carries a verdict this client must not read - reading it would be the OTHER
  // half of Ruling 5 broken - and the assertion is on what is NOT said.
  it("never claims the session was stopped when the answer does not say so", async () => {
    globalThis.fetch = (() =>
      Promise.resolve(jsonResponse({ verdict: "notOnFleet", details: [] }))) as unknown as typeof fetch;

    const err = await stopSession("9c41e7a2", "a reason").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    const message = (err as GatewayError).message;
    // The claim that shipped, word for word - "The session was stopped, but the answer came back
    // without the words that describe it." - must not be there in any form.
    expect(message).not.toMatch(/the session was stopped/i);
    expect(message).not.toMatch(/the session was not stopped/i);
    // And what IS there is the hedge, which is the whole of what this client actually knows, plus
    // where to go and look. The same sentence the command line and the Director window give it.
    expect(message).toContain("cannot report whether it was stopped");
    expect(message).toContain("session list");
  });
});
