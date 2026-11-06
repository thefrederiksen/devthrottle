// A time on the factory inbox screens, in the reader's own clock: "09:07" today, "4 Nov 09:07" on another day. The
// Gateway hands the moment (UTC); which clock it is read on is the reader's, so this is the one thing about a time the
// screen decides.
export function formatInboxTime(isoUtc: string, now: Date = new Date()): string {
  const at = new Date(isoUtc);
  if (Number.isNaN(at.getTime())) return isoUtc;
  const clock = `${pad(at.getHours())}:${pad(at.getMinutes())}`;
  const sameDay = at.getFullYear() === now.getFullYear() && at.getMonth() === now.getMonth() && at.getDate() === now.getDate();
  if (sameDay) return clock;
  return `${at.getDate()} ${MONTHS[at.getMonth()]} ${clock}`;
}

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

function pad(n: number): string {
  return String(n).padStart(2, "0");
}
