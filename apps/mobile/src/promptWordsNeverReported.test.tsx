// @vitest-environment jsdom
import { afterAll, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";

// NEVER THE PROMPT'S WORDS (the Error Logging mission, issue #3675). The owner's ruling: "A test makes sure no send
// path ever passes the prompt's words into a report."
//
// The send sites are DERIVED from the code (promptSendSites.testkit.ts): every call, in the phone app, of a
// client-core function that carries a person's words to the Gateway. Each site has a driver here that pushes a
// unique marker through it with EVERY Gateway call failing (502, with a correlation id), while every body posted
// to /client-errors is captured. The marker must appear in none of them. A derived site with no driver fails this
// test by name, and so does a driver whose site has gone; a run that derives no sites fails as a broken scan.
//
// Only what jsdom cannot provide is faked: the dictation dialog (a microphone), and the dictation store
// (IndexedDB), held in memory. The send code, the reporter and the Gateway client are the real ones.

const MARKER = "zq-prompt-marker-7f3a9c-never-in-a-report";

const { pending } = vi.hoisted(() => ({ pending: new Map<string, Record<string, unknown>>() }));

vi.mock("@devthrottle/client-core/dictation/pendingStore", () => ({
  pendingStoreAvailable: () => true,
  savePending: vi.fn(async (rec: Record<string, unknown>) => void pending.set(rec.id as string, rec)),
  listPending: vi.fn(async () => [...pending.values()]),
  getPending: vi.fn(async (id: string) => pending.get(id) ?? null),
  deletePending: vi.fn(async (id: string) => void pending.delete(id)),
  migratePendingRecord: (stored: unknown) => ({ rec: stored, migrated: false }),
}));

// The dialog needs a microphone. This stand-in offers the dialog's three ways out as buttons, so the shell's own
// handlers - the code under test - run exactly as they would after a real recording.
vi.mock("@devthrottle/client-core/dictation/DictationDialog", () => ({
  DictationDialog: (props: {
    onSend: (text: string, spokenDeliveryId?: string) => void;
    onSendAudio: (captured: unknown) => void;
  }) => (
    <div>
      <button type="button" onClick={() => props.onSend(`${MARKER} spoken`)}>
        fake-dialog-send-text
      </button>
      <button type="button" onClick={() => props.onSendAudio(capturedWithMarker())}>
        fake-dialog-send-audio
      </button>
    </div>
  ),
}));

import { promptSendSites } from "@devthrottle/client-core/errors/promptSendSites.testkit";
import { resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { useVoiceMode } from "@devthrottle/client-core/voice/useVoiceMode";
import { SessionControls } from "./components/SessionControls";

const SID = "sess-42";

function capturedWithMarker() {
  return { sentAt: Date.now(), blob: new Blob(["clip"], { type: "audio/webm" }), recordedMs: 1000, prefixText: `${MARKER} earlier`, surface: "mobile" };
}

// ---- the failing Gateway, and every report body it receives ----------------------------------------------

const reports: string[] = [];
const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
  if (url === "/client-errors") {
    reports.push(String(init?.body ?? ""));
    return new Response(null, { status: 202 });
  }
  return new Response(JSON.stringify({ error: "owning director is not connected" }), {
    status: 502,
    headers: { "Content-Type": "application/json", "X-Correlation-Id": "corr-test-1" },
  });
});
vi.stubGlobal("fetch", fetchMock);
// The microphone and audio decode do not exist in jsdom; the background path logs that and carries on.
vi.spyOn(console, "warn").mockImplementation(() => {});
vi.spyOn(console, "error").mockImplementation(() => {});
globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;

