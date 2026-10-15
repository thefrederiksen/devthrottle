import { afterEach, describe, expect, it, vi } from "vitest";
import {
  attachmentNameOf,
  getFactoryRecordsDocument,
  getFactoryRecordsDocumentHistory,
  getFactoryRecordsJournal,
  getFactoryRecordsTab,
} from "./factoryRecordsClient";

// The client the factory page's Records tab reads through (Factory Definition mission, phase 6). What is held down:
// every view is read from the owner's screen routes; a document's name, itself a path, travels escaped in the query;
// and a refusal is the Gateway's own sentence.

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("the factory records client", () => {
  const realFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = realFetch;
    vi.restoreAllMocks();
  });

  function capture(response: () => Response) {
    const fetchMock = vi.fn(async () => response());
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    return () => (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[0];
  }

  it("reads the tab and a journal from the owner's screen routes, the factory's own journal without a seat", async () => {
    let url = capture(() => jsonResponse({ heading: "The records of QA" }));
    await getFactoryRecordsTab("qa outreach");
    expect(url()).toBe("/gateway/factories/qa%20outreach/records");

    url = capture(() => jsonResponse({ entries: [] }));
    await getFactoryRecordsJournal("qa-outreach", "writer");
    expect(url()).toBe("/gateway/factories/qa-outreach/records/journal?seat=writer");

    url = capture(() => jsonResponse({ entries: [] }));
    await getFactoryRecordsJournal("qa-outreach", null);
    expect(url()).toBe("/gateway/factories/qa-outreach/records/journal");
  });

  it("sends a document's name escaped in the query, with a version or a page when one is asked for", async () => {
    let url = capture(() => jsonResponse({ text: "" }));
    await getFactoryRecordsDocument("qa-outreach", "outreach/ledger 1.json", null);
    expect(url()).toBe("/gateway/factories/qa-outreach/records/documents/read?name=outreach%2Fledger%201.json");

    url = capture(() => jsonResponse({ text: "" }));
    await getFactoryRecordsDocument("qa-outreach", "focus.md", 2);
    expect(url()).toBe("/gateway/factories/qa-outreach/records/documents/read?name=focus.md&version=2");

    url = capture(() => jsonResponse({ rows: [] }));
    await getFactoryRecordsDocumentHistory("qa-outreach", "focus.md", 21);
    expect(url()).toBe("/gateway/factories/qa-outreach/records/documents/history?name=focus.md&before=21");
  });

  it("throws the Gateway's own sentence when it refuses", async () => {
    capture(() => jsonResponse({ error: "qa-outreach has no working document 'nope.md'" }, 404));
    const refused = await getFactoryRecordsDocument("qa-outreach", "nope.md", null).catch((e: unknown) => e);
    expect((refused as Error).message).toBe("qa-outreach has no working document 'nope.md'");
  });

  it("refuses an answer that is a web page instead of data", async () => {
    capture(() => new Response("<html></html>", { status: 200, headers: { "Content-Type": "text/html" } }));
    const refused = await getFactoryRecordsTab("qa-outreach").catch((e: unknown) => e);
    expect((refused as Error).message).toContain("does not show a factory's records");
  });

  it("reads an attachment's name from the answer's Content-Disposition", () => {
    expect(attachmentNameOf("attachment; filename=home.png; filename*=UTF-8''home.png")).toBe("home.png");
    expect(attachmentNameOf("attachment")).toBeNull();
  });
});
