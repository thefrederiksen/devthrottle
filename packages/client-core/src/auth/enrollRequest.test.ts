import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import {
  MOBILE_ENROLLMENT_PROFILE,
  COCKPIT_ENROLLMENT_PROFILE,
  configureEnrollment,
  enrollmentProfile,
  desktopDeviceName,
  desktopPlatform,
  safeInternalPath,
  rememberEnrollNext,
  takeEnrollNext,
  readEnrollCredential,
} from "./enrollRequest";

// The shell-agnostic enrollment profile (issue #1088). The mobile profile must stay the DEFAULT and
// byte-compatible with the pre-#1088 phone behavior (the phone flow is the proven reference); the
// desktop Cockpit installs its own profile at startup. The next-route helpers preserve the
// originally-requested Cockpit route across the devthrottle.com round trip, and must refuse anything
// that could send the browser off this origin.

// A minimal in-memory sessionStorage so the storage-backed helpers run under the node test
// environment (no jsdom in this workspace; the helpers only need getItem/setItem/removeItem).
function stubSessionStorage(): void {
  const store = new Map<string, string>();
  vi.stubGlobal("sessionStorage", {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => { store.set(key, value); },
    removeItem: (key: string) => { store.delete(key); },
  });
}

// A sessionStorage that refuses every call, the way a browser with Web Storage denied does.
function stubRefusingSessionStorage(): void {
  const refuse = (): never => {
    throw new DOMException("The operation is insecure.", "SecurityError");
  };
  vi.stubGlobal("sessionStorage", {
    getItem: refuse,
    setItem: refuse,
    removeItem: refuse,
  });
}

describe("enrollment shell profile", () => {
  afterEach(() => configureEnrollment(MOBILE_ENROLLMENT_PROFILE));

  it("defaults to the mobile profile (the phone behavior must not regress)", () => {
    const profile = enrollmentProfile();
    expect(profile).toBe(MOBILE_ENROLLMENT_PROFILE);
    // The router basename re-based from /m to /mobile with the app mount...
    expect(profile.basename).toBe("/mobile");
    // ...but the callback path the website is told (redirect_uri) STAYS /m/device-callback: the
    // devthrottle.com allow-list (a different repo) still pins it, and the Gateway 301s that path to
    // /mobile/device-callback with the fragment intact. Guards against a well-meaning "canonicalize
    // this too" edit that would fail the website's strict redirect_uri check and break sign-in.
    expect(profile.callbackPath).toBe("/m/device-callback");
    expect(profile.deviceLabel).toBe("phone");
    expect(profile.defaultLanding).toBe("/");
  });

  it("the desktop Cockpit profile targets the Cockpit callback with a non-phone platform", () => {
    configureEnrollment(COCKPIT_ENROLLMENT_PROFILE);
    const profile = enrollmentProfile();
    expect(profile.callbackPath).toBe("/device-callback");
    expect(profile.platform()).toBe("browser");
    expect(profile.deviceLabel).toBe("device");
    expect(profile.basename).toBe("");
    // Both shells share the /signin route name inside their own routers.
    expect(profile.signInPath).toBe("/signin");
  });
});

describe("desktop device identity", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("platform is the non-phone 'browser' identifier", () => {
    expect(desktopPlatform()).toBe("browser");
  });

  it("device name is human-recognizable: browser + operating system from the user agent", () => {
    vi.stubGlobal("navigator", {
      userAgent:
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0",
    });
    expect(desktopDeviceName()).toBe("Edge on Windows");
  });

  it("falls back to a generic label when the user agent is unknown", () => {
    vi.stubGlobal("navigator", { userAgent: "SomethingUnrecognizable/1.0" });
    expect(desktopDeviceName()).toBe("Browser on desktop");
  });
});

