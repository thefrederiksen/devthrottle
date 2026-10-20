import { afterEach, describe, expect, it, vi } from "vitest";
import { GatewayError } from "../api/client";
import { getMailDeskChannels, MAIL_DESK_CHANNELS_PATH } from "./mailDeskClient";
import { noChannels, oddChannels } from "./mailDeskFixture.testkit";

// GET /gateway/mail-desk/channels read against docs/qa/mail-desk/phase-7/CONTRACT.md section 2. The client hands the
// Gateway's words over verbatim; 404 (the switch off) is an answer, not a failure; an answer that breaks the contract
// is thrown, never drawn as a guess.

const APP_SHELL = '<!doctype html><html><head><title>DevThrottle Cockpit</title></head><body><div id="root"></div></body></html>';

function respond(body: string | null, contentType: string | null, status = 200): Response {
  return new Response(body, { status, headers: contentType === null ? {} : { "Content-Type": contentType } });
}

function answering(response: Response) {
  const fetchMock = vi.fn().mockResolvedValue(response);
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function answeringJson(body: unknown) {
  return answering(respond(JSON.stringify(body), "application/json; charset=utf-8"));
}

async function rejection(): Promise<unknown> {
  return getMailDeskChannels().then(
    () => {
      throw new Error("the read was expected to throw");
    },
    (err: unknown) => err,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getMailDeskChannels", () => {
  it("getMailDeskChannels_Ok_ReadsTheActiveChannelsRouteAndHandsTheEnvelopeOverVerbatim", async () => {
    const fetchMock = answeringJson(oddChannels());

    const answer = await getMailDeskChannels();

    expect(fetchMock.mock.calls[0][0]).toBe(MAIL_DESK_CHANNELS_PATH);
    expect(MAIL_DESK_CHANNELS_PATH).toBe("/gateway/mail-desk/channels");
    expect(answer).toEqual({ kind: "channels", envelope: oddChannels() });
  });

  it("getMailDeskChannels_EmptyList_IsAnAnswerWithItsEmptySentence", async () => {
    answeringJson(noChannels());

    const answer = await getMailDeskChannels();

    expect(answer).toEqual({ kind: "channels", envelope: noChannels() });
  });

  it("getMailDeskChannels_404WithNoBody_IsTheSwitchOff", async () => {
    answering(respond(null, null, 404));

    expect(await getMailDeskChannels()).toEqual({ kind: "off" });
  });

  it("getMailDeskChannels_TheAppShell_IsAGatewayWithNoMailDesk", async () => {
    answering(respond(APP_SHELL, "text/html; charset=utf-8"));

    expect(await getMailDeskChannels()).toEqual({ kind: "off" });
  });

  it("getMailDeskChannels_ServerFault_ThrowsRatherThanLookingSwitchedOff", async () => {
    answering(respond(JSON.stringify({ error: "the store is unavailable" }), "application/json", 500));

    const err = await rejection();

    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).status).toBe(500);
  });

  it("getMailDeskChannels_NotJson_ThrowsSayingSo", async () => {
    answering(respond("{ not json", "application/json"));

    const err = await rejection();

    expect((err as Error).message).toContain("not valid JSON");
  });

  it("getMailDeskChannels_ToneOutsideTheContract_Throws", async () => {
    const body = oddChannels();
    (body.channels[1].signedIn as { tone: string }).tone = "red";
    answeringJson(body);

    const err = await rejection();

    expect((err as Error).message).toContain("red");
  });

  it("getMailDeskChannels_ModeToneOutsideTheContract_Throws", async () => {
    const body = oddChannels();
    (body.channels[0] as { modeTone: string }).modeTone = "good";
    answeringJson(body);

    expect(await rejection()).toBeInstanceOf(GatewayError);
  });

  it.each(["providerLabel", "machinesLabel", "modeLabel"] as const)(
    "getMailDeskChannels_ChannelWithout%s_ThrowsRatherThanShowingTheRawWord",
    async (field) => {
      const body = oddChannels();
      delete (body.channels[0] as unknown as Partial<Record<string, unknown>>)[field];
      answeringJson(body);

      const err = await rejection();

      expect((err as Error).message).toContain(field);
    },
  );

  it.each(["title", "summary", "emptyLabel"] as const)("getMailDeskChannels_EnvelopeWithout%s_Throws", async (field) => {
    const body = oddChannels() as unknown as Record<string, unknown>;
    delete body[field];
    answeringJson(body);

    const err = await rejection();

    expect((err as Error).message).toContain(field);
  });

  it("getMailDeskChannels_TodayWithoutItsCounts_Throws", async () => {
    const body = oddChannels();
    delete (body.channels[2].today as unknown as Partial<Record<string, unknown>>).counts;
    answeringJson(body);

    const err = await rejection();

    expect((err as Error).message).toContain("today.counts");
  });

  it("getMailDeskChannels_CountThatIsNotANumber_Throws", async () => {
    const body = oddChannels();
    (body.channels[0].today.counts[0] as { count: unknown }).count = "4";
    answeringJson(body);

    expect(await rejection()).toBeInstanceOf(GatewayError);
  });
});
