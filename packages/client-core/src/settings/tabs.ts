// The Settings tab set, shared by BOTH shells - the desktop Cockpit and the mobile app.
//
// It lives in client-core rather than in either app because the two surfaces must offer the SAME
// settings. They did not: the Cockpit had Notifications / AI / Car Mode with the microphone and
// transcription checks on a different page entirely, while the phone had a single untabbed "AI
// settings" scroll with no notification settings and none of the Car Mode settings at all. Two lists in
// two files drift apart by default; one list in one file cannot.
//
// Pure logic, no DOM, so the routing rules stay unit-testable (the repo's test convention).

/** Which shell is asking. Every caller states it - see visibleTabs. */
export type Surface = "cockpit" | "mobile";

export type TabId =
  | "account"
  | "usage"
  | "notifications"
  | "ai"
  | "language"
  | "transcription"
  | "dictionary"
  | "fleetmanager"
  | "injectedtext"
  | "devices"
  | "members"
  | "teamplan";

/**
 * The small heading a tab sits under where a surface lays its tabs out down the side (the Cockpit, 8 Oct 2026).
 * "team" is the team on screen: its heading is that team's NAME, which only the shell knows, so the shell
 * supplies it. A surface that lays the tabs out in one strip (the phone) does not draw the headings at all.
 */
export type TabGroup = "you" | "voice" | "fleet" | "team";

/** The fixed headings. The team's heading is its name, supplied by the shell (see TabGroup). */
export const GROUP_LABELS: Readonly<Record<Exclude<TabGroup, "team">, string>> = {
  you: "You",
  voice: "Voice",
  fleet: "Fleet",
};

interface TabDef {
  id: TabId;
  label: string;
  group: TabGroup;
  /**
   * Where this tab appears. "all" is the default and the rule; "cockpit" is the documented exception.
   *
   * A tab is "cockpit" ONLY when the phone genuinely cannot do the job, not merely because the desktop
   * got there first - the standing rule is that Settings stays in sync and the desktop may go DEEPER,
   * never that it holds settings the phone silently lacks. Injected text qualifies: it is one
   * fleet-wide, Gateway-owned block of text, configured once at a desk, and editing it means working in
   * a wide monospace editor that has no honest phone form. Adding a second entry here should feel
   * uncomfortable and needs the same kind of reason.
   */
  surface: "all" | "cockpit";
  /**
   * Not offered. The tab is out of the strip, and ?tab= does not resolve to it either - it is simply not
   * one of this surface's tabs any more, and an old link to it lands on the default like any other id
   * that is no longer a tab.
   *
   * Then why keep the row at all, rather than deleting it? Because this is meant to be REVERSIBLE by one
   * word. The row keeps the tab's identity, its label and its place in the order, and the panel behind it
   * is still in the codebase and still built - see SettingsTabs. Deleting the row and its component would
   * be a different, larger decision, and undoing it would be a rewrite rather than an edit.
   *
   * The AI tab is the reason this exists. It showed hosting model identities straight to customers, which
   * contradicts a rule the hosting layer already enforces one level down - the real provider is replaced
   * with "devthrottle" for every caller who is not an admin. Most of the tab is inert on the hosted
   * Gateway anyway: it says so itself, in its own words, because the live model catalog is refused there.
   *
   * Hiding a control must never quietly reset what it holds. It does not here: the models this tab used
   * to set live on the Gateway, per account, are written only when somebody chooses one, and are read by
   * the product wherever the wingman runs. Nothing in that path goes through this file.
   */
  hidden?: true;
}

/** What else, besides the surface, decides which tabs are offered. */
export interface TabContext {
  /**
   * True while a team is on screen in a shell that shows the whole app (not a Collaborator's pages-only
   * app). The team tabs are offered only then - with the person's own account on screen there is no team
   * to show, so the whole group is absent rather than empty.
   */
  team: boolean;
  /**
   * True when the Gateway's Team page answer for the team on screen carries a bill - its verdict that this
   * person's role may see the team's plan (rule 7). The client never works that out from the role's name.
   */
  teamPlan: boolean;
}

const NO_TEAM: TabContext = { team: false, teamPlan: false };

