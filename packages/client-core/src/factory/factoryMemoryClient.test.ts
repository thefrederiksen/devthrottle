import { afterEach, describe, expect, it, vi } from "vitest";
import { createSession, gatewayErrorMessage } from "../api/client";
import {
  deleteFactoryMemoryNote,
  FactoryMemoryRefusal,
  getFactoryMemoryHistory,
  getFactoryMemoryNote,
  listFactoryMemory,
  restoreFactoryMemoryNote,
  setFactoryMemoryNote,
} from "./factoryMemoryClient";

// The client the Cockpit's Memory tab reads and writes through (Factory Memory mission, phase 3b). What is held
// down: every call names the factory (a person is in no factory, so the Gateway cannot read it from the caller),
// every write states the version it was made against, and a stale write comes back carrying the note as it
// stands now - the thing the owner needs to merge instead of overwriting.

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const NOTE = {
  factory: "website-factory",
  name: "domains",
  version: 3,
  text: "Use the registrar check before buying.",
  deleted: false,
  authorKind: "session",
  authorId: "4baef30d-281d-44c9-9a86-654cef33ecfa",
  writtenAtUtc: "2026-09-28T12:00:00Z",
};

describe("the factory memory client", () => {
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

  it("names the factory on every read, and escapes the note's name in the path", async () => {
    let call = capture(() => jsonResponse({ factory: "website-factory", notes: [NOTE], bytes: 38, maxBytes: 524288, maxNotes: 100 }));
    const list = await listFactoryMemory("website-factory");
    expect(call().url).toBe("/factory-memory/notes?factory=website-factory");
    expect(call().init.method).toBe("GET");
    expect(list.notes[0].name).toBe("domains");

    call = capture(() => jsonResponse(NOTE));
    await getFactoryMemoryNote("website-factory", "a/b note");
    expect(call().url).toBe("/factory-memory/notes/a%2Fb%20note?factory=website-factory");

    call = capture(() => jsonResponse({ factory: "website-factory", name: "domains", versions: [NOTE] }));
    await getFactoryMemoryHistory("website-factory", "domains");
    expect(call().url).toBe("/factory-memory/notes/domains/history?factory=website-factory");
  });

  it("sends the version a correction was made against, and the version a delete was made against", async () => {
    let call = capture(() => jsonResponse({ ...NOTE, version: 4 }));
    await setFactoryMemoryNote("website-factory", "domains", "corrected", 3);
    expect(call().url).toBe("/factory-memory/notes/domains?factory=website-factory");
    expect(call().init.method).toBe("PUT");
    expect(call().body).toEqual({ text: "corrected", expectedVersion: 3 });

    call = capture(() => jsonResponse({ ...NOTE, version: 4, text: null, deleted: true }));
    await deleteFactoryMemoryNote("website-factory", "domains", 3);
    expect(call().init.method).toBe("DELETE");
    expect(call().body).toEqual({ expectedVersion: 3 });
  });

  it("asks for a restore by the version to put back, AND says which version it was looking at", async () => {
    // The second number is what makes a stale restore refusable: an agent can write while the owner is reading the
    // history, and without it the restore would bury that version with nobody seeing (review finding 4).
    const call = capture(() => jsonResponse({ ...NOTE, version: 5, authorKind: "person" }));
    const restored = await restoreFactoryMemoryNote("website-factory", "domains", 2, 4);
    expect(call().url).toBe("/factory-memory/notes/domains/restore?factory=website-factory");
    expect(call().init.method).toBe("POST");
    expect(call().body).toEqual({ version: 2, expectedVersion: 4 });
    expect(restored.authorKind).toBe("person");
  });

  it("turns a stale write into a refusal that carries the note as it stands now", async () => {
    const current = { ...NOTE, version: 4, text: "What a factory session wrote meanwhile." };
    capture(() => jsonResponse({ error: "the note changed since you read it", outcome: "Stale", current }, 409));

    const err = await setFactoryMemoryNote("website-factory", "domains", "mine", 3).catch((e: unknown) => e);

    expect(err).toBeInstanceOf(FactoryMemoryRefusal);
    const refusal = err as FactoryMemoryRefusal;
    expect(refusal.status).toBe(409);
    expect(refusal.outcome).toBe("Stale");
    expect(refusal.current).toEqual(current);
    // The Gateway's own sentence is what a person reads, not a line invented from the status code.
    expect(gatewayErrorMessage(refusal, "save this note")).toContain("the note changed since you read it");
  });

  it("says so when a Gateway from before this mission answers with a web page", async () => {
    globalThis.fetch = (async () =>
      new Response("<html></html>", { status: 200, headers: { "Content-Type": "text/html" } })) as unknown as typeof fetch;

    await expect(listFactoryMemory("website-factory")).rejects.toThrow(/does not serve factory memory/);
  });
});

describe("the spawn form's factory, as createSession sends it", () => {
  const realFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  function bodyOf(fetchMock: ReturnType<typeof vi.fn>): Record<string, unknown> {
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    return JSON.parse(String(init.body)) as Record<string, unknown>;
  }

  it("puts a named factory on the create request", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ sessionId: "s1" }, 201));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await createSession("dir-1", "C:\\repo", { agent: "ClaudeCode", factory: " website-factory " });

    expect(bodyOf(fetchMock).factory).toBe("website-factory");
  });

  it("sends no factory at all when the field is blank", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ sessionId: "s1" }, 201));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await createSession("dir-1", "C:\\repo", { agent: "ClaudeCode", factory: "   " });

    expect("factory" in bodyOf(fetchMock)).toBe(false);
  });
});
