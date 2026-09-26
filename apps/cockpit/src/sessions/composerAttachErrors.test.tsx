// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// Issue #2188/#2189 - the reported failure, at the surface the user actually pressed.
//
// What happened: Attach on the Cockpit session screen failed while the owning Director was briefly behind on
// its snapshot pushes. The Gateway answered 404 "session not found across any director" - for a session that
// was alive the whole time - and the composer rendered "The Gateway rejected the request (error 404)." The
// user could not tell what had failed or whether retrying would help, nothing reached the server log, and the
// status was reported back to an agent as "400" because a number was all the message carried.
//
// Three properties, each of which was broken independently:
//   1. The user is shown the server's REASON, never a bare status number.
//   2. The failure is REPORTED to the Gateway, so it does not exist only on the user's screen.
//   3. A retryable failure is RETRIED once before the user is told anything - which alone turns the
//      reported ten-second hole into a non-event.

const { uploadImage, sendPrompt, enqueuePrompt } = vi.hoisted(() => ({
  uploadImage: vi.fn(),
  sendPrompt: vi.fn(async () => ({ delivering: false })),
  enqueuePrompt: vi.fn(async () => []),
}));

// The real GatewayError and the real message mapper: the sentence the user reads is exactly what shipped,
// not a test double's idea of it. Only the network calls are faked.
vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@devthrottle/client-core/api/client")>();
  return { ...actual, uploadImage, sendPrompt, enqueuePrompt };
});

import { GatewayError } from "@devthrottle/client-core/api/client";
import { SessionComposer } from "./SessionComposer";

const SESSION = "1af26bff-d812-474d-b07c-ed8a4baed226";

/** The exact body the Gateway now returns when the owning Director's push has gone stale. */
function directorStaleError(): GatewayError {
  return new GatewayError(
    503,
    "The machine running this session has not reported in for 26 seconds. The session is still there - "
      + "this usually clears within a few seconds. Try again.",
    {
      reason:
        "The machine running this session has not reported in for 26 seconds. The session is still there - "
        + "this usually clears within a few seconds. Try again.",
      code: "director_stale",
      retryable: true,
    },
  );
}

function renderComposer() {
  return render(
    <SessionComposer sessionId={SESSION} value="" onChange={() => {}} onQueued={() => {}} />,
  );
}

/** Drive the hidden file input the Attach button clicks. */
function attach(file: File) {
  const input = document.querySelector<HTMLInputElement>("input.composer-file");
  if (input === null) throw new Error("the composer's file input is not in the document");
  Object.defineProperty(input, "files", { value: [file], configurable: true });
  fireEvent.change(input);
}

const PNG = () => new File([new Uint8Array([1, 2, 3])], "screenshot.png", { type: "image/png" });

const fetchMock = vi.fn();

beforeEach(() => {
  // Real timers on purpose: the retry delay is a product decision (long enough to outlast the observed
  // push gap, short enough that a person does not think the button hung), so the test waits it out rather
  // than faking it away and proving nothing about the real wait.
  uploadImage.mockReset();
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Cockpit composer: a failed Attach", () => {
  it("shows the server's reason, not a bare status number", async () => {
    // Fail both attempts so the message is what the user is finally left with.
    uploadImage.mockRejectedValue(directorStaleError());

    renderComposer();
    attach(PNG());

    await waitFor(
      () => expect(screen.getByText(/has not reported in for 26 seconds/)).toBeTruthy(),
      { timeout: 10000 },
    );
    // The old message must be gone: this is the regression that sent a user looking for "error 400".
    expect(screen.queryByText(/rejected the request/)).toBeNull();
    // And it tells them what to do.
    expect(screen.getByText(/Try again/)).toBeTruthy();
  });

  it("reports the failure to the Gateway, so it does not exist only on the screen", async () => {
    uploadImage.mockRejectedValue(directorStaleError());

    renderComposer();
    attach(PNG());

    await waitFor(
      () => {
        const reports = fetchMock.mock.calls.filter((c) => c[0] === "/client-errors");
        expect(reports.length).toBeGreaterThan(0);
        const body = JSON.parse((reports[0][1] as { body: string }).body) as Record<string, string>;
        expect(body.surface).toBe("cockpit-composer");
        expect(body.message).toContain("attach the image");
        expect(body.message).toContain("has not reported in for 26 seconds");
      },
      { timeout: 10000 },
    );
  });

  it("retries ONCE on a retryable failure and succeeds without bothering the user", async () => {
    // The observed hole was about ten seconds wide and closed on the next push. One retry is enough.
    uploadImage
      .mockRejectedValueOnce(directorStaleError())
      .mockResolvedValueOnce("D:\\shots\\upload-1.png");

    renderComposer();
    attach(PNG());

    await waitFor(() => expect(uploadImage).toHaveBeenCalledTimes(2), { timeout: 10000 });
    await waitFor(() => expect(screen.getByText("Image attached")).toBeTruthy(), { timeout: 10000 });
    // Nothing alarming was shown, and nothing was reported: this was not a failure the user needs to know
    // about. A retry that still reported would just move the noise from the screen into the log.
    expect(screen.queryByText(/has not reported in/)).toBeNull();
    expect(fetchMock.mock.calls.filter((c) => c[0] === "/client-errors")).toHaveLength(0);
  });

  it("does NOT retry a failure that retrying cannot fix", async () => {
    // A 404 for a session that genuinely does not exist is permanent. Retrying it wastes the user's time and
    // makes the eventual message arrive later - the opposite of helpful.
    uploadImage.mockRejectedValue(
      new GatewayError(404, "gone", {
        reason: "That session could not be found. It may have been closed.",
        retryable: false,
      }),
    );

    renderComposer();
    attach(PNG());

    await waitFor(
      () => expect(screen.getByText(/could not be found/)).toBeTruthy(),
      { timeout: 10000 },
    );
    expect(uploadImage).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Try again/)).toBeNull();
  });
});
