import { afterEach, describe, expect, it, vi } from "vitest";
import {
  FactoryDefinitionRefusal,
  getFactoryDefinitionHistory,
  getFactoryDefinitionTab,
  importFactoryDefinitionZip,
  publishFactoryDefinition,
  restoreFactoryDefinition,
  zipNameOf,
  type FactoryDefinitionAction,
} from "./factoryDefinitionClient";

// The client the factory page's Definition tab reads and writes through (Factory Definition mission, phase 6). What is
// held down: the views are read from the owner's screen routes; publish and restore go to the definition's own routes
// with exactly the versions the confirm named; an import sends the zip as it is; and a refusal comes back as the
// Gateway's own sentence carrying every finding.

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const ACTION: FactoryDefinitionAction = {
  version: 2,
  expectedVersion: 5,
  replaces: 3,
  label: "Restore",
  busyLabel: "Restoring...",
  confirmTitle: "Restore?",
  confirmLines: [],
  confirmLabel: "Restore",
  doneText: "Restored.",
};

describe("the factory definition client", () => {
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
      return { url, init };
    };
  }

  it("reads the tab and a page of the history from the owner's screen routes, escaping the id", async () => {
    let call = capture(() => jsonResponse({ heading: "The definition of QA" }));
    await getFactoryDefinitionTab("qa outreach");
    expect(call().url).toBe("/gateway/factories/qa%20outreach/definition");

    call = capture(() => jsonResponse({ rows: [] }));
    await getFactoryDefinitionHistory("qa-outreach", 7);
    expect(call().url).toBe("/gateway/factories/qa-outreach/definition/history?before=7");
  });

  it("publishes and restores through the definition's own routes with the versions the confirm named", async () => {
    let call = capture(() => jsonResponse({ version: 5, findings: [], changes: null, applied: [] }));
    await publishFactoryDefinition("qa-outreach", { ...ACTION, version: 5, replaces: 3 });
    expect(call().url).toBe("/factory-definitions/qa-outreach/publish");
    expect(JSON.parse(String(call().init.body))).toEqual({ version: 5, replaces: 3 });

    call = capture(() => jsonResponse({ version: 6, findings: [], changes: null, applied: [] }));
    await restoreFactoryDefinition("qa-outreach", ACTION);
    expect(call().url).toBe("/factory-definitions/qa-outreach/restore");
    expect(JSON.parse(String(call().init.body))).toEqual({ version: 2, expectedVersion: 5, replaces: 3 });
  });

  it("throws a refusal carrying the Gateway's sentence and every finding", async () => {
    capture(() =>
      jsonResponse(
        {
          error: "Version 5 was not published: the check found 1 error.",
          outcome: "CheckFailed",
          findings: [{ severity: "error", file: "seats/writer.yaml", line: 9, message: "No." }],
        },
        422,
      ),
    );
    const refused = await publishFactoryDefinition("qa-outreach", ACTION).catch((e: unknown) => e);
    expect(refused).toBeInstanceOf(FactoryDefinitionRefusal);
    const r = refused as FactoryDefinitionRefusal;
    expect(r.message).toBe("Version 5 was not published: the check found 1 error.");
    expect(r.outcome).toBe("CheckFailed");
    expect(r.findings).toEqual([{ severity: "error", file: "seats/writer.yaml", line: 9, message: "No." }]);
  });

  it("sends an import's zip as it is, to the address the Gateway gave", async () => {
    const call = capture(() => jsonResponse({ factory: "qa-outreach", version: 1, doneText: "Imported.", findings: [] }));
    const zip = new Blob(["PK"], { type: "application/zip" });
    const imported = await importFactoryDefinitionZip("/gateway/factories/qa-outreach/definition/import", zip);
    expect(call().init.method).toBe("POST");
    expect(call().init.body).toBe(zip);
    expect((call().init.headers as Record<string, string>)["Content-Type"]).toBe("application/zip");
    expect(imported.doneText).toBe("Imported.");
  });

  it("reads the zip's name from the answer's Content-Disposition, in both forms the Gateway may send", () => {
    expect(zipNameOf("attachment; filename=qa-outreach-definition-v3.zip; filename*=UTF-8''qa-outreach-definition-v3.zip")).toBe(
      "qa-outreach-definition-v3.zip",
    );
    expect(zipNameOf('attachment; filename="a b.zip"')).toBe("a b.zip");
    expect(zipNameOf("attachment")).toBeNull();
  });
});
