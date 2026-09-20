// The one place any web client reads "the Wingman could not read this stop" off a session row (mission
// "Wingman error and retry", 2026-09-19).
//
// WHY THIS FILE EXISTS. On 19 September seven sessions showed "Voice did not arrive. The Gateway is still
// trying" with nothing booked, and a session outside voice mode showed nothing at all. The Gateway now writes
// the retry schedule on the stored reading and folds ONE display (WingmanErrorFold) onto the row: the tag, a
// short plain reason, which retry is next and when, how many remain, and whether the schedule is used up.
//
// THE DIVISION OF LABOUR. The Gateway rules (CLAUDE.md rule 7): whether there is an error, every word of it,
// which retry is next and its absolute time. This file does no ruling. It reads the stamped display and turns
// the absolute time into "in 40 seconds" against the local clock - the same formatting a snooze countdown gets -
// and it composes nothing else. When nothing is booked the Gateway sends its own sentence and no time, so a
// client cannot say an attempt is coming that is not booked.
import type { SessionDto } from "../api/client";

/** The Gateway's finished display for a failed reading. Mirrors CcDirector.Gateway.Contracts.WingmanErrorDisplay. */
export type WingmanErrorDisplay = {
  tag?: string;
  reason?: string;
  nextRetryNumber?: number | string;
  retriesTotal?: number | string;
  retriesRemaining?: number | string;
  nextRetryAtUtc?: string | null;
  exhausted?: boolean;
  retryLabel?: string;
  exhaustedText?: string;
  askAgainLabel?: string;
};

// The generated schema.ts does not carry this field yet, so it is augmented here - the same way the delivery
// fields are in delivery.ts.
type SessionWithWingmanError = SessionDto & { wingmanError?: WingmanErrorDisplay | null };

/** The Gateway's Wingman error for this row, or null when it stamped none. Null on an older Gateway too. */
export function wingmanErrorOf(session: SessionDto): WingmanErrorDisplay | null {
  const error = (session as SessionWithWingmanError).wingmanError;
  if (error === null || error === undefined) return null;
  if (typeof error.tag !== "string" || error.tag.length === 0) return null;
  return error;
}

/** "40 seconds", "1 minute", "12 minutes". Whole units, rounded up, so the line never reads "in 0 seconds". */
export function waitWords(milliseconds: number): string {
  const seconds = Math.ceil(milliseconds / 1000);
  if (seconds < 90) return seconds === 1 ? "1 second" : `${seconds} seconds`;
  const minutes = Math.ceil(seconds / 60);
  return `${minutes} minutes`;
}

/**
 * The retry line under the tag.
 *
 *   booked, in the future:  "retry 2 of 8 in 40 seconds"
 *   booked, time reached:   "retry 2 of 8 is due now"   (the sweep that carries it comes past every 45 seconds)
 *   nothing booked:         the Gateway's own sentence, verbatim
 *
 * A booked time this client cannot parse is shown as the bare label: it still says which retry is booked and
 * claims no time it does not know.
 */
export function wingmanRetryLine(error: WingmanErrorDisplay, nowMs: number): string {
  const exhausted = error.exhausted === true || error.nextRetryAtUtc === null || error.nextRetryAtUtc === undefined;
  if (exhausted) return error.exhaustedText ?? "";
  const label = error.retryLabel ?? "";
  const at = Date.parse(String(error.nextRetryAtUtc));
  if (!Number.isFinite(at)) return label;
  const left = at - nowMs;
  if (left <= 0) return `${label} is due now`;
  return `${label} in ${waitWords(left)}`;
}
