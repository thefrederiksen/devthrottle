// @vitest-environment jsdom
import { afterAll, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { useState } from "react";

// NEVER THE PROMPT'S WORDS, anywhere in the Cockpit (the Error Logging mission, issue #3675). The twin of the
// phone's apps/mobile/src/promptWordsNeverReported.test.tsx: every prompt-send site in apps/cockpit/src is DERIVED
// from the code (promptSendSites.testkit.ts) - the composer, the queue's edit, the Voice tab, the Fleet Manager's
// quick prompts and the dictation resume at start-up - each is driven with a unique marker while every Gateway call
// fails, and no body posted to /client-errors may carry the marker. A derived site with no driver fails by name; a
// run that derives none fails as a broken scan.
//
// Only what jsdom cannot provide is faked: the dictation dialog (a microphone) and the dictation store (IndexedDB).
// The Fleet Manager's setting and page answers are faked too, because its quick prompts exist only on a running
// Fleet Manager whose page offers them - here the quick prompt's words ARE the marker.

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

vi.mock("@devthrottle/client-core/settings/fleetManagerClient", async () => {
  const { placement } = await import("../fleetmanager/fixtures");
  return {
    getFleetManagerPlacement: vi.fn(async () => placement("running")),
    startFleetManager: vi.fn(async () => placement("running")),
    restartFleetManager: vi.fn(async () => placement("running")),
  };
});
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", async () => {
  const { morningPage } = await import("../fleetmanager/fixtures");
  return {
    getFleetManagerPage: vi.fn(async () => ({ ...morningPage(), quickPrompts: [{ label: "fake-quick-prompt", words: `${MARKER} quick` }] })),
    answerFleetOutcome: vi.fn(async () => undefined),
  };
});

import { promptSendSites } from "@devthrottle/client-core/errors/promptSendSites.testkit";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { useVoiceMode } from "@devthrottle/client-core/voice/useVoiceMode";
import { resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";
import { SessionComposer } from "./SessionComposer";
import { QueuePanel } from "./QueuePanel";
import { FleetManagerView } from "../fleetmanager/FleetManagerView";
import { FM_SESSION } from "../fleetmanager/fixtures";

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
  "apps/cockpit/src/sessions/QueuePanel.tsx#editQueueItem#saveEdit": async () => {
    render(
      <QueuePanel sessionId={SID} queue={[{ id: "q-1", text: "queued", createdAt: "2026-10-10T00:00:00Z" }]} onQueue={() => {}} onPop={() => {}} />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.change(screen.getByDisplayValue("queued"), { target: { value: `${MARKER} edited` } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(document.querySelector(".qpanel-error")).not.toBeNull());
  },
  "apps/cockpit/src/sessions/VoiceTab.tsx#useVoiceMode#VoiceTab": async () => {
    const { result } = renderHook(() => useVoiceMode(SID));
    await act(async () => {
      await result.current.onRespondSend(`${MARKER} spoken reply`);
    });
    expect(result.current.error).toBeTruthy();
    const before = fetchMock.mock.calls.length;
    act(() =>
      result.current.onRespondSendAudio({
        sentAt: Date.now(),
        blob: new Blob(["clip"]),
        recordedMs: 1000,
        prefixText: `${MARKER} earlier`,
        surface: "cockpit",
      } as never),
    );
    await waitFor(() => expect(fetchMock.mock.calls.slice(before).some(([u]) => String(u).startsWith("/dictation"))).toBe(true));
  },
  "apps/cockpit/src/fleetmanager/FleetManagerView.tsx#sendTypedPrompt#sendQuick": async () => {
    render(
      <MemoryRouter initialEntries={["/fleet-manager"]}>
        <FleetManagerView />
      </MemoryRouter>,
    );
    const quick = await screen.findByRole("button", { name: "fake-quick-prompt" });
    await waitFor(() => expect((quick as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(quick);
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([u]) => String(u).includes(`/sessions/${FM_SESSION}/`))).toBe(true),
    );
    await waitFor(() => expect(document.querySelector(".fmp-bar-bad")).not.toBeNull());
  },
  "apps/cockpit/src/AppShell.tsx#resumePendingDictations#ShellFrame": async () => {
    pending.set("pending-1", {
      id: "pending-1",
      sessionId: SID,
      blob: new Blob(["clip"]),
      recordedMs: 1000,
      surface: "cockpit-send",
      before: `${MARKER} before`,
      after: `${MARKER} after`,
      prefix: `${MARKER} prefix`,
      createdAt: Date.now(),
      sentAt: Date.now(),
    });
    const before = fetchMock.mock.calls.length;
    await resumePendingDictations();
    await waitFor(() => expect(fetchMock.mock.calls.slice(before).some(([u]) => String(u).startsWith("/dictation"))).toBe(true));
  },
};

describe("no prompt-send path in the Cockpit puts the prompt's words into an error report", () => {
  const sites = promptSendSites(["apps/cockpit/src"]);

  it("derives the send sites from the code, and every one has a driver", () => {
    console.log(`[promptWordsNeverReported] cockpit: ${sites.length} prompt-send sites derived: ${sites.map((s) => s.id).join(", ")}`);
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
    console.log(`[promptWordsNeverReported] cockpit: exercised ${exercised} sites, captured ${reports.length} report bodies`);
    expect(exercised).toBe(sites.length);
    expect(reports.length).toBeGreaterThan(0);
    for (const body of reports) expect(body).not.toContain(MARKER);
  });
});
