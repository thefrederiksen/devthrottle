// The Alerts bell, tray and opened alert (the Alerts mission, phase 3), rendered against a fake Gateway that answers the
// four alert routes and the three features' answer routes.
//
// What these prove that a Gateway test cannot: the bell shows the Gateway's count of alerts that NEED AN ANSWER and
// nothing else ("9+" above nine, no badge at zero); the tray puts "Needs you" above "Earlier", newest first; opening it
// marks what it shows as seen; an X clears one Earlier alert and "Clear all" clears the rest, a refusal is shown in the
// Gateway's words; a failed read keeps the last-known list on screen; Escape closes, focus stays inside while open and
// returns to the bell after; and the longest real strings reach the screen inside lines that are allowed to wrap.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { AlertsProvider, ALERTS_POLL_MS, useAlerts } from "./AlertsProvider";
import { AlertsBell } from "./AlertsBell";
import { badgeText, bellLabel, listAlerts, type AlertDto } from "./alertsClient";
import { LONG_REASON, LONG_VERSION, messageAlert, restartAlert, secretAlert } from "./alertsFixtures.testkit";

interface FakeState {
  alerts: AlertDto[];
  needsAnswerCount: number;
  /** Status the list read answers with; anything but 200 is a failed read. */
  listStatus: number;
  /** The body a 200 list read sends, when a test wants a malformed one. */
  listBody?: unknown;
  clearStatus: number;
  clearBody: unknown;
}

let state: FakeState;
let calls: { url: string; method: string; body?: string }[];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function installGateway() {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      calls.push({ url, method, body: typeof init?.body === "string" ? init.body : undefined });
      if (url === "/gateway/alerts" && method === "GET") {
        if (state.listStatus !== 200) return json({ error: "The Gateway is restarting." }, state.listStatus);
        return json(state.listBody ?? { alerts: state.alerts, needsAnswerCount: state.needsAnswerCount });
      }
      if (url === "/gateway/alerts/seen") {
        const ids = (JSON.parse(String(init?.body)) as { ids: string[] }).ids;
        state.alerts = state.alerts.map((a) => (ids.includes(a.id) ? { ...a, seen: true } : a));
        return json({});
      }
      if (url === "/gateway/alerts/clear") {
        const before = state.alerts.length;
        state.alerts = state.alerts.filter((a) => a.needsAnswer);
        return json({ cleared: before - state.alerts.length });
      }
      const one = /^\/gateway\/alerts\/([^/]+)\/clear$/.exec(url);
      if (one) {
        if (state.clearStatus !== 200) return json(state.clearBody, state.clearStatus);
        const id = decodeURIComponent(one[1]);
        state.alerts = state.alerts.filter((a) => a.id !== id);
        return json({});
      }
      if (url === "/machines/SOREN_NORTH/director/restart-requests/req-1/decline") {
        state.alerts = state.alerts.map((a) => (a.id === "restart:req-1" ? { ...a, needsAnswer: false, status: "Declined" } : a));
        state.needsAnswerCount = state.alerts.filter((a) => a.needsAnswer).length;
        return json({});
      }
      throw new Error(`unexpected request: ${method} ${url}`);
    }),
  );
}

function given(alerts: AlertDto[], needsAnswerCount = alerts.filter((a) => a.needsAnswer).length) {
  state.alerts = alerts;
  state.needsAnswerCount = needsAnswerCount;
}

function renderBell(props: { openAlertId?: string | null } = {}) {
  return render(
    <AlertsProvider>
      <button type="button">Before the bell</button>
      <AlertsBell where="phone" {...props} />
    </AlertsProvider>,
  );
}

async function loaded() {
  await waitFor(() => expect(calls.some((c) => c.url === "/gateway/alerts")).toBe(true));
}

async function openTray() {
  fireEvent.click(screen.getByTestId("alerts-bell"));
  return screen.findByTestId("alerts-tray");
}

