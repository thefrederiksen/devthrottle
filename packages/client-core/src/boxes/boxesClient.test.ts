import { afterEach, describe, expect, it, vi } from "vitest";
import { gatewayErrorMessage } from "../api/client";
import { getBoxes, getDesktopTicket } from "./boxesClient";

// The Boxes screen's two calls (Factory Boxes mission, phase 1). Fakes only: fetch is stubbed per test, so nothing
// leaves the process. What is pinned: the routes and methods, the contract shape carried verbatim, the Gateway's own
// sentence on a failure, and an answer that breaks the contract thrown rather than drawn as a guess.

const ROW = {
  boxId: "demo-desktop-soren",
  displayName: "Soren's demo desktop",
  directorId: "4f6c2a3e-0000-0000-0000-000000000000",
  directorName: "devlinux slot 1",
  machineName: "devlinux",
  state: "Online",
  relativeDesktopPath: "/boxes/demo-desktop-soren/desktop/",
};

type Call = { url: string; init: RequestInit | undefined };

function stubFetch(status: number, body: string, contentType = "application/json"): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, init });
      return new Response(body, { status, headers: { "Content-Type": contentType } });
    }),
  );
  return calls;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getBoxes", () => {
  it("getBoxes_ContractAnswer_ReadsGetBoxesAndCarriesEveryFieldVerbatim", async () => {
    const calls = stubFetch(200, JSON.stringify({ count: 1, boxes: [ROW] }));

    const list = await getBoxes();

    expect(calls[0].url).toBe("/boxes");
    expect(calls[0].init?.method ?? "GET").toBe("GET");
    expect(list).toEqual({ count: 1, boxes: [ROW] });
  });

  it("getBoxes_EmptyList_IsCountZeroAndNoRows", async () => {
    stubFetch(200, JSON.stringify({ count: 0, boxes: [] }));

    expect(await getBoxes()).toEqual({ count: 0, boxes: [] });
  });

  it("getBoxes_GatewayRefuses_ThrowsWithTheGatewaysSentence", async () => {
    stubFetch(503, JSON.stringify({ error: "The Gateway is starting." }));

    const err = await getBoxes().catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "read the boxes")).toContain("The Gateway is starting.");
  });

  it("getBoxes_RowMissingItsBoxId_ThrowsRatherThanDrawingAGuess", async () => {
    const { boxId: _dropped, ...withoutId } = ROW;
    stubFetch(200, JSON.stringify({ count: 1, boxes: [withoutId] }));

    const err = await getBoxes().catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "read the boxes")).toBe(
      "The Gateway sent a list of boxes this app cannot read: box 1 is missing a field.",
    );
  });

  it("getBoxes_BodyNotJson_ThrowsSayingSo", async () => {
    stubFetch(200, "<html></html>", "text/html");

    const err = await getBoxes().catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "read the boxes")).toContain("its answer is not valid JSON");
  });
});

describe("getDesktopTicket", () => {
  it("getDesktopTicket_Answer_PostsToTheBoxsTicketRouteAndReturnsTheUrlVerbatim", async () => {
    const url = "https://desktop.example.test/boxes/demo-desktop-soren/desktop/?ticket=abc123";
    const calls = stubFetch(200, JSON.stringify({ url, expiresAtUtc: "2026-10-20T08:30:00Z" }));

    const ticket = await getDesktopTicket("demo-desktop-soren");

    expect(calls[0].url).toBe("/boxes/demo-desktop-soren/desktop-ticket");
    expect(calls[0].init?.method).toBe("POST");
    expect(ticket).toEqual({ url, expiresAtUtc: "2026-10-20T08:30:00Z" });
  });

  it("getDesktopTicket_BoxIdIsEncodedIntoThePath", async () => {
    const calls = stubFetch(200, JSON.stringify({ url: "https://x.test/", expiresAtUtc: "2026-10-20T08:30:00Z" }));

    await getDesktopTicket("a/b");

    expect(calls[0].url).toBe("/boxes/a%2Fb/desktop-ticket");
  });

  it("getDesktopTicket_GatewayRefuses_ThrowsWithTheGatewaysSentence", async () => {
    stubFetch(404, JSON.stringify({ error: "No connected Director in your account hosts a desktop for that box." }));

    const err = await getDesktopTicket("demo-desktop-soren").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "open the desktop")).toContain(
      "No connected Director in your account hosts a desktop for that box.",
    );
  });

  it("getDesktopTicket_AnswerWithoutAnAddress_Throws", async () => {
    stubFetch(200, JSON.stringify({ expiresAtUtc: "2026-10-20T08:30:00Z" }));

    const err = await getDesktopTicket("demo-desktop-soren").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "open the desktop")).toBe(
      "The Gateway sent a desktop ticket this app cannot read: it has no address to open.",
    );
  });
});
