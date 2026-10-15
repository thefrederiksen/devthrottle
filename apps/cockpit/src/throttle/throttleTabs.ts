// The tab set of the Your Throttle page, kept apart from the view so the list and the stored-value
// validation are plain data the tests can pin.
//
// The validator is DERIVED from TABS rather than repeating the keys. When Repos was folded in from its
// own page as a fourth tab (2026-07-14), a hand-written guard would have been a second place to forget
// to update - and the failure is quiet: the stored tab silently fails validation and the owner is
// dropped back on Overview every time they return to the page.

export type ThrottleTab = "overview" | "activity" | "breakdown" | "repos" | "agents" | "systems";

export const TABS: ReadonlyArray<{ key: ThrottleTab; label: string }> = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity" },
  { key: "breakdown", label: "Breakdown" },
  { key: "repos", label: "Repos" },
  { key: "agents", label: "Agents" },
  { key: "systems", label: "Systems" },
];

/** The tab shown when nothing valid is stored. */
export const DEFAULT_TAB: ThrottleTab = "overview";

/** True when `value` names a tab that exists. Guards whatever is read back out of storage, which is
 *  arbitrary text: it may predate a tab being added or outlive one being removed. */
export function isThrottleTab(value: string): value is ThrottleTab {
  return TABS.some((t) => t.key === value);
}
