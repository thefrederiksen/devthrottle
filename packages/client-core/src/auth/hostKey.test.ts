// @vitest-environment jsdom
// The host-supplied Gateway key (dev reports mission, phase 4): the credential the Director hands to the
// page it embeds in WebView2.
//
// Two things have to be true at once, and each is tested here against the REAL client, not a copy of its
// logic: the key must actually be the Bearer on every Gateway call, and it must never come to rest
// anywhere that outlives the document. A key that is held but not used makes the pane show nothing; a key
// that is used but also stored leaves the Director's own credential on the machine for the next page on
// this origin to pick up. The second failure is silent, which is why it is asserted by reading every store
// back rather than by reading the code.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authHeaders, ensureGatewayCookie } from "../api/client";
import { hostSuppliedKey, setHostSuppliedKey } from "./hostKey";

const DEVICE_KEY = "device-key-odd-31";
const HOST_KEY = "host-key-odd-47";

// The device key is read through the account store, which reads localStorage. Stub the one function the
// Bearer path calls so these tests are about the host key and not about enrollment.
const getDeviceKey = vi.fn<() => string>(() => "");
vi.mock("./deviceKey", () => ({
  getDeviceKey: () => getDeviceKey(),
  hasDeviceKey: () => getDeviceKey().length > 0,
  clearDeviceKey: () => {},
  clearAllDeviceKeys: () => {},
  hasMultipleAccounts: () => false,
  getInstallId: () => "install-odd-5",
}));

function bearer(): string | undefined {
  return (authHeaders() as Record<string, string>).Authorization;
}

beforeEach(() => {
  getDeviceKey.mockReturnValue("");
  setHostSuppliedKey(null);
  window.localStorage.clear();
  window.sessionStorage.clear();
  for (const pair of document.cookie.split(";")) {
    const name = pair.split("=")[0]?.trim();
    if (name) document.cookie = `${name}=; path=/; Max-Age=0`;
  }
});

afterEach(() => setHostSuppliedKey(null));

describe("the host-supplied Gateway key", () => {
  it("is the Bearer on Gateway calls once the host has supplied it", () => {
    getDeviceKey.mockReturnValue("");
    expect(bearer()).toBeUndefined();

    setHostSuppliedKey(HOST_KEY);

    expect(hostSuppliedKey()).toBe(HOST_KEY);
    expect(bearer()).toBe(`Bearer ${HOST_KEY}`);
  });

  it("outranks a device key this browser happens to hold, so an embedded pane reads as the host", () => {
    getDeviceKey.mockReturnValue(DEVICE_KEY);
    expect(bearer()).toBe(`Bearer ${DEVICE_KEY}`);

    setHostSuppliedKey(HOST_KEY);

    expect(bearer()).toBe(`Bearer ${HOST_KEY}`);
  });

  it("is never written to localStorage, sessionStorage or a cookie - not even by ensureGatewayCookie", () => {
    getDeviceKey.mockReturnValue("");
    setHostSuppliedKey(HOST_KEY);

    // Startup calls this on every load of the Cockpit bundle, embedded route included.
    ensureGatewayCookie();

    expect(JSON.stringify(window.localStorage)).not.toContain(HOST_KEY);
    expect(JSON.stringify(window.sessionStorage)).not.toContain(HOST_KEY);
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
    expect(document.cookie).not.toContain(HOST_KEY);
    expect(document.cookie).not.toContain("cc-gateway-token");
  });

  it("does not stop ensureGatewayCookie mirroring the browser's OWN device key", () => {
    // The cookie still carries the enrolled key for an ordinary browser: the guard above must not have
    // turned the cookie off for the Cockpit and the phone, which need it for the terminal stream.
    getDeviceKey.mockReturnValue(DEVICE_KEY);
    setHostSuppliedKey(HOST_KEY);

    ensureGatewayCookie();

    expect(document.cookie).toContain(`cc-gateway-token=${DEVICE_KEY}`);
    expect(document.cookie).not.toContain(HOST_KEY);
  });

  it("is forgotten when it is cleared, and the Bearer returns to the device key", () => {
    getDeviceKey.mockReturnValue(DEVICE_KEY);
    setHostSuppliedKey(HOST_KEY);
    expect(bearer()).toBe(`Bearer ${HOST_KEY}`);

    setHostSuppliedKey(null);

    expect(hostSuppliedKey()).toBeNull();
    expect(bearer()).toBe(`Bearer ${DEVICE_KEY}`);
  });

  it("treats an empty string as no key at all, so a host that supplies nothing supplies nothing", () => {
    getDeviceKey.mockReturnValue(DEVICE_KEY);

    setHostSuppliedKey("");

    expect(hostSuppliedKey()).toBeNull();
    expect(bearer()).toBe(`Bearer ${DEVICE_KEY}`);
  });
});