afterAll(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderControls() {
  const onError = vi.fn();
  render(<SessionControls sessionId={SID} onFlash={() => {}} onError={onError} showKeyRows />);
  return onError;
}

function typeMarker() {
  fireEvent.change(screen.getByPlaceholderText(/type a message/i), { target: { value: `${MARKER} typed` } });
}

async function settle() {
  // Let every failed call, its report and any queued retry run out.
  await act(async () => {
    await new Promise((r) => setTimeout(r, 50));
  });
}

/** One driver per derived site, keyed by the site's id. Each pushes the marker through that site and fails. */
const DRIVERS: Record<string, () => Promise<void>> = {
  "apps/mobile/src/components/SessionControls.tsx#sendPrompt#sendKey": async () => {
    const onError = renderControls();
    typeMarker(); // the key rows send a key, not the box - the words sit in the box while it fails
    fireEvent.click(screen.getByRole("button", { name: "Enter" }));
    await waitFor(() => expect(onError).toHaveBeenCalled());
  },
  "apps/mobile/src/components/SessionControls.tsx#sendTypedPrompt#onSend": async () => {
    const onError = renderControls();
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(onError).toHaveBeenCalled());
  },
  "apps/mobile/src/components/SessionControls.tsx#sendTypedPrompt#onDictateSend": async () => {
    const onError = renderControls();
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    fireEvent.click(await screen.findByRole("button", { name: "fake-dialog-send-text" }));
    await waitFor(() => expect(onError).toHaveBeenCalled());
  },
  "apps/mobile/src/components/SessionControls.tsx#backgroundTranscribeAndSend#onDictateSendAudio": async () => {
    renderControls();
    typeMarker();
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    fireEvent.click(await screen.findByRole("button", { name: "fake-dialog-send-audio" }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).startsWith("/dictation"))).toBe(true));
  },
  "apps/mobile/src/pages/VoiceMode.tsx#useVoiceMode#VoiceMode": async () => {
    const { result } = renderHook(() => useVoiceMode(SID));
    await act(async () => {
      await result.current.onRespondSend(`${MARKER} spoken reply`);
    });
    expect(result.current.error).toBeTruthy();
    // The voice reply's red box is the same Send failure as the typed one: it carries the Gateway's reason and
    // the X-Correlation-Id it answered with (review finding 2 - sendVoicePrompt threw a bare GatewayError).
    expect(result.current.error).toContain("owning director is not connected");
    await waitFor(() =>
      expect(
        reports.map((r) => JSON.parse(r) as Record<string, unknown>).filter((r) => r.surface === "voice-mode"),
      ).toEqual([expect.objectContaining({ action: "send prompt", http_status: 502, correlation_id: "corr-test-1", session_id: SID })]),
    );
    act(() =>result.current.onRespondSendAudio(capturedWithMarker() as never));
  },
  "apps/mobile/src/routes.tsx#resumePendingDictations#GatedLayout": async () => {
    pending.set("pending-1", {
      id: "pending-1",
      sessionId: SID,
      blob: new Blob(["clip"]),
      recordedMs: 1000,
      surface: "mobile-send",
      before: `${MARKER} before`,
      after: `${MARKER} after`,
      prefix: `${MARKER} prefix`,
      createdAt: Date.now(),
      sentAt: Date.now(),
    });
    await resumePendingDictations();
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).startsWith("/dictation"))).toBe(true));
  },
};

describe("no prompt-send path on the phone puts the prompt's words into an error report", () => {
  const sites = promptSendSites(["apps/mobile/src"]);

  it("derives the send sites from the code, and every one has a driver", () => {
    console.log(`[promptWordsNeverReported] phone: ${sites.length} prompt-send sites derived: ${sites.map((s) => s.id).join(", ")}`);
    expect(sites.length).toBeGreaterThan(0);
    const derived = sites.map((s) => s.id);
    expect(derived.filter((id) => !(id in DRIVERS)), "sites with no driver - add one").toEqual([]);
    expect(Object.keys(DRIVERS).filter((id) => !derived.includes(id)), "drivers whose site has gone").toEqual([]);
  });

  it("drives every site with a failing Gateway, and no report body carries the words", async () => {
    resetReportingForTests();
    setReportingComponent("mobile");
    let exercised = 0;
    for (const site of sites) {
      await DRIVERS[site.id]();
      await settle();
      cleanup();
      exercised++;
    }
    console.log(`[promptWordsNeverReported] phone: exercised ${exercised} sites, captured ${reports.length} report bodies`);
    expect(exercised).toBe(sites.length);
    // The proof is only as good as the reports it saw: the Send red box at least must have reported.
    expect(reports.length).toBeGreaterThan(0);
    expect(reports.some((r) => r.includes('"action":"send prompt"'))).toBe(true);
    for (const body of reports) expect(body).not.toContain(MARKER);
  });
});