// The full ordered set. Grouped the way the Cockpit lays them out down its left side (owner, 8 Oct 2026,
// following how ChatGPT and Claude arrange Settings): what is about YOU first, then how the fleet hears you,
// then the fleet itself, then the team on screen. Within a group the order is the order you meet them.
//
// Language takes the place AI held (issue #1010). The AI row is still here and still hidden - see the `hidden`
// note above; the two are separate decisions that happen to concern the same slot.
//
// The Fleet Manager tab holds where the Fleet Manager runs, and the Wingman's turn verdict switches it is built
// on. It took the place of the Assistant tab, which had itself been the Car Mode tab: Car Mode was removed from
// the product (#1028), and the Assistant was removed by the Fleet Manager mission (step 9), taking with it the
// one setting that tab still held - the model the Assistant's fleet brain thought with. "carmode" and
// "assistant" are retired ids now, like "machine" below.
//
// THE PAGES THAT BECAME TABS (owner, 8 Oct 2026). Account, Plan and usage (Your Throttle), Dictionary, Devices
// and phone, and the team's Members and Team plan were pages of their own in the Cockpit's menu. (Network was a tab
// for a day too; the owner's menu ruling the same day put it back in the menu, under Set up.) They
// are tabs of this one Settings page now, and every one of them is "cockpit", for one reason: their content is
// a Cockpit page today, and the phone reaches the same things through its own screens (its Account and Your
// Throttle screens). Bringing the phone's Settings and menu into this same shape is the next piece of work,
// named in the 8 October layout report - not something to do silently here, because a tab added to the phone
// is a change to the phone. Until then each of these is the documented Cockpit-only exception, not drift.
const ALL_TABS: TabDef[] = [
  { id: "account", label: "Account", group: "you", surface: "cockpit" },
  { id: "usage", label: "Plan and usage", group: "you", surface: "cockpit" },
  { id: "notifications", label: "Notifications", group: "you", surface: "all" },
  { id: "ai", label: "AI", group: "you", surface: "all", hidden: true },
  { id: "language", label: "Language", group: "you", surface: "all" },
  { id: "transcription", label: "Transcription", group: "voice", surface: "all" },
  { id: "dictionary", label: "Dictionary", group: "voice", surface: "cockpit" },
  { id: "injectedtext", label: "Injected text", group: "voice", surface: "cockpit" },
  { id: "fleetmanager", label: "Fleet Manager", group: "fleet", surface: "all" },
  { id: "devices", label: "Devices and phone", group: "fleet", surface: "cockpit" },
  { id: "members", label: "Members", group: "team", surface: "cockpit" },
  { id: "teamplan", label: "Team plan", group: "team", surface: "cockpit" },
];

/**
 * The tabs to show on this surface.
 *
 * `surface` is REQUIRED rather than defaulting to "all": a caller that forgets it fails to compile,
 * instead of a phone quietly inheriting a tab it cannot render. That is the whole safety of this
 * mechanism - a default here would hand the mobile shell the Cockpit's list on the first careless call.
 *
 * Hidden tabs are dropped here, and this is the ONLY place they are dropped - every other rule in this
 * file works off what this function returns, so hiding a tab needs no second edit anywhere.
 */
export function visibleTabs(surface: Surface, context: TabContext = NO_TEAM): { id: TabId; label: string; group: TabGroup }[] {
  return ALL_TABS.filter(
    (t) =>
      (t.surface === "all" || t.surface === surface) &&
      t.hidden !== true &&
      (t.group !== "team" || context.team) &&
      (t.id !== "teamplan" || context.teamPlan),
  ).map((t) => ({
    id: t.id,
    label: t.label,
    group: t.group,
  }));
}

/**
 * Resolve the ?tab= parameter to a tab THIS surface actually shows. Unknown, missing, retired, hidden, or
 * not-on-this-surface values fall to the surface's first tab (Account on the Cockpit, Notifications on the
 * phone). A team tab with no team on screen falls the same way: there is no team for it to show.
 *
 * It is filtered by surface for the same reason visibleTabs is: a phone opening a link to
 * ?tab=injectedtext must land on a real tab, not select a tab that its own strip does not list and its
 * own panel cannot draw. A deep link is not permission to render something.
 *
 * "machine", "telemetry", "privacy", "carmode" and "assistant" are retired ids (the "This machine" tab left in issue #2022; the
 * old standalone Telemetry page redirected to /settings?tab=telemetry, issue #1405; the Privacy tab was
 * removed by issue #2017; Car Mode by issue #1028; the Assistant by the Fleet Manager mission, step 9). They no longer resolve to a tab, so an old bookmark lands on the default rather
 * than on a tab that no longer exists. A hidden tab behaves exactly the same way, by the same rule and
 * with no special case: it is not in the list this reads, so an old link to it lands on the default.
 */
export function tabFromParam(raw: string | null, surface: Surface, context: TabContext = NO_TEAM): TabId {
  const shown = visibleTabs(surface, context);
  const match = shown.find((t) => t.id === raw);
  return match ? match.id : shown[0].id;
}
