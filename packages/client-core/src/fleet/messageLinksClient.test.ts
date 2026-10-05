import { describe, it, expect, vi, afterEach } from "vitest";
import { listMessageLinks, removeMessageLink, setUpMessageLink } from "./messageLinksClient";
import { GatewayError } from "../api/client";

// The message links client (issue #3548) at the fetch level: the routes, the methods and the body field names
// the Gateway reads (FleetMessageLinkEndpoints, FleetMessageLinkCreateRequest), and a refusal's own sentence
// arriving as the error. A wrong route or a renamed field turns these red; the dialog tests mock this file out.

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

describe("messageLinksClient", () => {
  it("sets up a link with POST /fleet/links and the three fields the Gateway reads", async () => {
    const fetchMock = stubFetch(jsonResponse(201, { link: { linkId: "l1", summary: "One message, no reply. Live." }, replaced: [] }));
    const result = await setUpMessageLink("sender-1", "recipient-1", "once");

    const { url, init } = call(fetchMock);
    expect(url).toBe("/fleet/links");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ senderSessionId: "sender-1", recipientSessionId: "recipient-1", amount: "once" });
    expect(result.link.summary).toBe("One message, no reply. Live.");
  });

  it("lists links with GET /fleet/links", async () => {
    const fetchMock = stubFetch(jsonResponse(200, { links: [], stoppedWithinDays: 30 }));
    const list = await listMessageLinks();
    const { url, init } = call(fetchMock);
    expect(url).toBe("/fleet/links");
    expect(init.method ?? "GET").toBe("GET");
    expect(list.stoppedWithinDays).toBe(30);
  });

  it("removes a link with DELETE /fleet/links/{id}, the id escaped", async () => {
    const fetchMock = stubFetch(jsonResponse(200, { linkId: "a/b", status: "removed", summary: "Talk as much as needed, both ways. Removed." }));
    const after = await removeMessageLink("a/b");
    const { url, init } = call(fetchMock);
    expect(url).toBe("/fleet/links/a%2Fb");
    expect(init.method).toBe("DELETE");
    expect(after.status).toBe("removed");
  });

  it("throws a refusal with the Gateway's own sentence", async () => {
    stubFetch(
      jsonResponse(409, {
        code: "already_related",
        error: "One of these sessions started the other, so they may already message each other. No link was set up.",
      }),
    );
    const err = await setUpMessageLink("a", "b", "ongoing").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).status).toBe(409);
    expect((err as GatewayError).serverReason).toBe(
      "One of these sessions started the other, so they may already message each other. No link was set up.",
    );
  });
});
