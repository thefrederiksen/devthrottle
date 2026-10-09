// @vitest-environment jsdom
import { afterAll, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";

// NEVER THE PROMPT'S WORDS, in the Cockpit composer (the Error Logging mission, issue #3675). The twin of the
// phone's apps/mobile/src/promptWordsNeverReported.test.tsx: the send sites in SessionComposer.tsx are DERIVED
// from the code (promptSendSites.testkit.ts), each is driven with a unique marker while every Gateway call fails,
// and no body posted to /client-errors may carry the marker. A derived site with no driver fails by name; a run
// that derives none fails as a broken scan.
//
// Only what jsdom cannot provide is faked: the dictation dialog (a microphone) and the dictation store (IndexedDB).

const MARKER = "zq-prompt-marker-cockpit-4b81-never-in-a-report";

const { pending } = vi.hoisted(() => ({ pending: new Map<string, Record<string, unknown>>() }));

vi.mock("@devthrottle/client-core/dictation/pendingStore", () => ({
  pendingStoreAvailable: () => true,
  savePending: vi.fn(async (rec: Record<string, unknown>) => void pending.set(rec.id as string, rec)),
  listPending: vi.fn(async () => [...pending.values()]),
  getPending: vi.fn(async (id: string) => pending.get(id) ?? null),
  deletePending: vi.fn(async (id: string) => void pending.delete(id)),
  migratePendingRecord: (stored: unknown) => ({ rec: stored, migrated: false }),
}));

vi.mock("@devthrottle/client-core/dictation/DictationDialog", () => ({
  DictationDialog: (props: {
    onSend: (text: string, spokenDeliveryId?: string) => void;
    onSendAudio: (captured: unknown) => void;
  }) => (
    <div>
      <button type="button" onClick={() => props.onSend(`${MARKER} spoken`)}>
        fake-dialog-send-text
      </button>
      <button
        type="button"
        onClick={() =>
          props.onSendAudio({ sentAt: Date.now(), blob: new Blob(["clip"]), recordedMs: 1000, prefixText: `${MARKER} earlier`, surface: "cockpit" })
        }
      >
        fake-dialog-send-audio
      </button>
    </div>
  ),
}));

import { promptSendSites } from "@devthrottle/client-core/errors/promptSendSites.testkit";
import { resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";
import { SessionComposer } from "./SessionComposer";

const SID = "sess-42";

const reports: string[] = [];
const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
  if (url === "/client-errors") {
    reports.push(String(init?.body ?? ""));
    return new Response(null, { status: 202 });
  }
  return new Response(JSON.stringify({ error: "owning director is not connected" }), {
    status: 502,
    headers: { "Content-Type": "application/json", "X-Correlation-Id": "corr-test-2" },
  });
});
vi.stubGlobal("fetch", fetchMock);
vi.spyOn(console, "warn").mockImplementation(() => {});
vi.spyOn(console, "error").mockImplementation(() => {});
globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;

afterAll(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function Harness({ sending }: { sending?: "send" | "queue" | "both" }) {
  const [value, setValue] = useState("");
  return <SessionComposer sessionId={SID} value={value} onChange={setValue} onQueued={() => {}} sending={sending} />;
}

function typeMarker() {
  fireEvent.change(screen.getByPlaceholderText(/Type a message/i), { target: { value: `${MARKER} typed` } });
}

const alertShown = () => waitFor(() => expect(document.querySelector(".composer-error")).not.toBeNull());
const dictationTried = () =>
  waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).startsWith("/dictation"))).toBe(true));

const DRIVERS: Record<string, () => Promise<void>> = {
  "apps/cockpit/src/sessions/SessionComposer.tsx#sendTypedPrompt#send": async () => {
    render(<Harness />);
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await alertShown();
  },
  "apps/cockpit/src/sessions/SessionComposer.tsx#enqueuePrompt#queue": async () => {
    render(<Harness sending="queue" />);
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Queue it" }));
    await alertShown();
  },
  "apps/cockpit/src/sessions/SessionComposer.tsx#sendTypedPrompt#onDictateSend": async () => {
    render(<Harness />);
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    fireEvent.click(await screen.findByRole("button", { name: "fake-dialog-send-text" }));
    await alertShown();
  },
  "apps/cockpit/src/sessions/SessionComposer.tsx#backgroundTranscribeAndSend#onDictateSendAudio": async () => {
    render(<Harness />);
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    fireEvent.click(await screen.findByRole("button", { name: "fake-dialog-send-audio" }));
    await dictationTried();
  },
};

describe("no prompt-send path in the Cockpit composer puts the prompt's words into an error report", () => {
  const sites = promptSendSites(["apps/cockpit/src/sessions/SessionComposer.tsx"]);

  it("derives the send sites from the code, and every one has a driver", () => {
    console.log(`[promptWordsNeverReported] cockpit composer: ${sites.length} prompt-send sites derived: ${sites.map((s) => s.id).join(", ")}`);
    expect(sites.length).toBeGreaterThan(0);
    const derived = sites.map((s) => s.id);
    expect(derived.filter((id) => !(id in DRIVERS)), "sites with no driver - add one").toEqual([]);
    expect(Object.keys(DRIVERS).filter((id) => !derived.includes(id)), "drivers whose site has gone").toEqual([]);
  });

  it("drives every site with a failing Gateway, and no report body carries the words", async () => {
    resetReportingForTests();
    setReportingComponent("cockpit");
    let exercised = 0;
    for (const site of sites) {
      await DRIVERS[site.id]();
      await act(async () => {
        await new Promise((r) => setTimeout(r, 50));
      });
      cleanup();
      exercised++;
    }
    console.log(`[promptWordsNeverReported] cockpit composer: exercised ${exercised} sites, captured ${reports.length} report bodies`);
    expect(exercised).toBe(sites.length);
    expect(reports.length).toBeGreaterThan(0);
    for (const body of reports) expect(body).not.toContain(MARKER);
  });
});
