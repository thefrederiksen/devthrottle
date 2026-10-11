// The "Saved, but:" line under a schedule save (Factory Control, step 3). Kept apart from scheduleClient so a screen
// whose tests replace the network client still has it.
import type { CronJob } from "./scheduleClient";

/**
 * The warnings on a saved schedule, as one line for the "Saved, but:" banner: the Gateway's load warning and its
 * shift-end warning (Factory Control, step 3), each verbatim, in that order. Null when the Gateway gave neither.
 */
export function savedWarnings(saved: Pick<CronJob, "loadWarning" | "shiftWarning">): string | null {
  const parts = [saved.loadWarning, saved.shiftWarning].filter((w): w is string => typeof w === "string" && w.length > 0);
  return parts.length === 0 ? null : parts.join(" ");
}
