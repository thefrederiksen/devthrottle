// The dev report OWNER routes (src/CcDirector.Gateway/Api/DevReportEndpoints.cs). These four, and only
// these, are what the Cockpit and the phone call. Every word a report, an item or a reply shows - status,
// statusLabel, the reply text - is the Gateway's and is typed here as the plain string it sends; nothing in
// this client decides what a state means (repository rule 7).
//
// A report the Gateway answers 404 for simply does not appear: the reads return null for it rather than
// throwing, so a screen has nothing to render instead of an error to explain.

import { authHeaders, GatewayError, gatewayFetch, POLL_TIMEOUT_MS } from "../api/client";
import type { DevReportItem } from "./protocol";

export interface DevReportSummary {
  id: string;
  sessionId: string;
  key: string;
  title: string;
  status: string;
  version: number;
  publishedAtUtc: string;
  updatedAtUtc: string;
  sessionEnded: boolean;
  openItems: number;
  /**
   * The session this report came from, in the owner's words - e.g. `121 devthrottle - tool not working on
   * linux`. The Gateway composes it; every view renders it verbatim (repository rule 7). A Gateway too old
   * to send it leaves it undefined, and a view then shows nothing rather than composing one of its own.
   */
  sessionLabel?: string;
  /** The way back to that session, in the owner's words - e.g. `back to 121 devthrottle - tool not working on linux`. Same rule: verbatim, or nothing. */
  backLabel?: string;
}

/** An item as the Gateway recorded it: the contract's item shape plus the Gateway's state and times. */
export type DevReportRecordedItem = DevReportItem & {
  status: string;
  statusLabel: string;
  sentAtUtc: string | null;
  deliveredAtUtc: string | null;
};

export interface DevReportReply {
  id: string;
  text: string;
  at: string;
}

export interface DevReportDetail {
  report: DevReportSummary;
  items: DevReportRecordedItem[];
  replies: DevReportReply[];
}

export interface DevReportHtml {
  html: string;
  version: number;
}

export interface DevReportSendUpdate {
  id: string;
  status: string;
  statusLabel: string;
}

function reportPath(reportId: string): string {
  return `/dev-reports/${encodeURIComponent(reportId)}`;
}

/** GET /dev-reports?sessionId= - one session's reports. */
export async function listDevReports(sessionId: string, signal?: AbortSignal): Promise<DevReportSummary[]> {
  const res = await gatewayFetch(
    `/dev-reports?sessionId=${encodeURIComponent(sessionId)}`,
    { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (!res.ok) throw await GatewayError.from(res, "load the reports");
  const body = (await res.json()) as { reports: DevReportSummary[] };
  return body.reports;
}

/** GET /dev-reports/{id} - the report with its items and replies, or null when the Gateway has no such report. */
export async function getDevReport(reportId: string, signal?: AbortSignal): Promise<DevReportDetail | null> {
  const res = await gatewayFetch(
    reportPath(reportId),
    { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (res.status === 404) return null;
  if (!res.ok) throw await GatewayError.from(res, "load the report");
  return (await res.json()) as DevReportDetail;
}

/** GET /dev-reports/{id}/html?version= - the report's bytes as text, and the version the Gateway served. */
export async function getDevReportHtml(reportId: string, version: number, signal?: AbortSignal): Promise<DevReportHtml | null> {
  const res = await gatewayFetch(`${reportPath(reportId)}/html?version=${encodeURIComponent(String(version))}`, {
    method: "GET",
    headers: { Accept: "text/plain", ...authHeaders() },
    signal,
  });
  if (res.status === 404) return null;
  if (!res.ok) throw await GatewayError.from(res, "load the report page");
  const served = Number(res.headers.get("X-Dev-Report-Version"));
  if (!Number.isInteger(served) || served < 1) {
    throw new Error(`The Gateway served report ${reportId} without a valid X-Dev-Report-Version header.`);
  }
  return { html: await res.text(), version: served };
}

/** POST /dev-reports/{id}/send - the owner's notes and answers. Returns the Gateway's word on each item. */
export async function sendDevReportItems(reportId: string, items: DevReportItem[], signal?: AbortSignal): Promise<DevReportSendUpdate[]> {
  const res = await gatewayFetch(`${reportPath(reportId)}/send`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ items }),
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "send the notes");
  const body = (await res.json()) as { updates: DevReportSendUpdate[] };
  return body.updates;
}
