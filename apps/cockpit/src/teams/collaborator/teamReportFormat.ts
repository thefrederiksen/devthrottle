// The one way the team's Reports page writes a date: "2 Oct", as the mockup draws it (S10), in this browser's own
// time zone. Layout only - the Gateway decides what is shown, this decides how a time reads.

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** "2026-10-02T09:00:00Z" -> "2 Oct". A value that is not a date is shown as it came. */
export function shortDate(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : `${date.getDate()} ${MONTHS[date.getMonth()]}`;
}

/** "2 Oct, 14:05" - a comment's time, where the hour matters. */
export function dateAndTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const hh = String(date.getHours()).padStart(2, "0");
  const mm = String(date.getMinutes()).padStart(2, "0");
  return `${shortDate(iso)}, ${hh}:${mm}`;
}

/** How often the team's Reports page reads the Gateway again while it is on screen. There is no push for reports. */
export const TEAM_REPORTS_POLL_MS = 5000;