beforeEach(() => {
  state = { alerts: [], needsAnswerCount: 0, listStatus: 200, clearStatus: 200, clearBody: {} };
  calls = [];
  installGateway();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("the bell", () => {
  it("AlertsBell_Zero_DrawsNoBadge_AndTheTraySaysNothingNeedsYou", async () => {
    renderBell();
    await loaded();
    expect(screen.queryByTestId("alerts-badge")).toBeNull();
    expect(screen.getByTestId("alerts-bell").getAttribute("aria-label")).toBe("Alerts");
    const tray = await openTray();
    expect(await within(tray).findByText("Nothing needs you.")).toBeTruthy();
    expect(within(tray).queryByText("Earlier")).toBeNull();
    expect(within(tray).queryByRole("button", { name: "Clear all" })).toBeNull();
  });

  it("AlertsBell_One_ShowsOne_AndNamesItInTheLabel", async () => {
    given([restartAlert()]);
    renderBell();
    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("1"));
    expect(screen.getByTestId("alerts-bell").getAttribute("aria-label")).toBe("Alerts, 1 needs you");
  });

  it("AlertsBell_CountsOnlyAlertsThatNeedAnAnswer", async () => {
    given([
      restartAlert(),
      secretAlert({ needsAnswer: false, status: "Approved" }),
      messageAlert({ needsAnswer: false, status: "Allowed" }),
    ]);
    renderBell();
    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("1"));
    expect(screen.getByTestId("alerts-bell").getAttribute("aria-label")).toBe("Alerts, 1 needs you");
  });

  it("AlertsBell_ShowsTheGatewaysCount_AsNinePlusAboveNine", async () => {
    given([restartAlert()], 12);
    renderBell();
    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("9+"));
    expect(screen.getByTestId("alerts-bell").getAttribute("aria-label")).toBe("Alerts, 12 need you");
  });

  it("badgeText_and_bellLabel_AtTheEdges", () => {
    expect(badgeText(0)).toBe("");
    expect(badgeText(9)).toBe("9");
    expect(badgeText(10)).toBe("9+");
    expect(bellLabel(0)).toBe("Alerts");
    expect(bellLabel(2)).toBe("Alerts, 2 need you");
  });
});

