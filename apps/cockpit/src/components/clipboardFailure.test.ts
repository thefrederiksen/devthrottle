import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { describeAndReport, resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";
import { GATEWAY_UNREACHABLE_MESSAGE } from "@devthrottle/client-core/api/client";
import { ClipboardRefusedError } from "./clipboardFailure";

// A copy the browser refused is the browser's doing. describeAndReport reads a bare TypeError as "the request never
// reached the Gateway", which is what a page with no clipboard API throws - so the Cockpit hands it a
// ClipboardRefusedError instead, and the sentence and the report say what really happened.

const posted: Array<Record<string, unknown>> = [];
const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
  posted.push(JSON.parse(String(init?.body)) as Record<string, unknown>);
  return new Response(null, { status: 204 });
});

describe("ClipboardRefusedError", () => {
  beforeEach(() => {
    posted.length = 0;
    fetchMock.mockClear();
    vi.stubGlobal("fetch", fetchMock);
    resetReportingForTests();
    setReportingComponent("cockpit");
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetReportingForTests();
  });

  it("describeAndReport_NoClipboardApi_SaysTheBrowserRefused_NotThatTheGatewayIsUnreachable", async () => {
    // What `navigator.clipboard.writeText` throws on a page the browser does not trust: clipboard is undefined.
    const raw = new TypeError("Cannot read properties of undefined (reading 'writeText')");

    const shown = describeAndReport("cockpit-chat", "copy the link", new ClipboardRefusedError(raw), { sessionId: "s-1" });

    expect(shown).not.toBe(GATEWAY_UNREACHABLE_MESSAGE);
    expect(shown).toContain("could not copy the link");
    expect(shown).toContain("the browser did not allow copying to the clipboard");
    await vi.waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toMatchObject({
      component: "cockpit",
      surface: "cockpit-chat",
      action: "copy the link",
      user_visible: true,
      exception_type: "ClipboardRefusedError",
      session_id: "s-1",
    });
  });

  it("constructor_PermissionRefusal_KeepsTheBrowsersNameAndWords", () => {
    const refused = new DOMException("Write permission denied.", "NotAllowedError");

    expect(new ClipboardRefusedError(refused).message).toBe(
      "the browser did not allow copying to the clipboard (NotAllowedError: Write permission denied.)",
    );
  });
});