// The credential the website returns in the callback fragment decides the enrollment path (multi-tenant
// hosted sign-in, Phase C): an access_token is a HOSTED round trip (forwarded as Authorization: Bearer),
// a device_key is the pre-hosted SELF-HOST round trip (posted in the body). The self-host case must keep
// selecting device_key exactly as before, or every self-hosted install's sign-in breaks.
describe("readEnrollCredential", () => {
  it("selects the hosted path when the fragment carries an access_token", () => {
    const params = new URLSearchParams("access_token=abc.def.ghi&state=s1");
    expect(readEnrollCredential(params)).toEqual({ mode: "hosted", accessToken: "abc.def.ghi" });
  });

  it("selects the self-host path when the fragment carries a device_key (unchanged behavior)", () => {
    const params = new URLSearchParams("device_key=dk-123&state=s1");
    expect(readEnrollCredential(params)).toEqual({ mode: "selfHost", deviceKey: "dk-123" });
  });

  it("rejects an ambiguous fragment carrying BOTH credentials (fail closed, do not guess)", () => {
    // A legitimate callback carries exactly one credential. Both present means we cannot know which
    // gateway kind the callback is for; guessing would send the wrong request shape, so it fails
    // closed to null exactly like the neither-present case.
    const params = new URLSearchParams("device_key=dk-123&access_token=abc.def.ghi");
    expect(readEnrollCredential(params)).toBeNull();
  });

  it("returns null when the fragment carries neither credential", () => {
    expect(readEnrollCredential(new URLSearchParams("state=s1"))).toBeNull();
    expect(readEnrollCredential(new URLSearchParams(""))).toBeNull();
  });
});

describe("safeInternalPath", () => {
  it("accepts a root-relative in-app path with a query", () => {
    expect(safeInternalPath("/fleet?tab=map")).toBe("/fleet?tab=map");
    expect(safeInternalPath("/")).toBe("/");
  });

  it("rejects anything that could leave the origin", () => {
    expect(safeInternalPath("//evil.example/phish")).toBeNull();
    expect(safeInternalPath("https://evil.example")).toBeNull();
    expect(safeInternalPath("fleet")).toBeNull();
    expect(safeInternalPath("")).toBeNull();
    expect(safeInternalPath(null)).toBeNull();
  });
});

describe("next-route preservation across the round trip", () => {
  beforeEach(() => stubSessionStorage());
  afterEach(() => vi.unstubAllGlobals());

  it("remembers a safe route and hands it back exactly once", () => {
    rememberEnrollNext("/directors/abc?x=1");
    expect(takeEnrollNext()).toBe("/directors/abc?x=1");
    // Consumed: a second read lands on the shell default.
    expect(takeEnrollNext()).toBeNull();
  });

  it("stores nothing for an unsafe or missing route", () => {
    rememberEnrollNext("//evil.example");
    expect(takeEnrollNext()).toBeNull();
    rememberEnrollNext(null);
    expect(takeEnrollNext()).toBeNull();
  });

  it("an unsafe remembered route clears any previously remembered one", () => {
    rememberEnrollNext("/fleet");
    rememberEnrollNext("//evil.example");
    expect(takeEnrollNext()).toBeNull();
  });

  it("returns null (the shell default) when storage is unavailable", () => {
    // A browser that refuses Web Storage - a sandboxed frame, cookies disabled, Safari's private
    // mode - throws on the call rather than handing back an empty store, and the helpers must
    // degrade to the shell default instead of taking the sign-in flow down with them.
    //
    // This used to be written as vi.unstubAllGlobals(), leaning on the node test environment having
    // no sessionStorage at all. That stopped being true: Node ships its own Web Storage globals
    // (experimental from 22.4, unflagged from 24), so unstubbing restored a WORKING process-wide
    // store, the route was remembered, and the test failed on Node 24 and later while still passing
    // on the Node 22 runner. The refusal is now stated by the test rather than borrowed from the
    // runtime, so it means the same thing everywhere.
    stubRefusingSessionStorage();
    rememberEnrollNext("/fleet");
    expect(takeEnrollNext()).toBeNull();
  });
});
