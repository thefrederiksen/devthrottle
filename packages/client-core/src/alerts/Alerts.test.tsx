// The Alerts bell, the Alerts content and the Cockpit's popover (the Alerts mission, phases 3 and 5), rendered against a
// fake Gateway that answers the four alert routes and the three features' answer routes.
//
// What these prove that a Gateway test cannot: the bell shows the Gateway's count of alerts that NEED AN ANSWER and
// nothing else ("9+" above nine, no badge at zero), and on the phone goes to the Alerts screen instead of a popover;
// "Needs you" is a list of cards with each kind's answer ON the card - one tap answers, the restart still asks twice -
// and an answered card leaves at once with a line in the Gateway's status word; "Earlier" is collapsed, with its count
// and "Clear all" in its own header, and opens to compact rows that open in place; showing the list marks what it shows
// as seen; an X clears one Earlier alert and a refusal is shown in the Gateway's words; a failed read keeps the
// last-known list; a deep link puts focus on its alert; the popover keeps focus inside and returns it to the bell; and
// the longest real strings reach the screen in full, inside lines that are allowed to wrap.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { AlertsProvider, ALERTS_POLL_MS, useAlerts } from "./AlertsProvider";
import { AlertsBell } from "./AlertsBell";
import { AlertsList, ANSWERED_LINE_MS } from "./AlertsList";
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
  /** When set, what an answer turns its alert into on the Gateway instead of the plain answered word. */
  outcome?: { status: string; tone: "good" | "bad" | "neutral" };
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
      // The three features' own answer routes: each turns its alert into an answered one, as the Gateway would.
      const answered = (id: string, status: string, body: unknown = {}) => {
        const after = state.outcome ?? { status, tone: "neutral" as const };
        state.alerts = state.alerts.map((a) => (a.id === id ? { ...a, needsAnswer: false, ...after } : a));
        state.needsAnswerCount = state.alerts.filter((a) => a.needsAnswer).length;
        return json(body);
      };
      if (url === "/machines/SOREN_NORTH/director/restart-requests/req-1/decline") return answered("restart-request:req-1", "Declined");
      if (url === "/machines/SOREN_NORTH/director/restart-requests/req-1/accept") return answered("restart-request:req-1", "Accepted");
      if (url === "/gateway/secrets/transfers/tr-1/answer") {
        const approve = (JSON.parse(String(init?.body)) as { approve?: boolean }).approve === true;
        return answered("secret-transfer:tr-1", approve ? "Approved" : "Denied", { transfer: {}, note: "" });
      }
      if (url === "/fleet/link-requests/lr-1/answer") {
        const approve = (JSON.parse(String(init?.body)) as { approve?: boolean }).approve === true;
        return answered("message-request:lr-1", approve ? "Allowed" : "Declined", { request: {}, note: "" });
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

/** The Alerts content on its own, as the phone's Alerts screen mounts it. */
function renderList(focusAlertId: string | null = null) {
  return render(
    <AlertsProvider>
      <AlertsList where="phone" focusAlertId={focusAlertId} />
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
    expect(within(tray).queryByRole("region", { name: "Earlier" })).toBeNull();
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

describe("the bell on the phone", () => {
  it("AlertsBell_WithOnOpenPage_GoesToTheAlertsScreen_AndDrawsNoPopover", async () => {
    given([restartAlert()]);
    const onOpenPage = vi.fn();
    render(
      <AlertsProvider>
        <AlertsBell where="phone" onOpenPage={onOpenPage} />
      </AlertsProvider>,
    );
    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("1"));
    const bell = screen.getByTestId("alerts-bell");
    expect(bell.getAttribute("aria-haspopup")).toBeNull();
    fireEvent.click(bell);
    expect(onOpenPage).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("alerts-tray")).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("the list", () => {
  it("AlertsList_Many_NeedsYouAsCards_ThenEarlierCollapsed_NewestFirst", async () => {
    given([
      secretAlert({ id: "secret-transfer:old", title: "Older question", updatedAtUtc: "2026-10-24T20:00:00Z" }),
      restartAlert({ id: "restart-request:done", title: "Accepted restart", needsAnswer: false, status: "Accepted", updatedAtUtc: "2026-10-24T21:50:00Z" }),
      messageAlert({ id: "message-request:new", title: "Newer question", updatedAtUtc: "2026-10-24T21:00:00Z" }),
      secretAlert({ id: "secret-transfer:denied", title: "Denied transfer", needsAnswer: false, status: "Denied", updatedAtUtc: "2026-10-24T19:00:00Z" }),
    ]);
    renderList();
    const needs = await screen.findByRole("region", { name: "Needs you" });
    await within(needs).findByText("Newer question");
    // Needs you: one card per alert, newest first, each with its answer buttons on it and no X.
    const cards = within(needs).getAllByTestId("alert-card");
    expect(cards.map((c) => c.getAttribute("data-alert-id"))).toEqual(["message-request:new", "secret-transfer:old"]);
    expect(within(cards[0]).getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(within(cards[1]).getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(within(needs).queryByRole("button", { name: /^Clear:/ })).toBeNull();
    // A card does not carry a status word competing with its question.
    expect(within(needs).queryByText("Waiting")).toBeNull();

    // Earlier: collapsed by default, its count in the header beside Clear all.
    const earlier = screen.getByRole("region", { name: "Earlier" });
    const toggle = within(earlier).getByRole("button", { name: "Earlier (2)" });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(within(earlier).queryAllByTestId("alerts-row")).toHaveLength(0);
    expect(within(earlier).getByRole("button", { name: "Clear all" })).toBeTruthy();

    fireEvent.click(toggle);
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    const rows = within(earlier).getAllByTestId("alerts-row");
    expect(rows.map((r) => r.getAttribute("data-alert-id"))).toEqual(["restart-request:done", "secret-transfer:denied"]);
    expect(within(rows[0]).getByText("Accepted")).toBeTruthy();
    expect(within(earlier).getAllByRole("button", { name: /^Clear:/ })).toHaveLength(2);
  });

  it("AlertsList_Card_ShowsTheQuestion_WhoAskedAndWhyOnce_TheExpiry_AndTheButtons", async () => {
    given([secretAlert({ seen: true, expiresAtUtc: "2026-10-24T22:26:00Z" })]);
    renderList();
    const card = await screen.findByTestId("alert-card");
    expect(within(card).getByRole("heading", { name: "Send the secret vercel-bypass to devthrottle-mac-mini?" })).toBeTruthy();
    expect(within(card).getByText("Rig Developer (042) asks: The preview deploy needs it")).toBeTruthy();
    // The reason is in that one sentence; the card never quotes it a second time.
    expect(card.textContent?.split("The preview deploy needs it")).toHaveLength(2);
    expect(within(card).getByText(/^Expires /)).toBeTruthy();
    expect(within(card).getByText("Replaces the entry already on devthrottle-mac-mini.")).toBeTruthy();
    expect(within(card).getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(within(card).getByRole("button", { name: "Deny" })).toBeTruthy();
    expect(card.querySelector("details")?.open).toBe(false);
  });

  it("AlertsList_SecretTransfer_ApprovedFromTheCard_LeavesNeedsYou_WithALineSayingSo_ThenTheLineGoes", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    given([secretAlert({ seen: true }), restartAlert({ id: "restart-request:old", needsAnswer: false, status: "Declined", seen: true, updatedAtUtc: "2026-10-24T20:00:00Z" })]);
    renderList();
    const card = await screen.findByTestId("alert-card");
    fireEvent.click(within(card).getByRole("button", { name: "Approve" }));

    const line = await screen.findByTestId("alerts-answered");
    expect(line.textContent).toBe("Approved: Send the secret vercel-bypass to devthrottle-mac-mini?");
    expect(screen.queryByTestId("alert-card")).toBeNull();
    expect(screen.getByRole("button", { name: "Earlier (2)" })).toBeTruthy();
    expect(calls).toContainEqual(expect.objectContaining({ url: "/gateway/secrets/transfers/tr-1/answer", method: "POST", body: JSON.stringify({ approve: true, where: "phone" }) }));
    // It is recorded as answered on the phone, exactly as before.

    await act(async () => {
      await vi.advanceTimersByTimeAsync(ANSWERED_LINE_MS);
    });
    expect(screen.queryByTestId("alerts-answered")).toBeNull();
  });

  // Phase 5 review, observation 3: a failed answer must not read as a calm note. The Gateway rules the tone; the line
  // and the Earlier row only colour what they are told.
  it("AlertsList_AnAnswerThatFailed_ReadsAsBad_InTheLineAndTheEarlierRow", async () => {
    state.outcome = { status: "Failed", tone: "bad" };
    given([secretAlert({ seen: true })]);
    renderList();
    fireEvent.click(within(await screen.findByTestId("alert-card")).getByRole("button", { name: "Approve" }));
    const line = await screen.findByTestId("alerts-answered");
    expect(line.textContent).toBe("Failed: Send the secret vercel-bypass to devthrottle-mac-mini?");
    expect(line.getAttribute("data-tone")).toBe("bad");
    expect(line.className).toContain("alert-tone-bad");
    fireEvent.click(screen.getByRole("button", { name: "Earlier (1)" }));
    expect(within(screen.getByTestId("alerts-row")).getByText("Failed").className).toContain("alert-tone-bad");
  });

  it("AlertsList_AnAnswerThatWorked_ReadsAsGood_AndAnUnknownToneAsNeutral", async () => {
    state.outcome = { status: "Allowed", tone: "good" };
    given([messageAlert({ seen: true }), secretAlert({ needsAnswer: false, status: "Odd", tone: "sideways" as never, seen: true })]);
    renderList();
    fireEvent.click(within(await screen.findByTestId("alert-card")).getByRole("button", { name: "Approve" }));
    expect((await screen.findByTestId("alerts-answered")).className).toContain("alert-tone-good");
    fireEvent.click(screen.getByRole("button", { name: "Earlier (2)" }));
    expect(screen.getByText("Odd").className).toContain("alert-tone-neutral");
  });

  it("AlertsList_MessageRequest_DeniedFromTheCard_InOneTap", async () => {
    given([messageAlert({ seen: true })]);
    renderList();
    const card = await screen.findByTestId("alert-card");
    expect(within(card).getByText("Asks for: one message and a reply")).toBeTruthy();
    expect(within(card).getByText("Its reason: I need to hand the merge over")).toBeTruthy();
    fireEvent.click(within(card).getByRole("button", { name: "Deny" }));
    expect((await screen.findByTestId("alerts-answered")).textContent).toBe("Declined: Fix the Release Manager (121) wants to message Release Manager (125)");
    expect(calls).toContainEqual(expect.objectContaining({ url: "/fleet/link-requests/lr-1/answer", method: "POST" }));
    expect(screen.queryByTestId("alert-card")).toBeNull();
  });

  it("AlertsList_Restart_AcceptedFromTheCard_InTwoSteps", async () => {
    given([restartAlert({ seen: true })]);
    renderList();
    const card = await screen.findByTestId("alert-card");
    expect(within(card).getByText(/holds 7 live sessions/)).toBeTruthy();
    fireEvent.click(within(card).getByRole("button", { name: "Accept" }));
    // The first tap only asks; nothing is sent.
    expect(within(card).getByText(/^Accept once, and nothing more is asked of you/)).toBeTruthy();
    expect(calls.some((c) => c.url.endsWith("/accept"))).toBe(false);
    fireEvent.click(within(card).getByRole("button", { name: "Yes, restart it" }));
    expect((await screen.findByTestId("alerts-answered")).textContent).toBe("Accepted: Restart the Director on SOREN_NORTH?");
    expect(calls).toContainEqual(expect.objectContaining({ url: "/machines/SOREN_NORTH/director/restart-requests/req-1/accept", method: "POST" }));
  });

  it("AlertsList_AnAnswer_ReadsTheListAgain_AndTheCountDropsAtOnce", async () => {
    given([restartAlert({ seen: true })]);
    renderBell();
    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("1"));
    const tray = await openTray();
    fireEvent.click(await within(tray).findByRole("button", { name: "Decline" }));
    await waitFor(() => expect(screen.queryByTestId("alerts-badge")).toBeNull());
    expect(within(tray).getByText("Declined: Restart the Director on SOREN_NORTH?")).toBeTruthy();
  });

  it("AlertsList_Showing_MarksOnlyTheUnseenAlertsSeen", async () => {
    given([restartAlert({ seen: false }), secretAlert({ seen: true })]);
    renderBell();
    await loaded();
    // Nothing is marked seen before the owner opens Alerts.
    expect(calls.some((c) => c.url === "/gateway/alerts/seen")).toBe(false);
    await openTray();
    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/alerts/seen")).toBe(true));
    const seen = calls.filter((c) => c.url === "/gateway/alerts/seen");
    expect(seen).toHaveLength(1);
    expect(JSON.parse(seen[0].body ?? "{}")).toEqual({ ids: ["restart-request:req-1"] });
  });

  it("AlertsList_X_ClearsThatEarlierAlert_AndReadsTheListAgain", async () => {
    given([restartAlert(), secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderList();
    fireEvent.click(await screen.findByRole("button", { name: "Earlier (1)" }));
    fireEvent.click(screen.getByRole("button", { name: "Clear: Send the secret vercel-bypass to devthrottle-mac-mini?" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Earlier" })).toBeNull());
    expect(calls).toContainEqual(expect.objectContaining({ url: "/gateway/alerts/secret-transfer%3Atr-1/clear", method: "POST" }));
    expect(screen.getByRole("heading", { name: /Restart the Director on SOREN_NORTH\?/ })).toBeTruthy();
  });

  it("AlertsList_ClearAll_InEarliersHeader_ClearsEveryEarlierAlert_AndKeepsWhatNeedsAnAnswer", async () => {
    given([
      restartAlert({ seen: true }),
      secretAlert({ needsAnswer: false, status: "Approved", seen: true }),
      messageAlert({ needsAnswer: false, status: "Allowed", seen: true }),
    ]);
    renderList();
    const earlier = await screen.findByRole("region", { name: "Earlier" });
    // Clear all is offered without opening the section, and belongs to it.
    fireEvent.click(within(earlier).getByRole("button", { name: "Clear all" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Earlier" })).toBeNull());
    expect(calls).toContainEqual(expect.objectContaining({ url: "/gateway/alerts/clear", method: "POST" }));
    expect(screen.getByTestId("alert-card")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Clear all" })).toBeNull();
  });

  it("AlertsList_ClearRefused_ShowsTheGatewaysSentence", async () => {
    const sentence = "This still needs your answer; it leaves when you answer it or when it expires.";
    state.clearStatus = 409;
    state.clearBody = { error: sentence };
    given([secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderList();
    fireEvent.click(await screen.findByRole("button", { name: "Earlier (1)" }));
    fireEvent.click(screen.getByRole("button", { name: /^Clear:/ }));
    const refusal = await screen.findByText(sentence);
    // It sits indented under its own row (phase 5 review, observation 4), by a rule that outranks .alert-line's margin.
    expect(refusal.closest("[data-testid=alerts-row]")).toBeTruthy();
    expect(refusal.className).toContain("alerts-row-error");
    expect(readFileSync(join(__dirname, "alerts.css"), "utf8")).toMatch(/\.alert-line\.alerts-row-error\s*\{[^}]*margin:\s*0 0 6px 22px/);
    // The alert stays on screen; a refused clear removed nothing.
    expect(screen.getByTestId("alerts-row")).toBeTruthy();
  });

  it("AlertsList_NothingNeedsYou_SaysSo_AndEarlierIsStillThere", async () => {
    given([secretAlert({ needsAnswer: false, status: "Delivered", seen: true })]);
    renderList();
    expect(await screen.findByText("Nothing needs you.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Earlier (1)" })).toBeTruthy();
  });

  it("AlertsList_AnEarlierRow_OpensInPlace_ToItsSummaryAndDetails", async () => {
    given([restartAlert({ needsAnswer: false, status: "Accepted", summary: "Accepted - waiting for the Director.", seen: true }, { state: "Accepted", canAccept: false })]);
    renderList();
    fireEvent.click(await screen.findByRole("button", { name: "Earlier (1)" }));
    const row = screen.getByTestId("alerts-row");
    const open = within(row).getByRole("button", { name: /^Restart the Director on SOREN_NORTH\?/ });
    expect(open.getAttribute("aria-expanded")).toBe("false");
    fireEvent.click(open);
    expect(open.getAttribute("aria-expanded")).toBe("true");
    expect(within(row).getByText("Accepted - waiting for the Director.")).toBeTruthy();
    expect(row.querySelector("details")).toBeTruthy();
    // Nothing to answer on an Earlier alert.
    expect(within(row).queryByRole("button", { name: "Accept" })).toBeNull();
  });

  it("AlertsList_ReadFails_KeepsTheLastKnownList_AndSaysSo", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    given([restartAlert({ seen: true })]);
    renderBell();
    const tray = await openTray();
    await within(tray).findByRole("heading", { name: /Restart the Director on SOREN_NORTH\?/ });

    state.listStatus = 503;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ALERTS_POLL_MS);
    });

    expect((await within(tray).findByRole("alert")).textContent).toContain("Could not load the alerts: The Gateway is restarting.");
    expect(within(tray).getByRole("heading", { name: /Restart the Director on SOREN_NORTH\?/ })).toBeTruthy();
    expect(screen.getByTestId("alerts-badge").textContent).toBe("1");
  });

  it("AlertsList_ReadFailsBeforeAnyList_SaysSoAndClaimsNothing", async () => {
    state.listStatus = 503;
    renderList();
    expect((await screen.findByRole("alert")).textContent).toContain("Could not load the alerts");
    // It does not know that nothing needs the owner, so it must not say so.
    expect(screen.queryByText("Nothing needs you.")).toBeNull();
  });

  it("listAlerts_ABodyWithoutTheAlertsArray_IsAnError_NeverAnEmptyList", async () => {
    state.listBody = { items: [], needsAnswerCount: 0 };
    await expect(listAlerts()).rejects.toThrow(/without an 'alerts' array/);
    state.listBody = { alerts: [] };
    await expect(listAlerts()).rejects.toThrow(/without a 'needsAnswerCount'/);
  });

  it("AlertsList_DeepLinkToACard_PutsFocusOnIt", async () => {
    given([restartAlert({ seen: true }), secretAlert({ seen: true, updatedAtUtc: "2026-10-24T21:00:00Z" })]);
    renderList("secret-transfer:tr-1");
    await waitFor(() => expect((document.activeElement as HTMLElement | null)?.getAttribute("data-alert-id")).toBe("secret-transfer:tr-1"));
  });

  it("AlertsList_DeepLinkToAnEarlierAlert_OpensTheSectionAndTheRow_AndPutsFocusOnIt", async () => {
    given([restartAlert({ seen: true }), messageAlert({ needsAnswer: false, status: "Allowed", seen: true })]);
    renderList("message-request:lr-1");
    await waitFor(() => expect((document.activeElement as HTMLElement | null)?.getAttribute("data-alert-id")).toBe("message-request:lr-1"));
    expect(screen.getByRole("button", { name: "Earlier (1)" }).getAttribute("aria-expanded")).toBe("true");
    expect(within(screen.getByTestId("alerts-row")).getByTestId("alert-detail").getAttribute("data-kind")).toBe("message-request");
  });

  it("AlertsTray_DeepLink_OpensThePopoverOnThatAlert", async () => {
    given([restartAlert({ seen: true }), secretAlert({ seen: true })]);
    renderBell({ openAlertId: "secret-transfer:tr-1" });
    const tray = await screen.findByTestId("alerts-tray");
    await waitFor(() => expect(tray.contains(document.activeElement)).toBe(true));
    expect((document.activeElement as HTMLElement).getAttribute("data-alert-id")).toBe("secret-transfer:tr-1");
  });

  it("AlertsList_OverflowFixture_PutsTheLongestStringsInLinesThatWrap", async () => {
    given([
      restartAlert(
        { seen: true, summary: LONG_REASON },
        { capability: { verdict: "CanRestart", reason: LONG_REASON, guardedRestart: "Available", guardedRestartReason: LONG_REASON, launcherVersion: LONG_VERSION } },
      ),
      secretAlert({ needsAnswer: false, status: "Denied", seen: true, title: LONG_REASON }),
    ]);
    expect(LONG_VERSION).toHaveLength(46);
    expect(LONG_REASON).toHaveLength(300);
    renderList();
    const card = await screen.findByTestId("alert-card");
    // The card holds the 300-character reason in full - never ellipsised.
    expect(card.querySelector(".alert-summary")?.textContent).toBe(LONG_REASON);
    const lines = [...card.querySelectorAll(".alert-line")];
    expect(lines.find((l) => l.textContent?.includes(LONG_VERSION))).toBeTruthy();
    expect(lines.filter((l) => l.textContent?.includes(LONG_REASON)).length).toBeGreaterThanOrEqual(2);
    fireEvent.click(screen.getByRole("button", { name: "Earlier (1)" }));
    expect(screen.getByTestId("alerts-row").querySelector(".alerts-row-title")?.textContent).toBe(LONG_REASON);
    // Every class that holds text must wrap anywhere and be allowed to shrink, and none may cut text off. jsdom does no
    // layout, so the rule itself is what is checked here; the screenshots at 360 px and 280 px are the QA run's.
    for (const cls of ["alert-line", "alerts-row-title", "alert-detail-title", "alert-status", "alerts-tray-heading", "alerts-section-heading", "alerts-section-toggle", "alert-details-summary", "alerts-row-time"]) {
      const rule = cssRule(cls);
      expect(rule, `.${cls} must wrap anywhere`).toMatch(/overflow-wrap:\s*anywhere/);
      expect(rule, `.${cls} must be allowed to shrink`).toMatch(/min-width:\s*0/);
    }
    const css = readFileSync(join(__dirname, "alerts.css"), "utf8");
    expect(css).not.toMatch(/text-overflow:\s*ellipsis/);
  });
});

describe("the provider", () => {
  // Phase 4 review, observation 1: the refusal describes the alert at the moment it was refused. Once the alert is
  // answered, or leaves the list, the sentence is no longer true and must not sit under an Earlier row.
  describe("a clear's refusal", () => {
    const sentence = "This still needs your answer; it leaves when you answer it or when it expires.";
    const id = "message-request:lr-1";
    let alerts: ReturnType<typeof useAlerts>;
    function Probe() {
      alerts = useAlerts();
      return <span data-testid="refusal">{alerts.clearErrors[id] ?? ""}</span>;
    }
    function refusal() {
      return screen.getByTestId("refusal").textContent;
    }
    async function refusedWhileItNeedsAnAnswer() {
      given([messageAlert({ id, needsAnswer: false, status: "Allowed", seen: true })]);
      render(
        <AlertsProvider>
          <Probe />
        </AlertsProvider>,
      );
      await loaded();
      await waitFor(() => expect(alerts.earlier).toHaveLength(1));
      // Between two reads the request went back to waiting; the X pressed on the Earlier row is refused.
      given([messageAlert({ id, needsAnswer: true, status: "Pending", seen: true })]);
      state.clearStatus = 409;
      state.clearBody = { error: sentence };
      await act(async () => alerts.clear(id));
      expect(refusal()).toBe(sentence);
      expect(alerts.needsYou).toHaveLength(1);
    }

    it("stays while the alert still needs an answer", async () => {
      await refusedWhileItNeedsAnAnswer();
      await act(async () => alerts.refresh());
      expect(refusal()).toBe(sentence);
    });

    it("goes once a read lists the alert as answered", async () => {
      await refusedWhileItNeedsAnAnswer();
      given([messageAlert({ id, needsAnswer: false, status: "Allowed", seen: true })]);
      await act(async () => alerts.refresh());
      expect(alerts.earlier).toHaveLength(1);
      expect(refusal()).toBe("");
    });

    it("goes once the alert leaves the list", async () => {
      await refusedWhileItNeedsAnAnswer();
      given([]);
      await act(async () => alerts.refresh());
      expect(refusal()).toBe("");
    });

    it("a failed clear on an alert that stays in Earlier keeps its sentence", async () => {
      given([messageAlert({ id, needsAnswer: false, status: "Allowed", seen: true })]);
      render(
        <AlertsProvider>
          <Probe />
        </AlertsProvider>,
      );
      await loaded();
      await waitFor(() => expect(alerts.earlier).toHaveLength(1));
      state.clearStatus = 503;
      state.clearBody = { error: "The Gateway is restarting." };
      await act(async () => alerts.clear(id));
      await act(async () => alerts.refresh());
      expect(refusal()).toContain("The Gateway is restarting.");
    });
  });
});

describe("keyboard and focus in the Cockpit's popover", () => {
  it("AlertsTray_Opening_MovesFocusIn_TabStaysInside_EscapeClosesAndReturnsFocusToTheBell", async () => {
    given([restartAlert({ seen: true }), secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderBell();
    await loaded();
    const bell = screen.getByTestId("alerts-bell");
    bell.focus();
    const tray = await openTray();
    await within(tray).findByRole("heading", { name: /Restart the Director on SOREN_NORTH\?/ });
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

  it("AlertsTray_AnswersAndToggles_AreButtons_AndTheCloseButtonReturnsFocusToTheBell", async () => {
    given([restartAlert({ seen: true }), secretAlert({ needsAnswer: false, status: "Approved", seen: true })]);
    renderBell();
    await loaded();
    const tray = await openTray();
    const card = await within(tray).findByTestId("alert-card");
    expect(within(card).getByRole("button", { name: "Accept" })).toBeTruthy();
    expect(within(tray).getByRole("button", { name: "Earlier (1)" })).toBeTruthy();
    fireEvent.click(within(tray).getByRole("button", { name: "Close alerts" }));
    expect(screen.queryByTestId("alerts-tray")).toBeNull();
    expect(document.activeElement).toBe(screen.getByTestId("alerts-bell"));
  });
});

describe("waitingFrom", () => {
  it("lists only the alerts that need an answer and were asked by that session, by kind if asked", async () => {
    given([
      secretAlert({ id: "secret-transfer:a" }, { askedBySessionId: "AAAAAAAA-1111-2222-3333-444444444444" }),
      secretAlert({ id: "secret-transfer:b", needsAnswer: false }, { askedBySessionId: "aaaaaaaa-1111-2222-3333-444444444444" }),
      messageAlert({ id: "message-request:c" }, { requesterSessionId: "aaaaaaaa-1111-2222-3333-444444444444" }),
      restartAlert({ id: "restart-request:d" }),
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
    await waitFor(() => expect(screen.getByTestId("probe").textContent).toBe("secret-transfer:a,message-request:c|secret-transfer:a"));
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
