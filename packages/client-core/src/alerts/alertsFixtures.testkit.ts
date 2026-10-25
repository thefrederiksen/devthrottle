// TEST SUPPORT ONLY - never imported by product code. Records shaped as the Gateway serialises them, for the Alerts
// tests, including the overflow fixture: the longest real strings the tray must hold without growing wider.
import type { DirectorRestartRequest } from "../restart/restartRequests";
import type { SecretTransfer } from "../secrets/secretTransfersClient";
import type { MessageLinkRequest } from "../fleet/messageLinksClient";
import type { AlertDto } from "./alertsClient";

/** A 46-character launcher version with no break point - the string that pushed the old restart card off the phone. */
export const LONG_VERSION = "2.31.0-preview.20261024.214130+sha.3f9c0a1b7e2";

/** A 300-character reason. */
export const LONG_REASON =
  "the launcher on mac-mini has a staged update and the Mac Tester cannot run the v2.31.0 release checks until the Director restarts onto it, because the checks need the new terminal input commands, the new alert routes and the new capability check, and every one of them ships only in the build that is staged now. ".repeat(2).slice(0, 300);

export function restartRequest(overrides: Partial<DirectorRestartRequest> = {}): DirectorRestartRequest {
  return {
    id: "req-1",
    machine: "SOREN_NORTH",
    directorId: "d-1",
    directorName: "DevThrottle_1",
    requestedBySessionId: "11111111-2222-3333-4444-555555555555",
    requestedBySessionName: "Rig Alpha - Architect (11111111)",
    reason: "the launcher has a staged update",
    requestedAtUtc: "2026-09-07T12:00:00Z",
    expiresAtUtc: "2026-09-07T12:30:00Z",
    state: "Pending",
    stateReason: "",
    progress: "",
    liveSessionCount: 7,
    liveSessionsSentence: "That Director holds 7 live sessions right now; each will be asked to write a handover and close, leaf-first.",
    capability: {
      verdict: "CanRestart",
      reason: "SOREN_NORTH can be restarted: its launcher (version 2.1.0) holds a live command stream and declares director/restart.",
      guardedRestart: "Available",
      guardedRestartReason: "the launcher on SOREN_NORTH (version 2.1.0) declares director/restart:only-if-empty, so it will refuse a restart while its Director still holds live sessions and say how many.",
      launcherVersion: "2.1.0",
    },
    title: "Restart the Director on SOREN_NORTH?",
    askedBySentence: "Rig Alpha - Architect (11111111) asks: the launcher has a staged update",
    acceptSentence: "Accept once, and nothing more is asked of you: the Director asks its 7 live sessions to write a handover and close, records them in a workspace, checks the machine again, asks its launcher for a restart only if it is empty, and reports here.",
    canAccept: true,
    ...overrides,
  };
}

export function secretTransfer(overrides: Partial<SecretTransfer> = {}): SecretTransfer {
  return {
    transferId: "tr-1",
    entry: "vercel-bypass",
    targetName: "vercel-bypass",
    fromMachine: "SOREN_NORTH",
    toMachine: "devthrottle-mac-mini",
    replace: true,
    askedBySessionId: "aaaaaaaa-1111-2222-3333-444444444444",
    askedBy: 'Session 042 "Rig - Developer"',
    reason: "The preview deploy needs it",
    state: "waiting",
    createdAtUtc: "2026-10-10T11:58:00Z",
    expiresAtUtc: "2026-10-10T12:13:00Z",
    summary: "vercel-bypass from SOREN_NORTH to devthrottle-mac-mini",
    replaceNote: "Replaces the entry already on devthrottle-mac-mini.",
    statusText: 'Waiting for your answer. Asked by Session 042 "Rig - Developer".',
    canAnswer: true,
    ...overrides,
  };
}

export function messageRequest(overrides: Partial<MessageLinkRequest> = {}): MessageLinkRequest {
  return {
    requestId: "lr-1",
    requesterSessionId: "bbbbbbbb-0000-0000-0000-000000000121",
    targetSessionId: "cccccccc-0000-0000-0000-000000000125",
    requesterLabel: "121 Fix the Release - Manager",
    targetLabel: "125 Release - Release Manager",
    reason: "I need to hand the merge over",
    requestedAmount: "once-with-reply",
    status: "pending",
    askedAtUtc: "2026-10-23T10:00:00Z",
    ...overrides,
  };
}

type Common = Omit<AlertDto, "kind" | "detail">;

function common(id: string, overrides: Partial<Common>): Common {
  return {
    id,
    needsAnswer: true,
    title: "An alert",
    summary: "Someone asked for something.",
    status: "Waiting",
    createdAtUtc: "2026-10-24T21:40:00Z",
    updatedAtUtc: "2026-10-24T21:41:00Z",
    expiresAtUtc: null,
    seen: false,
    ...overrides,
  };
}

export function restartAlert(overrides: Partial<Common> = {}, detail: Partial<DirectorRestartRequest> = {}): AlertDto {
  return {
    ...common("restart:req-1", { title: "Restart the Director on SOREN_NORTH?", summary: "Rig Alpha asks: the launcher has a staged update.", status: "Pending", expiresAtUtc: "2026-10-24T22:11:00Z", ...overrides }),
    kind: "restart-request",
    detail: restartRequest(detail),
  };
}

export function secretAlert(overrides: Partial<Common> = {}, detail: Partial<SecretTransfer> = {}): AlertDto {
  return {
    ...common("secret:tr-1", { title: "Move vercel-bypass to devthrottle-mac-mini?", summary: "Session 042 asks: the preview deploy needs it.", ...overrides }),
    kind: "secret-transfer",
    detail: secretTransfer(detail),
  };
}

export function messageAlert(overrides: Partial<Common> = {}, detail: Partial<MessageLinkRequest> = {}): AlertDto {
  return {
    ...common("message:lr-1", { title: "Let 121 message 125?", summary: "121 asks: I need to hand the merge over.", ...overrides }),
    kind: "message-request",
    detail: messageRequest(detail),
  };
}
