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
  | "notifications"
  | "ai"
  | "language"
  | "transcription"
  | "assistant"
  | "fleetmanager"
  | "injectedtext";

interface TabDef {
  id: TabId;
  label: string;
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

// The full ordered set. The order is the order you meet them: how the fleet reaches you, what language it
// speaks to you in, how it hears you, the assistant built on all three, and last the text it hands your
// agents.
//
// Language takes the place AI held in the strip (issue #1010). The AI row is still here and still hidden -
// see the `hidden` note below; the two are separate decisions that happen to concern the same slot.
//
// The Fleet Manager takes the Assistant's place (the Fleet Manager mission, step 5): where the Fleet Manager runs,
// and the Wingman's turn verdict switches it is built on. The Assistant row is hidden, not deleted - the same
// one-word reversibility the AI row has - until step 9 removes the Assistant from the product.
//
// Assistant is the tab that used to be Car Mode. Car Mode was removed from the product (#1028), and the one
// setting it held that was never Car Mode's alone - the model the fleet brain thinks with - belongs to the
// Assistant, the surface that still drives that brain.
const ALL_TABS: TabDef[] = [
  { id: "notifications", label: "Notifications", surface: "all" },
  { id: "ai", label: "AI", surface: "all", hidden: true },
  { id: "language", label: "Language", surface: "all" },
  { id: "transcription", label: "Transcription", surface: "all" },
  { id: "assistant", label: "Assistant", surface: "all", hidden: true },
  { id: "fleetmanager", label: "Fleet Manager", surface: "all" },
  { id: "injectedtext", label: "Injected text", surface: "cockpit" },
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
export function visibleTabs(surface: Surface): { id: TabId; label: string }[] {
  return ALL_TABS.filter(
    (t) => (t.surface === "all" || t.surface === surface) && t.hidden !== true,
  ).map((t) => ({
    id: t.id,
    label: t.label,
  }));
}

/**
 * Resolve the ?tab= parameter to a tab THIS surface actually shows. Unknown, missing, retired, hidden, or
 * not-on-this-surface values fall to the first tab (Notifications).
 *
 * It is filtered by surface for the same reason visibleTabs is: a phone opening a link to
 * ?tab=injectedtext must land on a real tab, not select a tab that its own strip does not list and its
 * own panel cannot draw. A deep link is not permission to render something.
 *
 * "machine", "telemetry", and "privacy" are retired ids (the "This machine" tab left in issue #2022; the
 * old standalone Telemetry page redirected to /settings?tab=telemetry, issue #1405; the Privacy tab was
 * removed by issue #2017). They no longer resolve to a tab, so an old bookmark lands on the default rather
 * than on a tab that no longer exists. A hidden tab behaves exactly the same way, by the same rule and
 * with no special case: it is not in the list this reads, so an old link to it lands on the default.
 */
export function tabFromParam(raw: string | null, surface: Surface): TabId {
  const shown = visibleTabs(surface);
  const match = shown.find((t) => t.id === raw);
  return match ? match.id : shown[0].id;
}
