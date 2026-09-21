// The work-history surface of the Gateway (issue #2194): the typed, same-origin client the History
// page reads. The Gateway keeps one durable record per session, written WHILE the session runs, and
// serves it grouped by repository and day at GET /history/report - the same endpoint the daily
// report and the brain consume, so the page is one reader of a shared spine, not a private one.
//
// THE CLIENT IS DUMB (rule 7): every ending label, tone and description line on these records was
// folded once on the Gateway. This module and the page render them verbatim and never re-derive
// what a state means.
import { authHeaders, GatewayError } from "../api/client";

/** One session's history record - camelCase mirror of the C# WorkHistorySessionDto. */
export interface WorkHistorySession {
  sessionId: string;
  sessionNumber?: number | null;
  sessionName?: string | null;
  machineName?: string | null;
  directorId?: string | null;
  repoPath?: string | null;
  repoName?: string | null;
  agentKind?: string | null;
  model?: string | null;
  missionName?: string | null;
  missionId?: string | null;
  sessionRole?: string | null;
  /**
   * Who asked for this session (internal#982): "human" | "agent" | "schedule" | "unknown".
   * NULL means the row predates the field - which is NOT the same as "unknown", the answer for a
   * create path that was asked and had nothing to say. Never render either as "human".
   */
  originKind?: string | null;
  /** Where the create call came from: "desktop" | "cockpit" | "phone" | "cli" | "cron" |
   * "workflow" | "trigger" | "api" | "unknown". Null on rows that predate the field. */
  originSurface?: string | null;
  /** The session that ASKED for this one, or null. Keys the same table, so it resolves against
   * other records in the report - and may name a row retention has already pruned. */
  parentSessionId?: string | null;
  startedAtUtc: string;
  lastActivityUtc?: string | null;
  lastSeenUtc: string;
  /** null while the session runs; "closed" | "finished" | "director-stopped" | "interrupted". */
  endingKind?: string | null;
  /** Gateway-folded wording; render verbatim. */
  endingLabel?: string | null;
  /** Gateway-folded display tone: "live" | "ok" | "neutral" | "attention". */
  endingTone: string;
  endedAtUtc?: string | null;
  /** Gateway-folded one-liner; never empty. */
  descriptionLine: string;
  turnCount?: number | null;
  /** Completed agent turns (one flip to waiting-for-input equals one turn). Null when never reported. */
  agentTurnCount?: number | null;
  /** Total seconds spent waiting on the user, closed stretches only. Null when never reported. */
  idleSeconds?: number | null;
  /** How many times the session started waiting on the user (internal#982) - the matched pair to
   * idleSeconds, and not derivable from it. Null when never reported. */
  waitingStretchCount?: number | null;
  /** Character volume of input submitted, operator plus agent-driven. Null when never reported. */
  inputCharacterCount?: number | null;
  /** Cumulative token spend, kept per kind because they are priced differently. Null when the
   * agent's driver reports no usage. */
  inputTokens?: number | null;
  outputTokens?: number | null;
  cacheReadTokens?: number | null;
  cacheCreationTokens?: number | null;
  /** The fullest the context window was observed to be. A PEAK, never a sum - occupancy is a gauge
   * that drops on compaction. Null when the driver reports no reading. */
  peakContextTokens?: number | null;
  /** null until a summary exists; "sealed" | "generated" | "none" | "unavailable". */
  summaryKind?: string | null;
  summaryIsPartial: boolean;
  summaryText?: string | null;
  whatWasBuilt?: string[] | null;
  leftUnverified?: string[] | null;
  branches?: string[] | null;
  pullRequests?: string[] | null;
  commits?: string[] | null;
}

/** One repository group's one day. */
export interface WorkHistoryDay {
  /** UTC day, yyyy-MM-dd. */
  day: string;
  /** The cached roll-up paragraph, when the background pass has written it. */
  summaryText?: string | null;
  /** True while the roll-up has not been written (or is being refreshed) - say so, never invent. */
  summaryPending: boolean;
  sessions: WorkHistorySession[];
}

export interface WorkHistoryRepo {
  repoKey: string;
  displayName: string;
  /** Newest day first. */
  days: WorkHistoryDay[];
}

export interface WorkHistoryReport {
  fromDay: string;
  toDay: string;
  /** Most recently active repository first. */
  repos: WorkHistoryRepo[];
}

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string };
        detail = body.error ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

/**
 * GET /history/report - what was worked on over the inclusive UTC day range, grouped by repository
 * and day. Throws on failure so the page surfaces it (no fallback to a misleading empty history).
 */
export async function getWorkHistoryReport(
  fromDay: string,
  toDay: string,
  signal?: AbortSignal,
): Promise<WorkHistoryReport> {
  const query = new URLSearchParams({ from: fromDay, to: toDay });
  const res = await fetch(`/history/report?${query.toString()}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /history/report");
  return (await res.json()) as WorkHistoryReport;
}
