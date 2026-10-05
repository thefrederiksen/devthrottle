// A dev report sent to a member of the team (devthrottle_internal#2309): the routes under /teams/{teamId}/reports, as
// the Cockpit's Reports page reads them. Every word and flag a screen shows - who it is from, "New" or "Read", where a
// comment goes, who it can still be sent to - is the Gateway's and is typed here as the plain value it sends; nothing
// in this client decides what a state means (repository rule 7).
//
// Two halves:
//   - SENT TO ME: the reports someone sent to this person, one of them, its bytes, "I opened it", and a comment. A
//     comment goes to the report's author person and never to an agent - the Gateway keeps it apart from the agent's
//     conversation entirely.
//   - MINE: this person's own reports in the team, who each went to, sending one to members, and the comments people
//     wrote on it.
//
// A report the Gateway answers 404 for does not appear: the single-report reads return null for it rather than
// throwing, so a screen has nothing to render instead of an error to explain.

import { authHeaders, GatewayError, gatewayFetch, POLL_TIMEOUT_MS } from "../api/client";
import type { DevReportApi } from "../devreports/controller";
import type { DevReportDetail, DevReportHtml, DevReportSendUpdate } from "../devreports/devReportsClient";
import { call } from "./invitationsClient";

/** One report on the recipient's list. */
export interface ReceivedReport {
  id: string;
  title: string;
  status: string;
  /** The version this person was sent - the only one they read. A later one reaches them only when it is sent again. */
  version: number;
  /** Who sent it, as the Team page names them. */
  from: string;
  sentAtUtc: string;
  read: boolean;
  /** "New" or "Read", in the Gateway's words. */
  readLabel: string;
  /** How many of its questions wait on this person and where to answer them, or null when none do (#2307). */
  questionsLabel: string | null;
}

export interface ReceivedReports {
  count: number;
  reports: ReceivedReport[];
  /** What the page says when nothing was sent. */
  emptyText: string;
  /** Whether this person runs sessions in the team, and so has reports of their own to show beside these. */
  showYourReports: boolean;
}

export interface ReportComment {
  id: string;
  text: string;
  atUtc: string;
  /** For a comment written with an answer on the Questions page: which question, and what was chosen (#2307). */
  aboutLabel: string | null;
}

export interface ReceivedReportDetail {
  report: ReceivedReport;
  /** The comments this person wrote on it, oldest first. */
  comments: ReportComment[];
  /** Whether a comment can be sent: false when the author can no longer read comments here. */
  canComment: boolean;
  /** Where a comment goes, or why none can be sent, in the Gateway's words - never an agent. */
  commentsNote: string;
  /** Whether the report's own notes and answers are offered. The Gateway's answer; false for every team reader. */
  notesOpen: boolean;
  /** Whether the answer controls in the report's own markup may be used. The Gateway's answer; false today. */
  answersOpen: boolean;
}

/** One of this person's own reports on their list. */
export interface OwnReport {
  id: string;
  title: string;
  status: string;
  version: number;
  updatedAtUtc: string;
  /** "Not sent to anyone yet", "Sent to 2 people". */
  sentToLabel: string;
  /** "1 comment", or null when there are none. */
  commentsLabel: string | null;
}

export interface OwnReports {
  count: number;
  reports: OwnReport[];
  emptyText: string;
}

export interface ReportRecipient {
  memberId: string;
  name: string;
  sentAtUtc: string;
  /** The version they hold. */
  sentVersion: number;
  /** "Has version 1 of 3" when they hold an older version; null when they hold the current one. */
  versionLabel: string | null;
  read: boolean;
  /** "Read" or "Not read yet". */
  readLabel: string;
}

export interface ReportRecipientChoice {
  memberId: string;
  name: string;
  role: string;
  /** Set when they already hold an older version: what sending does for them. */
  heldLabel: string | null;
}

export interface CommentFromPerson {
  id: string;
  from: string;
  text: string;
  atUtc: string;
  /** For a comment written with an answer on the Questions page: which question, and what was chosen (#2307). */
  aboutLabel: string | null;
}

export interface OwnReportDetail {
  report: { id: string; title: string; status: string; version: number; publishedAtUtc: string; updatedAtUtc: string };
  recipients: ReportRecipient[];
  /** What the page says when it was sent to nobody. */
  recipientsEmptyText: string;
  /** The members it can be sent to now: not sent it yet, or holding an older version. */
  choices: ReportRecipientChoice[];
  sendNote: string;
  comments: CommentFromPerson[];
  commentsEmptyText: string;
  /** Whether the report's own notes and answers are offered. The Gateway's answer; false for every team reader. */
  notesOpen: boolean;
  /** Whether the answer controls in the report's own markup may be used. The Gateway's answer; false today. */
  answersOpen: boolean;
}

