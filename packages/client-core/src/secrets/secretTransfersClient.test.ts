import { describe, it, expect, vi, afterEach } from "vitest";
import { answerSecretTransfer, listSecretTransfers, timeLeftWords } from "./secretTransfersClient";
import { GatewayError } from "../api/client";

// The secret transfers client (Secret Handoff, issue #2943) at the fetch level: the routes, the methods and the body
// field names the Gateway reads (SecretTransferEndpoints, SecretTransferAnswerRequest), and a refusal's own sentence
// arriving as the error. A wrong route or a renamed field turns these red; the card tests mock this file out.

function jsonResponse(status: number, body: unknown): Response {
  const text = JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => "application/json" },
    json: async () => JSON.parse(text),
    text: async () => text,
  } as unknown as Response;
}

const realFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = realFetch;
});

function stubFetch(response: Response) {
  const fetchMock = vi.fn(async () => response);
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  return fetchMock;
}

function call(fetchMock: ReturnType<typeof stubFetch>) {
  const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
  return { url: String(url), init: init ?? {} };
}

describe("secretTransfersClient", () => {
  it("listSecretTransfers_ReadsGetGatewaySecretsTransfers", async () => {
    const fetchMock = stubFetch(jsonResponse(200, { transfers: [{ transferId: "t1", summary: "a from X to Y" }], finishedWithinHours: 24 }));

    const list = await listSecretTransfers();

    const { url, init } = call(fetchMock);
    expect(url).toBe("/gateway/secrets/transfers");
    expect(init.method ?? "GET").toBe("GET");
    expect(list.transfers[0].summary).toBe("a from X to Y");
  });

  it("answerSecretTransfer_Approve_PostsApproveAndWhere", async () => {
    const fetchMock = stubFetch(jsonResponse(200, { transfer: { transferId: "t/1", statusText: "Approved on the phone. Delivering." }, note: "Approved." }));

    const result = await answerSecretTransfer("t/1", true, "phone");

    const { url, init } = call(fetchMock);
    expect(url).toBe("/gateway/secrets/transfers/t%2F1/answer");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ approve: true, where: "phone" });
    expect(result.transfer.statusText).toBe("Approved on the phone. Delivering.");
  });

  it("answerSecretTransfer_Deny_PostsDenyAndWhere", async () => {
    const fetchMock = stubFetch(jsonResponse(200, { transfer: { transferId: "t1" }, note: "Denied." }));

    await answerSecretTransfer("t1", false, "badge");

    expect(JSON.parse(String(call(fetchMock).init.body))).toEqual({ deny: true, where: "badge" });
  });

  it("answerSecretTransfer_Refused_ThrowsTheGatewaysSentence", async () => {
    stubFetch(jsonResponse(409, { code: "already_answered", error: "This transfer was already approved in the cc-secrets window. Nothing changed." }));

    const err = await answerSecretTransfer("t1", true, "cockpit").catch((e: unknown) => e);

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).message).toBe("This transfer was already approved in the cc-secrets window. Nothing changed.");
  });

  it("timeLeftWords_FormatsTheMinutesLeft", () => {
    const now = Date.parse("2026-10-10T12:00:00Z");
    expect(timeLeftWords("2026-10-10T12:14:30Z", now)).toBe("14 minutes left");
    expect(timeLeftWords("2026-10-10T12:01:10Z", now)).toBe("1 minute left");
    expect(timeLeftWords("2026-10-10T12:00:40Z", now)).toBe("under a minute left");
  });
});
