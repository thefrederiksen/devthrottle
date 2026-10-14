import { afterEach, describe, expect, it, vi } from "vitest";
import { gatewayErrorMessage } from "../api/client";
import {
  bringBackFactoryBrief,
  FactoryBriefRefusal,
  getFactoryBriefEditor,
  getFactoryBriefHistory,
  getFactoryBriefVersion,
  publishFactoryBrief,
  saveFactoryBriefDraft,
} from "./factoryBriefClient";

// The client the seat page's brief panel reads and writes through (Factory Seats mission, phase 2b). What is held
// down: the views are read from the owner's screen routes and the writes go to the brief's own routes; every save
// states the version it was made against; a bring back states the newest version the owner was looking at; and a
// refusal comes back as the Gateway's own sentence, carrying the newer version when the Gateway sent one - the
// thing the owner needs in order to merge instead of overwriting.

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const VERSION = {
  factory: "warmforward",
  seat: "scout",
  version: 4,
  text: "You are the Scout.",
  state: "draft",
  authorKind: "session",
  authorId: "73000000-0000-4000-8000-000000000001",
  writtenAtUtc: "2026-10-13T15:00:00Z",
  publishedAtUtc: null,
  publishedBy: null,
  newestVersion: 4,
  publishedVersion: 3,
};

