import { describe, it, expect, afterEach, vi } from "vitest";
import {
  configureUnauthorizedRedirect,
  cockpitSignInRedirect,
  mobileSignInRedirect,
  resolveSignInTarget,
  fetchSessionFileSize,
  GatewayError,
  sendPrompt,
} from "./client";

// The shell-aware mid-session 401 redirect (issue #1024, retargeted by issue #1088). A 401 on the
// DESKTOP Cockpit must re-gate through the desktop's own /signin route - the shared client-core
// device-enrollment flow - NOT hard-navigate the whole app to the mobile PWA's /mobile/signin enrollment
// screen (which ejected the user from the desktop shell), and NOT to the retired /login token wall
// (issue #1088: after a website revoke the desktop returns to the shared sign-in flow, never to
// login.html). Each shell installs its own redirect at startup (configureUnauthorizedRedirect); these
// tests exercise the exact builder functions the shells install (cockpitSignInRedirect /
// mobileSignInRedirect) plus the resolver the 401 path calls (resolveSignInTarget), so the behavior is
// proven without a DOM.

describe("shell-aware unauthorized redirect", () => {
  // Restore the mobile default so one test's configuration never leaks into another.
  afterEach(() => configureUnauthorizedRedirect(mobileSignInRedirect));

  it("desktop 401 routes to the Cockpit /signin (with next=), never to /mobile/signin or /login", () => {
    // What apps/cockpit/src/main.tsx installs at startup.
    configureUnauthorizedRedirect(cockpitSignInRedirect);

    const target = resolveSignInTarget({ pathname: "/session/abc", search: "?tab=terminal" });

    expect(target).toBe(`/signin?next=${encodeURIComponent("/session/abc?tab=terminal")}`);
    // The bug #1024 fixed: the desktop path must NOT land on the mobile enrollment route.
    expect(target).not.toContain("/mobile/signin");
    expect(target.startsWith("/mobile/")).toBe(false);
    // The #1088 acceptance: a 401 returns to the shared sign-in flow, never to the token wall.
    expect(target.startsWith("/login")).toBe(false);
  });

  it("desktop redirect carries the current route forward so the user lands back after sign-in", () => {
    configureUnauthorizedRedirect(cockpitSignInRedirect);

    // A 401 while sitting on the roster root round-trips back to "/".
    expect(resolveSignInTarget({ pathname: "/", search: "" })).toBe(
      `/signin?next=${encodeURIComponent("/")}`,
    );
  });

  it("mobile 401 still routes to /mobile/signin", () => {
    // What apps/mobile/src/main.tsx installs at startup.
    configureUnauthorizedRedirect(mobileSignInRedirect);

    expect(resolveSignInTarget({ pathname: "/mobile/session/abc", search: "" })).toBe("/mobile/signin");
  });

  it("defaults to the mobile route when a shell installs nothing", () => {
    // The mobile shell historically owned client-core, so the unconfigured default stays /mobile/signin.
    expect(resolveSignInTarget({ pathname: "/mobile", search: "" })).toBe("/mobile/signin");
  });
});

// fetchSessionFileSize (Local Files, Phase 4): the download panel's size probe. It issues a one-byte
// ranged GET (Range: bytes=0-0) so the Director answers 206 with Content-Range "bytes 0-0/<total>", and
// reads the FULL size from that total - without downloading the file. A missing file / offline machine
// (404 / 503) throws a GatewayError so the panel fails loud; a response with no readable total returns
// null so the panel shows the name with NO guessed size.
describe("fetchSessionFileSize", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  // A minimal fetch Response stand-in: status, a header bag, and a cancelable body.
  function fakeResponse(status: number, headers: Record<string, string>): Response {
    const map = new Map(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v]));
    return {
      ok: status >= 200 && status < 300,
      status,
      headers: { get: (name: string) => map.get(name.toLowerCase()) ?? null },
      body: { cancel: () => Promise.resolve() },
    } as unknown as Response;
  }

  it("sends a Range: bytes=0-0 header to the session file URL", async () => {
    const fetchMock = vi.fn(async () =>
      fakeResponse(206, { "Content-Range": "bytes 0-0/2048" }),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await fetchSessionFileSize("sess-1", "D:\\out\\big.pdf");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(`/sessions/sess-1/file?path=${encodeURIComponent("D:\\out\\big.pdf")}`);
    expect((init.headers as Record<string, string>).Range).toBe("bytes=0-0");
  });

  it("reads the full size from the Content-Range total on a 206", async () => {
    globalThis.fetch = (async () =>
      fakeResponse(206, { "Content-Range": "bytes 0-0/1500000" })) as unknown as typeof fetch;

    expect(await fetchSessionFileSize("s", "D:\\a\\b.bin")).toBe(1500000);
  });

  it("returns null when the server ignored the range (200, no Content-Range)", async () => {
    globalThis.fetch = (async () =>
      fakeResponse(200, { "Content-Length": "42" })) as unknown as typeof fetch;

    expect(await fetchSessionFileSize("s", "D:\\a\\b.bin")).toBeNull();
  });

  it("throws a GatewayError with the status for a missing file (404)", async () => {
    globalThis.fetch = (async () => fakeResponse(404, {})) as unknown as typeof fetch;

    await expect(fetchSessionFileSize("s", "D:\\gone.bin")).rejects.toMatchObject({
      status: 404,
    });
    await expect(fetchSessionFileSize("s", "D:\\gone.bin")).rejects.toBeInstanceOf(GatewayError);
  });

  it("throws a GatewayError with the status when the machine is offline (503)", async () => {
    globalThis.fetch = (async () => fakeResponse(503, {})) as unknown as typeof fetch;

    await expect(fetchSessionFileSize("s", "D:\\a\\b.bin")).rejects.toMatchObject({
      status: 503,
    });
  });
});

// sendPrompt naming a recording (Voice Delivery mission, phase 1): "Send anyway" passes the recording's upload id,
// and it must reach the Gateway as a CLAIM (deliveryIdClaim) - never as deliveryId, which only the Gateway sets, and
// never as deliveryUploadId, which would mark typed-around words as spoken.
describe("sendPrompt with a recording id", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  function bodyOf(fetchMock: ReturnType<typeof vi.fn>): Record<string, unknown> {
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    return JSON.parse(init.body as string) as Record<string, unknown>;
  }

  it("sends the recording's upload id as deliveryIdClaim and nothing else", async () => {
    // Proves the id reaches the wire under the one field the Gateway verifies.
    const fetchMock = vi.fn(async () => ({ ok: true, status: 200, json: async () => ({}) }) as unknown as Response);
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await sendPrompt("sid-1", "typed before the spoken words", true, undefined, undefined, undefined, "rec-42");

    const body = bodyOf(fetchMock);
    expect(body.deliveryIdClaim).toBe("rec-42");
    expect(body).not.toHaveProperty("deliveryId");
    expect(body).not.toHaveProperty("deliveryUploadId");
  });

  it("an ordinary prompt carries no recording claim", async () => {
    // Proves every other caller's body is unchanged. A real Response: since phase 5 every prompt answer's body is
    // read for its deliveryId.
    const fetchMock = vi.fn(async () => new Response(null, { status: 200 }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await sendPrompt("sid-1", "hello", true);

    expect(bodyOf(fetchMock)).not.toHaveProperty("deliveryIdClaim");
  });
});