const base = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/reports`;
const sentToMe = (teamId: string, reportId?: string) =>
  `${base(teamId)}/sent-to-me${reportId === undefined ? "" : `/${encodeURIComponent(reportId)}`}`;
const mine = (teamId: string, reportId?: string) =>
  `${base(teamId)}/mine${reportId === undefined ? "" : `/${encodeURIComponent(reportId)}`}`;

/** A read that answers null on 404 and throws on every other failure. */
async function readOrNull<T>(path: string, what: string, signal?: AbortSignal): Promise<T | null> {
  const res = await gatewayFetch(
    path,
    { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal },
    { timeoutMs: POLL_TIMEOUT_MS },
  );
  if (res.status === 404) return null;
  if (!res.ok) throw await GatewayError.from(res, what);
  return (await res.json()) as T;
}

/** The report's bytes and the version served, or null. The same headers the owner's route answers with. */
async function readHtml(path: string, reportId: string, version: number, signal?: AbortSignal): Promise<DevReportHtml | null> {
  const res = await gatewayFetch(`${path}/html?version=${encodeURIComponent(String(version))}`, {
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

// ---------------------------------------------------------------- sent to me

/** GET /teams/{teamId}/reports/sent-to-me - the reports sent to this person, newest first. */
export function getReportsSentToMe(teamId: string, signal?: AbortSignal): Promise<ReceivedReports> {
  return call<ReceivedReports>("GET", sentToMe(teamId), "load the reports sent to you", undefined, signal);
}

/** GET /teams/{teamId}/reports/sent-to-me/{id} - one, with this person's own comments; null when it was not sent to them. */
export function getReportSentToMe(teamId: string, reportId: string, signal?: AbortSignal): Promise<ReceivedReportDetail | null> {
  return readOrNull<ReceivedReportDetail>(sentToMe(teamId, reportId), "load the report", signal);
}

/** POST /teams/{teamId}/reports/sent-to-me/{id}/read - this person opened the version named. The Gateway marks it
 *  only when it is the version they hold, and answers 409 with its own sentence otherwise. */
export async function markReportRead(teamId: string, reportId: string, version: number, signal?: AbortSignal): Promise<void> {
  await call<{ read: boolean }>("POST", `${sentToMe(teamId, reportId)}/read`, "mark the report read", { version }, signal);
}

/** POST /teams/{teamId}/reports/sent-to-me/{id}/comments - a comment, for the report's author person. */
export async function commentOnReport(teamId: string, reportId: string, text: string, signal?: AbortSignal): Promise<ReportComment> {
  const body = await call<{ comment: ReportComment }>("POST", `${sentToMe(teamId, reportId)}/comments`, "send the comment", { text }, signal);
  return body.comment;
}

// ---------------------------------------------------------------- mine

/** GET /teams/{teamId}/reports/mine - this person's own reports in the team. */
export function getMyTeamReports(teamId: string, signal?: AbortSignal): Promise<OwnReports> {
  return call<OwnReports>("GET", mine(teamId), "load your reports", undefined, signal);
}

/** GET /teams/{teamId}/reports/mine/{id} - one of them, with who it went to and the comments; null when it is not theirs. */
export function getMyTeamReport(teamId: string, reportId: string, signal?: AbortSignal): Promise<OwnReportDetail | null> {
  return readOrNull<OwnReportDetail>(mine(teamId, reportId), "load the report", signal);
}

/** POST /teams/{teamId}/reports/mine/{id}/recipients - send the version the page was showing to members, by their Team
 *  page ids. The Gateway sends exactly that version, or answers 409 with its own sentence when it is no longer the
 *  newest. */
export function sendMyTeamReport(
  teamId: string,
  reportId: string,
  memberIds: string[],
  version: number,
  signal?: AbortSignal,
): Promise<OwnReportDetail> {
  return call<OwnReportDetail>("POST", `${mine(teamId, reportId)}/recipients`, "send the report", { memberIds, version }, signal);
}

// ---------------------------------------------------------------- the viewer

/** The viewer's record for a report read here: the Gateway's title, status and version, and no session - a report
 *  shared in a team never shows which session wrote it, or the agent's conversation. */
function asDetail(report: { id: string; title: string; status: string; version: number }, publishedAtUtc = "", updatedAtUtc = ""): DevReportDetail {
  return {
    report: {
      id: report.id,
      sessionId: "",
      key: "",
      title: report.title,
      status: report.status,
      version: report.version,
      publishedAtUtc,
      updatedAtUtc,
      sessionEnded: false,
      openItems: 0,
    },
    items: [],
    replies: [],
  };
}

/** A team reader's page carries no notes (the Gateway's notesOpen is false, so the viewer loads no notes script), so
 *  nothing can be queued to send. Reaching this is a defect, and says so. */
function noNotes(): Promise<DevReportSendUpdate[]> {
  return Promise.reject(new Error("This report's notes are off for a team reader, so there is nothing to send."));
}

/** The viewer's Gateway calls for a report sent to this person: the same viewer, the same frame host, the bytes of the
 *  version they were sent from the recipient's own route. The page carries no notes, so nothing is ever sent. */
export function receivedReportApi(teamId: string): DevReportApi {
  return {
    getDetail: async (reportId, signal) => {
      const detail = await getReportSentToMe(teamId, reportId, signal);
      return detail === null ? null : asDetail(detail.report, detail.report.sentAtUtc, detail.report.sentAtUtc);
    },
    getHtml: (reportId, version, signal) => readHtml(sentToMe(teamId, reportId), reportId, version, signal),
    send: () => noNotes(),
  };
}

/** The viewer's Gateway calls for one of this person's own reports in the team. */
export function ownTeamReportApi(teamId: string): DevReportApi {
  return {
    getDetail: async (reportId, signal) => {
      const detail = await getMyTeamReport(teamId, reportId, signal);
      return detail === null ? null : asDetail(detail.report, detail.report.publishedAtUtc, detail.report.updatedAtUtc);
    },
    getHtml: (reportId, version, signal) => readHtml(mine(teamId, reportId), reportId, version, signal),
    send: () => noNotes(),
  };
}