describe("the factory brief client", () => {
  const realFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = realFetch;
    vi.restoreAllMocks();
  });

  function capture(response: () => Response) {
    const fetchMock = vi.fn(async () => response());
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    return () => {
      const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
      return { url, init, body: init.body === undefined ? undefined : JSON.parse(String(init.body)) };
    };
  }

  it("reads the editor, a page of the history and one version from the owner's screen routes, escaping both ids", async () => {
    let call = capture(() => jsonResponse({ heading: "Edit the brief of Scout", text: "x", expectedVersion: 3 }));
    const editor = await getFactoryBriefEditor("warm forward", "night/watch");
    expect(call().url).toBe("/gateway/factories/warm%20forward/seats/night%2Fwatch/brief/editor");
    expect(call().init.method).toBe("GET");
    expect(call().init.body).toBeUndefined();
    expect(editor.expectedVersion).toBe(3);

    call = capture(() => jsonResponse({ rows: [], nextBefore: null }));
    await getFactoryBriefHistory("warmforward", "scout", null);
    expect(call().url).toBe("/gateway/factories/warmforward/seats/scout/brief/history");

    call = capture(() => jsonResponse({ version: 7, text: "seven" }));
    const one = await getFactoryBriefVersion("warmforward", "scout", 7);
    expect(call().url).toBe("/gateway/factories/warmforward/seats/scout/brief/versions/7");
    expect(one.text).toBe("seven");
  });

  it("asks for an older page with exactly the starting version the Gateway named", async () => {
    const call = capture(() => jsonResponse({ rows: [], nextBefore: null }));

    await getFactoryBriefHistory("warmforward", "scout", 26);

    expect(call().url).toBe("/gateway/factories/warmforward/seats/scout/brief/history?before=26");
  });

  it("saves a draft to the brief's own route, stating the version the text was started from", async () => {
    const call = capture(() => jsonResponse(VERSION));

    const saved = await saveFactoryBriefDraft("warmforward", "scout", "You are the Scout.", 3);

    expect(call().url).toBe("/factory-briefs/warmforward/scout/draft");
    expect(call().init.method).toBe("PUT");
    expect(call().body).toEqual({ text: "You are the Scout.", expectedVersion: 3 });
    expect(saved.version).toBe(4);
  });

  it("a first draft states version 0, which is not left out as if it were nothing", async () => {
    const call = capture(() => jsonResponse({ ...VERSION, version: 1 }));

    await saveFactoryBriefDraft("warmforward", "scout", "first", 0);

    expect(call().body).toEqual({ text: "first", expectedVersion: 0 });
  });

  it("publishes one version, and brings one back stating the newest version the owner was looking at", async () => {
    let call = capture(() => jsonResponse({ ...VERSION, state: "published" }));
    await publishFactoryBrief("warmforward", "scout", 4);
    expect(call().url).toBe("/factory-briefs/warmforward/scout/publish");
    expect(call().init.method).toBe("POST");
    expect(call().body).toEqual({ version: 4 });

    call = capture(() => jsonResponse({ ...VERSION, version: 6, state: "published" }));
    await bringBackFactoryBrief("warmforward", "scout", 2, 5);
    expect(call().url).toBe("/factory-briefs/warmforward/scout/restore");
    expect(call().init.method).toBe("POST");
    expect(call().body).toEqual({ version: 2, expectedVersion: 5 });
  });

  it("a stale save is thrown as a refusal carrying the Gateway's sentence, its outcome and the newer version", async () => {
    const newer = { ...VERSION, version: 5, text: "What a session wrote meanwhile." };
    capture(() =>
      jsonResponse(
        { error: "the brief of 'scout' is at version 5, not 3; read it again, merge, and save once more", outcome: "Stale", current: newer },
        409,
      ),
    );

    const err = await saveFactoryBriefDraft("warmforward", "scout", "mine", 3).then(
      () => null,
      (e: unknown) => e,
    );

    expect(err).toBeInstanceOf(FactoryBriefRefusal);
    const refusal = err as FactoryBriefRefusal;
    expect(refusal.status).toBe(409);
    expect(refusal.outcome).toBe("Stale");
    expect(refusal.current?.version).toBe(5);
    expect(refusal.current?.text).toBe("What a session wrote meanwhile.");
    // What the screen shows is the Gateway's own sentence, never a line made up from the status code.
    expect(gatewayErrorMessage(refusal, "save the draft")).toContain("the brief of 'scout' is at version 5, not 3");
  });

  it("a refused publish keeps the Gateway's sentence and has no version to merge", async () => {
    capture(() =>
      jsonResponse({ error: "version 3 is already the published brief of 'scout'", outcome: "AlreadyPublished", current: null }, 409),
    );

    const err = (await publishFactoryBrief("warmforward", "scout", 3).then(
      () => null,
      (e: unknown) => e,
    )) as FactoryBriefRefusal;

    expect(err).toBeInstanceOf(FactoryBriefRefusal);
    expect(err.outcome).toBe("AlreadyPublished");
    expect(err.current).toBeNull();
    expect(gatewayErrorMessage(err, "publish this brief")).toContain("version 3 is already the published brief of 'scout'");
  });

  it("a refusal with a detail shows both sentences, and one with no outcome still says why", async () => {
    capture(() =>
      jsonResponse(
        {
          error: "a seat's brief is written by a person, or by a session of its factory",
          detail: "Every version records who wrote it. This request carried neither a session's key nor a person's device key.",
        },
        403,
      ),
    );

    const err = (await saveFactoryBriefDraft("warmforward", "scout", "mine", 0).then(
      () => null,
      (e: unknown) => e,
    )) as FactoryBriefRefusal;

    expect(err.status).toBe(403);
    expect(err.outcome).toBeNull();
    expect(err.message).toBe(
      "a seat's brief is written by a person, or by a session of its factory. Every version records who wrote it. This request carried neither a session's key nor a person's device key.",
    );
  });

  it("an answer that is a web page is refused, never read as a saved draft", async () => {
    globalThis.fetch = vi.fn(async () => new Response("<!doctype html><html></html>", { status: 200, headers: { "Content-Type": "text/html" } })) as unknown as typeof fetch;

    await expect(saveFactoryBriefDraft("warmforward", "scout", "mine", 3)).rejects.toThrow(/answered with a web page instead of data/);
    await expect(getFactoryBriefEditor("warmforward", "scout")).rejects.toThrow(/answered with a web page instead of data/);
  });

  it("an error body that is not JSON still becomes a refusal that says what the Gateway sent", async () => {
    globalThis.fetch = vi.fn(async () => new Response("Bad Gateway", { status: 502 })) as unknown as typeof fetch;

    const err = (await publishFactoryBrief("warmforward", "scout", 3).then(
      () => null,
      (e: unknown) => e,
    )) as FactoryBriefRefusal;

    expect(err).toBeInstanceOf(FactoryBriefRefusal);
    expect(err.status).toBe(502);
    expect(err.message).toBe("Bad Gateway");
  });
});
