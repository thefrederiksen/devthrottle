import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { GatewayError, gatewayErrorMessage } from "./client";
import { describeAndReport, resetReportWindow } from "../errors/reportClientError";

// Issue #2189: THE RULE - a number is not an error message.
//
// The failure these pin: attaching a picture from the Cockpit session screen produced
// "The Gateway rejected the request (error 404)." on screen and nothing at all on the server. The user
// could not tell what had failed, could not tell whether retrying would help, and reported the status as
// "400" because three digits were the only thing the message gave them to hold onto. Meanwhile the Gateway
// HAD written a good reason into the response body, and the client threw it away at the throw site.
//
// Three properties are pinned, and they are independent:
//   1. The server's reason survives to the screen.
//   2. NO message is ever only a status number - for any status, with or without a reason.
//   3. Showing an error also reports it, so an on-screen error cannot exist only on the user's screen.

describe("gatewayErrorMessage: the server's reason reaches the user", () => {
  it("prefers the server's sentence over anything we would write ourselves", () => {
    const reason =
      "The machine running this session has not reported in for 26 seconds. The session is still there - "
      + "this usually clears within a few seconds. Try again.";
    const err = new GatewayError(503, "ignored diagnostic", { reason, retryable: true });

    expect(gatewayErrorMessage(err, "attach the image")).toBe(reason);
  });

  it("does not leak the internal diagnostic when a reason is present", () => {
    const err = new GatewayError(503, "POST upload-image failed: 503", {
      reason: "That machine is catching up. Try again.",
    });
    const msg = gatewayErrorMessage(err, "attach the image");

    expect(msg).not.toContain("POST");
    expect(msg).not.toContain("upload-image");
    expect(msg).not.toContain("failed:");
  });

  it("adds a retry hint when the server said the failure is retryable and the reason omits one", () => {
    const err = new GatewayError(503, "x", { reason: "That machine is catching up.", retryable: true });

    expect(gatewayErrorMessage(err, "attach the image")).toContain("Try again");
  });

  it("does not duplicate the retry hint when the reason already gives it", () => {
    const err = new GatewayError(503, "x", { reason: "Still catching up - try again.", retryable: true });
    const msg = gatewayErrorMessage(err, "attach the image");

    expect(msg.match(/try again/gi)?.length).toBe(1);
  });

  it("terminates a reason that is a PHRASE before adding the hint, so the two do not run together", () => {
    // The defect this pins, found by photographing a disconnected Director for the one-repository-list
    // mission's phase-4 proof: GET /directors/{id}/known-repositories answers 502 with the reason
    // "Director not connected", which is a phrase and not a sentence. The retry hint was appended
    // straight onto it and the screen read "Director not connected Try again." - two sentences with
    // nothing between them. It had gone unseen because every reason anyone had looked at happened to
    // end in a full stop.
    const err = new GatewayError(502, "x", { reason: "Director not connected", retryable: true });

    expect(gatewayErrorMessage(err, "load that machine's repositories")).toBe(
      "Director not connected. Try again.",
    );
  });

  it("leaves a reason that already ends in a sentence exactly as it was", () => {
    // The other half, and the one a careless fix would break: a reason that IS terminated must not gain
    // a second full stop. Both endings have to be pinned, or the fix trades one malformed line for another.
    const stop = new GatewayError(503, "x", { reason: "That machine is catching up.", retryable: true });
    expect(gatewayErrorMessage(stop, "attach the image")).toBe("That machine is catching up. Try again.");

    // A question mark and an exclamation mark end a sentence just as a full stop does.
    const question = new GatewayError(503, "x", { reason: "Is that machine awake?", retryable: true });
    expect(gatewayErrorMessage(question, "attach the image")).toBe("Is that machine awake? Try again.");
  });

  it("does not leave a gap where the reason had trailing space", () => {
    const err = new GatewayError(502, "x", { reason: "Director not connected   ", retryable: true });

    expect(gatewayErrorMessage(err, "attach the image")).toBe("Director not connected. Try again.");
  });

  it("never invites a retry for a failure retrying cannot fix", () => {
    // A 404 for an unknown session is permanent. Telling the user to try again would have them press the
    // button forever.
    const err = new GatewayError(404, "x", {
      reason: "That session could not be found. It may have been closed.",
      retryable: false,
    });

    expect(gatewayErrorMessage(err, "attach the image")).not.toContain("Try again");
  });
});

describe("gatewayErrorMessage: never a bare status number", () => {
  // The guard for the whole issue. This is the assertion that fails against the pre-fix code, where every
  // status other than 401 collapsed to "The Gateway rejected the request (error NNN)."
  const statuses = [400, 403, 404, 409, 413, 423, 429, 500, 502, 503, 504];

  it("always says what failed, for every status, when an action is named", () => {
    for (const status of statuses) {
      const msg = gatewayErrorMessage(new GatewayError(status, `x failed: ${status}`), "attach the image");

      // It names the action the user took...
      expect(msg, `status ${status}`).toContain("attach the image");
      // ...and it is a sentence, not a code with punctuation around it.
      expect(msg.replace(/[^a-z]/gi, "").length, `status ${status}`).toBeGreaterThan(25);
      // ...and it never leaks the internal diagnostic.
      expect(msg, `status ${status}`).not.toContain("failed:");
    }
  });

  it("says something even with no action named and no server reason", () => {
    // The passive read case: still a sentence, never a naked number.
    const msg = gatewayErrorMessage(new GatewayError(500, "GET /directors/abc failed: 500"));

    expect(msg).not.toBe("The Gateway rejected the request (error 500).");
    expect(msg.replace(/[^a-z]/gi, "").length).toBeGreaterThan(25);
    expect(msg).not.toContain("/directors");
  });

  it("marks the transport statuses retryable without the server having to say so", () => {
    for (const status of [0, 429, 502, 503, 504]) {
      expect(new GatewayError(status, "x").retryable, `status ${status}`).toBe(true);
    }
    for (const status of [400, 403, 404, 409, 413, 423, 500]) {
      expect(new GatewayError(status, "x").retryable, `status ${status}`).toBe(false);
    }
  });

  it("lets the server's retryable flag override the status class in both directions", () => {
    // A Gateway that knows better than the status class must be believed - that is the point of the flag.
    expect(new GatewayError(404, "x", { retryable: true }).retryable).toBe(true);
    expect(new GatewayError(503, "x", { retryable: false }).retryable).toBe(false);
  });
});

describe("describeAndReport: showing an error also records it", () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    resetReportWindow();
    fetchMock.mockReset();
    fetchMock.mockResolvedValue({ ok: true });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("returns the sentence AND posts the report to the Gateway", () => {
    const err = new GatewayError(503, "x", { reason: "That machine is catching up.", retryable: true });

    const shown = describeAndReport("cockpit-composer", "attach the image", err);

    // What the user reads.
    expect(shown).toContain("That machine is catching up.");
    // What the server records. Before this, the report simply did not happen: the whole product had one
    // explicit report call site, so finding this failure meant grepping a Gateway log by hand.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/client-errors");
    const body = JSON.parse((init as { body: string }).body) as Record<string, string>;
    expect(body.surface).toBe("cockpit-composer");
    expect(body.message).toContain("attach the image");
    expect(body.message).toContain("That machine is catching up.");
  });

  it("still returns the sentence when reporting itself fails", () => {
    // Reporting an error must never become an error the user sees.
    fetchMock.mockImplementation(() => {
      throw new Error("network down");
    });

    const shown = describeAndReport("cockpit-composer", "attach the image", new GatewayError(500, "x"));

    expect(shown).toContain("attach the image");
  });
});