describe("the tray", () => {
  it("AlertsTray_Many_PutsNeedsYouAboveEarlier_NewestFirst", async () => {
    given([
      secretAlert({ id: "secret:old", title: "Older question", updatedAtUtc: "2026-10-24T20:00:00Z" }),
      restartAlert({ id: "restart:done", title: "Accepted restart", needsAnswer: false, status: "Accepted", updatedAtUtc: "2026-10-24T21:50:00Z" }),
      messageAlert({ id: "message:new", title: "Newer question", updatedAtUtc: "2026-10-24T21:00:00Z" }),
      secretAlert({ id: "secret:denied", title: "Denied transfer", needsAnswer: false, status: "Denied", updatedAtUtc: "2026-10-24T19:00:00Z" }),
    ]);
    renderBell();
    await loaded();
    const tray = await openTray();
    await within(tray).findByText("Newer question");
    const titles = within(tray).getAllByTestId("alerts-row").map((r) => r.getAttribute("data-alert-id"));
    expect(titles).toEqual(["message:new", "secret:old", "restart:done", "secret:denied"]);
    const needs = within(tray).getByRole("region", { name: "Needs you" });
    const earlier = within(tray).getByRole("region", { name: "Earlier" });
    expect(within(needs).getAllByTestId("alerts-row")).toHaveLength(2);
    expect(within(earlier).getAllByTestId("alerts-row")).toHaveLength(2);
    // The status word shows on an Earlier row; the X exists only there.
    expect(within(earlier).getByText("Accepted")).toBeTruthy();
    expect(within(needs).queryByRole("button", { name: /^Clear:/ })).toBeNull();
    expect(within(earlier).getAllByRole("button", { name: /^Clear:/ })).toHaveLength(2);
  });

  it("AlertsTray_Opening_MarksOnlyTheUnseenAlertsSeen", async () => {
    given([restartAlert({ seen: false }), secretAlert({ seen: true })]);
    renderBell();
    await loaded();
    // Nothing is marked seen before the owner opens the tray.
    expect(calls.some((c) => c.url === "/gateway/alerts/seen")).toBe(false);
    await openTray();
    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/alerts/seen")).toBe(true));
    const seen = calls.filter((c) => c.url === "/gateway/alerts/seen");
    expect(seen).toHaveLength(1);
    expect(JSON.parse(seen[0].body ?? "{}")).toEqual({ ids: ["restart:req-1"] });
  });

  it("AlertsTray_X_ClearsThatEarlierAlert_AndReadsTheListAgain", async () => {
    given([restartAlert(), secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderBell();
    await loaded();
    const tray = await openTray();
    fireEvent.click(await within(tray).findByRole("button", { name: "Clear: Move vercel-bypass to devthrottle-mac-mini?" }));
    await waitFor(() => expect(within(tray).queryByText("Move vercel-bypass to devthrottle-mac-mini?")).toBeNull());
    expect(calls).toContainEqual(expect.objectContaining({ url: "/gateway/alerts/secret%3Atr-1/clear", method: "POST" }));
    expect(within(tray).getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy();
  });

  it("AlertsTray_ClearAll_ClearsEveryEarlierAlert_AndKeepsWhatNeedsAnAnswer", async () => {
    given([
      restartAlert({ seen: true }),
      secretAlert({ needsAnswer: false, status: "Approved", seen: true }),
      messageAlert({ needsAnswer: false, status: "Allowed", seen: true }),
    ]);
    renderBell();
    await loaded();
    const tray = await openTray();
    fireEvent.click(await within(tray).findByRole("button", { name: "Clear all" }));
    await waitFor(() => expect(within(tray).queryByText("Earlier")).toBeNull());
    expect(calls).toContainEqual(expect.objectContaining({ url: "/gateway/alerts/clear", method: "POST" }));
    expect(within(tray).getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy();
    // With Earlier empty there is nothing left to clear, so the button goes.
    expect(within(tray).queryByRole("button", { name: "Clear all" })).toBeNull();
  });

  it("AlertsTray_ClearRefused_ShowsTheGatewaysSentence", async () => {
    const sentence = "This still needs your answer; it leaves when you answer it or when it expires.";
    state.clearStatus = 409;
    state.clearBody = { error: sentence };
    given([secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderBell();
    await loaded();
    const tray = await openTray();
    fireEvent.click(await within(tray).findByRole("button", { name: /^Clear:/ }));
    expect(await within(tray).findByText(sentence)).toBeTruthy();
    // The alert stays on screen; a refused clear removed nothing.
    expect(within(tray).getByTestId("alerts-row")).toBeTruthy();
  });

  it("AlertsTray_ReadFails_KeepsTheLastKnownList_AndSaysSo", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    given([restartAlert({ seen: true })]);
    renderBell();
    const tray = await openTray();
    await within(tray).findByText("Restart the Director on SOREN_NORTH?");

    state.listStatus = 503;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ALERTS_POLL_MS);
    });

    expect((await within(tray).findByRole("alert")).textContent).toContain("Could not load the alerts: The Gateway is restarting.");
    expect(within(tray).getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy();
    expect(screen.getByTestId("alerts-badge").textContent).toBe("1");
  });

  it("AlertsTray_ReadFailsBeforeAnyList_SaysSoAndClaimsNothing", async () => {
    state.listStatus = 503;
    renderBell();
    const tray = await openTray();
    expect((await within(tray).findByRole("alert")).textContent).toContain("Could not load the alerts");
    // It does not know that nothing needs the owner, so it must not say so.
    expect(within(tray).queryByText("Nothing needs you.")).toBeNull();
  });

  it("listAlerts_ABodyWithoutTheAlertsArray_IsAnError_NeverAnEmptyList", async () => {
    state.listBody = { items: [], needsAnswerCount: 0 };
    await expect(listAlerts()).rejects.toThrow(/without an 'alerts' array/);
    state.listBody = { alerts: [] };
    await expect(listAlerts()).rejects.toThrow(/without a 'needsAnswerCount'/);
  });
});

describe("an opened alert", () => {
  it("AlertDetail_Restart_ShowsTheGatewaysWords_TheTwoStepAnswer_AndCollapsedDetails", async () => {
    given([restartAlert({ seen: true })]);
    renderBell();
    await loaded();
    const tray = await openTray();
    fireEvent.click(await within(tray).findByText("Restart the Director on SOREN_NORTH?"));
    const detail = await within(tray).findByTestId("alert-detail");
    expect(within(detail).getByText("Rig Alpha asks: the launcher has a staged update.")).toBeTruthy();
    expect(within(detail).getByText(/holds 7 live sessions/)).toBeTruthy();
    expect(within(detail).getByText(/^Expires /)).toBeTruthy();
    const details = detail.querySelector("details");
    expect(details?.open).toBe(false);
    expect(details?.textContent).toContain("declares director/restart:only-if-empty");
    fireEvent.click(within(detail).getByText("Accept"));
    expect(within(detail).getByText("Yes, restart it")).toBeTruthy();
  });

  it("AlertDetail_AnAnswer_ReadsTheListAgain_AndTheCountDropsAtOnce", async () => {
    given([restartAlert({ seen: true })]);
    renderBell();
    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("1"));
    const tray = await openTray();
    fireEvent.click(await within(tray).findByText("Restart the Director on SOREN_NORTH?"));
    fireEvent.click(await within(tray).findByText("Decline"));
    await waitFor(() => expect(screen.queryByTestId("alerts-badge")).toBeNull());
    expect(within(tray).getByText("Declined")).toBeTruthy();
  });

  it("AlertDetail_SecretTransfer_And_MessageRequest_RenderTheirOwnAnswerBlocks", async () => {
    given([secretAlert({ seen: true }), messageAlert({ seen: true, updatedAtUtc: "2026-10-24T21:00:00Z" })]);
    renderBell();
    await loaded();
    const tray = await openTray();
    fireEvent.click(await within(tray).findByText("Move vercel-bypass to devthrottle-mac-mini?"));
    let detail = await within(tray).findByTestId("alert-detail");
    expect(within(detail).getByText("Replaces the entry already on devthrottle-mac-mini.")).toBeTruthy();
    expect(within(detail).getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(within(detail).getByRole("button", { name: "Deny" })).toBeTruthy();

    fireEvent.click(within(tray).getByRole("button", { name: "Back to alerts" }));
    fireEvent.click(await within(tray).findByText("Let 121 message 125?"));
    detail = await within(tray).findByTestId("alert-detail");
    expect(within(detail).getByText("Asks for: one message and a reply")).toBeTruthy();
    expect(within(detail).getByText('"I need to hand the merge over"')).toBeTruthy();
  });

  it("AlertsBell_DeepLink_OpensTheTrayOnThatAlert", async () => {
    given([restartAlert({ seen: true }), secretAlert({ seen: true })]);
    renderBell({ openAlertId: "secret:tr-1" });
    const detail = await screen.findByTestId("alert-detail");
    expect(within(detail).getByText("Move vercel-bypass to devthrottle-mac-mini?")).toBeTruthy();
  });

  it("AlertDetail_OverflowFixture_PutsTheLongestStringsInLinesThatWrap", async () => {
    given([
      restartAlert(
        { seen: true, summary: LONG_REASON },
        { capability: { verdict: "CanRestart", reason: LONG_REASON, guardedRestart: "Available", guardedRestartReason: LONG_REASON, launcherVersion: LONG_VERSION } },
      ),
    ]);
    expect(LONG_VERSION).toHaveLength(46);
    expect(LONG_REASON).toHaveLength(300);
    renderBell();
    await loaded();
    const tray = await openTray();
    // The row's one-line summary holds the 300-character reason, ellipsised, in a class that wraps and shrinks.
    const row = await within(tray).findByTestId("alerts-row");
    expect(row.querySelector(".alerts-row-summary")?.textContent).toBe(LONG_REASON);
    fireEvent.click(within(row).getByText("Restart the Director on SOREN_NORTH?"));
    const detail = await within(tray).findByTestId("alert-detail");
    const lines = [...detail.querySelectorAll(".alert-line")];
    const versionLine = lines.find((l) => l.textContent?.includes(LONG_VERSION));
    const reasonLines = lines.filter((l) => l.textContent?.includes(LONG_REASON));
    expect(versionLine).toBeTruthy();
    expect(reasonLines.length).toBeGreaterThanOrEqual(2);
    // Every class that holds text in the tray must wrap anywhere and be allowed to shrink. jsdom does no layout, so the
    // rule itself is what is checked here; the screenshots at 360 px and 280 px are phase 4's QA run.
    for (const cls of ["alert-line", "alerts-row-title", "alerts-row-summary", "alert-detail-title", "alert-status", "alerts-tray-heading", "alerts-section-heading", "alert-details-summary", "alerts-row-time"]) {
      const rule = cssRule(cls);
      expect(rule, `.${cls} must wrap anywhere`).toMatch(/overflow-wrap:\s*anywhere/);
      expect(rule, `.${cls} must be allowed to shrink`).toMatch(/min-width:\s*0/);
    }
  });
});

describe("keyboard and focus", () => {
  it("AlertsTray_Opening_MovesFocusIn_TabStaysInside_EscapeClosesAndReturnsFocusToTheBell", async () => {
    given([restartAlert({ seen: true }), secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderBell();
    await loaded();
    const bell = screen.getByTestId("alerts-bell");
    bell.focus();
    const tray = await openTray();
    await within(tray).findByText("Restart the Director on SOREN_NORTH?");
    expect(bell.getAttribute("aria-expanded")).toBe("true");
    expect(tray.getAttribute("role")).toBe("dialog");
    expect(tray.contains(document.activeElement)).toBe(true);

    const buttons = within(tray).getAllByRole("button");
    const first = buttons[0];
    const last = buttons[buttons.length - 1];
    last.focus();
    fireEvent.keyDown(tray, { key: "Tab" });
    expect(document.activeElement).toBe(first);
    fireEvent.keyDown(tray, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(last);

    fireEvent.keyDown(tray, { key: "Escape" });
    expect(screen.queryByTestId("alerts-tray")).toBeNull();
    expect(document.activeElement).toBe(bell);
    expect(bell.getAttribute("aria-expanded")).toBe("false");
  });

  it("AlertsTray_RowsAreButtons_AndTheCloseButtonReturnsFocusToTheBell", async () => {
    given([restartAlert({ seen: true })]);
    renderBell();
    await loaded();
    const tray = await openTray();
    const row = await within(tray).findByTestId("alerts-row");
    expect(within(row).getAllByRole("button").length).toBeGreaterThan(0);
    fireEvent.click(within(tray).getByRole("button", { name: "Close alerts" }));
    expect(screen.queryByTestId("alerts-tray")).toBeNull();
    expect(document.activeElement).toBe(screen.getByTestId("alerts-bell"));
  });
});

describe("waitingFrom", () => {
  it("lists only the alerts that need an answer and were asked by that session, by kind if asked", async () => {
    given([
      secretAlert({ id: "secret:a" }, { askedBySessionId: "AAAAAAAA-1111-2222-3333-444444444444" }),
      secretAlert({ id: "secret:b", needsAnswer: false }, { askedBySessionId: "aaaaaaaa-1111-2222-3333-444444444444" }),
      messageAlert({ id: "message:c" }, { requesterSessionId: "aaaaaaaa-1111-2222-3333-444444444444" }),
      restartAlert({ id: "restart:d" }),
    ]);
    function Probe() {
      const { waitingFrom } = useAlerts();
      const all = waitingFrom("aaaaaaaa-1111-2222-3333-444444444444").map((a) => a.id).join(",");
      const secrets = waitingFrom("aaaaaaaa-1111-2222-3333-444444444444", "secret-transfer").map((a) => a.id).join(",");
      return <span data-testid="probe">{`${all}|${secrets}`}</span>;
    }
    render(
      <AlertsProvider>
        <Probe />
      </AlertsProvider>,
    );
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("secret:a,message:c|secret:a"));
  });

  it("outside a provider there are no alerts and no poll", () => {
    function Probe() {
      return <span data-testid="probe">{useAlerts().needsAnswerCount}</span>;
    }
    render(<Probe />);
    expect(screen.getByTestId("probe").textContent).toBe("0");
    expect(calls).toHaveLength(0);
  });
});

describe("the one stylesheet", () => {
  it("no shell stylesheet writes an alerts rule - a shell may only set the position and size variables", () => {
    const appsRoot = join(__dirname, "..", "..", "..", "..", "apps");
    const offenders: string[] = [];
    for (const file of cssFiles(appsRoot)) {
      const text = readFileSync(file, "utf8");
      if (/\.alerts?-[a-z-]+[^{;]*\{/.test(text) || /\.alerts\s*[{,.:]/.test(text)) offenders.push(file);
    }
    expect(offenders).toEqual([]);
  });
});

/** The declarations of one single-class rule in alerts.css. */
function cssRule(cls: string): string {
  const css = readFileSync(join(__dirname, "alerts.css"), "utf8");
  const match = new RegExp(`(^|\\n)\\.${cls}\\s*\\{([^}]*)\\}`).exec(css);
  if (!match) throw new Error(`alerts.css has no rule for .${cls}`);
  return match[2];
}

function cssFiles(dir: string): string[] {
  const out: string[] = [];
  for (const name of readdirSync(dir)) {
    if (name === "node_modules" || name === "dist") continue;
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...cssFiles(full));
    else if (name.endsWith(".css")) out.push(full);
  }
  return out;
}
