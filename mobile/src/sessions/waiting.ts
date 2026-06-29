// Waiting-time formatting + a live ticking clock for the roster's "needs you" cards (issue #844).
// A faithful port of the desktop SessionRail Waiting() ladder
// (src/CcDirector.Cockpit/Components/SessionRail.razor), adapted for the mobile card: the label
// reads "just now" for a sub-minute wait, otherwise "waiting <dur>" where <dur> climbs the ladder
// minutes -> "12m", hours -> "1h 4m", days -> "2d 3h". Kept as a pure function so it is unit-
// testable and so the ticking hook stays a thin re-render trigger that recomputes from the held
// needsYouSince (no roster refetch just to tick - issue #844 scope).
import { useEffect, useState } from "react";

// Compact elapsed-waiting label from a needs-you session's needsYouSince UTC stamp.
//   sinceIso - the ISO 8601 needsYouSince value (UTC) from the /sessions payload.
//   now      - the current epoch milliseconds (passed in so the caller's ticking clock drives it).
// Returns "" when sinceIso is empty or unparseable so the caller renders nothing.
export function waitingLabel(sinceIso: string, now: number): string {
  const trimmed = (sinceIso ?? "").trim();
  if (trimmed.length === 0) return "";
  const since = Date.parse(trimmed);
  if (Number.isNaN(since)) return "";

  let ms = now - since;
  if (ms < 0) ms = 0;

  const totalMinutes = Math.floor(ms / 60000);
  if (totalMinutes < 1) return "just now";

  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;

  if (days >= 1) return `waiting ${days}d ${hours}h`;
  if (hours >= 1) return `waiting ${hours}h ${minutes}m`;
  return `waiting ${minutes}m`;
}

// A re-render trigger that updates on a fixed interval, returning the current epoch milliseconds.
// Used by the needs-you cards so their waiting label ticks up live without refetching the roster.
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(timer);
  }, [intervalMs]);
  return now;
}
