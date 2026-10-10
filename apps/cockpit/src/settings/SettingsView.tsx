import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { SettingsTabPanel, SettingsTabStrip } from "@devthrottle/client-core/settings/SettingsTabs";
import { tabFromParam, type TabContext, type TabId } from "@devthrottle/client-core/settings/tabs";
import { useAccounts } from "@devthrottle/client-core/auth/useAccounts";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { getTeamPage } from "@devthrottle/client-core/teams/teamPageClient";
import { GatewayError } from "@devthrottle/client-core/api/client";
import { ErrorBanner, LoadingState } from "../components";
import { InjectedTextTab } from "./InjectedTextTab";
import { useSuggestionCount } from "../dictionary/useSuggestionCount";
import { AccountTab } from "../account/AccountTab";
import { DemoModeCard } from "@devthrottle/client-core/settings/DemoModeCard";
import { YourThrottleView } from "../throttle/YourThrottleView";
import { DictionaryView } from "../dictionary/DictionaryView";
import { PhoneView } from "../phone/PhoneView";
import { TranscriptionHealthView } from "../transcription/TranscriptionHealthView";
import { TeamPageView } from "../team/TeamPageView";
import { InviteView } from "../team/InviteView";
import { TeamGovernanceView } from "../team/TeamGovernanceView";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-settings";

// The Cockpit Settings page (issue #1025, epic #967) - the React port of the retired Blazor
// wwwroot/pages/settings.html.
//
// ONE SETTINGS PAGE, TABS DOWN THE LEFT (owner, 8 Oct 2026, following how ChatGPT and Claude lay out Settings). The
// tabs sit under small headings - You, Voice, Fleet, and the team on screen - and several pages that were rows of the
// Cockpit's menu are tabs here now: Account, Plan and usage (Your Throttle), Dictionary, Devices and phone,
// and the team's Members and Team plan. Their old addresses lead here (routes.tsx). The team's Governance tab is new here.
//
// The tab set and its order are client-core's (tabs.ts), shared with the phone - CLAUDE.md rule 8. This file is only
// the desktop frame: the heading, the side layout, and the Cockpit-only tabs, whose content is Cockpit code that has no
// business in the library both shells load. The shared tabs come from the shared panel.
//
// THE TAB LIVES IN THE ADDRESS (?tab=). The menu behind your name links straight to a tab - Usage, Connect your phone -
// so a link followed while Settings is already open must move the tab, which a tab held only in component state would
// not. Choosing a tab here writes the address with `replace`, so Back leaves Settings rather than walking the tabs.
//
// The team tabs are offered only while a team is on screen in the whole app, and Team plan only when the Gateway's Team
// page answer carries a bill - its verdict that this person's role sees the team's plan (rule 7). The page reads that
// answer once per team to decide; the tab reads it again for itself, so the tab always shows the Gateway's latest. A
// read that FAILED is not a verdict either way (review of #3682): the Team plan tab stays, and while it is "unknown"
// the tab shows only the failure and a Retry of THIS read - never the plan section, which would draw an empty page for
// a role that has no plan. The Retry's answer then decides. Only the Gateway's refusal (403) takes the tab away.
//
// A link straight to a team tab waits for its answer. While the team on screen is still being confirmed, or the Team
// page has not yet said whether this person sees the plan, the panel says it is loading instead of opening Account
// and then jumping.
//
// Responsive (CodingStyle.md): each tab renders immediately with a loading line and loads asynchronously; on a failure
// it shows an explicit error banner, never a fabricated value.

// The Cockpit's route to one session (main.tsx: "session/:sessionId"), for the Fleet Manager tab's "Open it".
const cockpitSessionHref = (sessionId: string) => `/session/${encodeURIComponent(sessionId)}`;

/** What the Gateway's Team page answer says about the Team plan tab: offered (it carries a bill), not offered (no bill,
 *  or the Gateway refused the page), or unknown (the read failed - `error` says why). */
type TeamPlanAnswer = { kind: "offered" } | { kind: "not-offered" } | { kind: "unknown"; error: string };

/** The Team plan answer for the team on screen; null while it is first read. `retry` reads it again, keeping the last
 *  answer on screen until the new one lands (`retrying`), so the tab does not drop out of the strip and come back. */
function useTeamPlanAnswer(teamId: string | null): { answer: TeamPlanAnswer | null; retrying: boolean; retry: () => void } {
  const [held, setHeld] = useState<{ teamId: string; attempt: number; answer: TeamPlanAnswer } | null>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (teamId === null) return undefined;
    const controller = new AbortController();
    getTeamPage(teamId, controller.signal).then(
      (page) => setHeld({ teamId, attempt, answer: { kind: page.bill !== null ? "offered" : "not-offered" } }),
      (err) => {
        if (controller.signal.aborted) return;
        const refused = err instanceof GatewayError && err.status === 403;
        setHeld({
          teamId,
          attempt,
          answer: refused ? { kind: "not-offered" } : { kind: "unknown", error: describeAndReport(SURFACE, "read the team's plan", err) },
        });
      },
    );
    return () => controller.abort();
  }, [teamId, attempt]);
  const answer = teamId === null || held === null || held.teamId !== teamId ? null : held.answer;
  const retrying = answer !== null && held !== null && held.attempt !== attempt;
  const retry = () => setAttempt((n) => n + 1);
  return { answer, retrying, retry };
}

export function SettingsView() {
  const [params, setParams] = useSearchParams();
  const team = useCurrentTeam();
  const { active } = useAccounts();

  // The team the team tabs are about: one on screen, confirmed, in the whole app. A Collaborator's pages-only app never
  // reaches this page at all (the shell shows only their pages).
  const current = !team.resolving && team.current !== null && team.current.app.full ? team.current : null;
  const plan = useTeamPlanAnswer(current?.id ?? null);
  const teamPlan = plan.answer;
  const context: TabContext = { team: current !== null, teamPlan: teamPlan !== null && teamPlan.kind !== "not-offered" };

  // Resolved from the address on every render - see "THE TAB LIVES IN THE ADDRESS" above. Scoped to this surface and
  // to the team on screen, so a tab this page does not list can never be selected.
  const asked = params.get("tab");
  const waiting =
    (asked === "members" || asked === "teamplan" || asked === "governance") &&
    (team.resolving || (asked === "teamplan" && current !== null && teamPlan === null));
  const tab = tabFromParam(asked, "cockpit", waiting ? { team: true, teamPlan: true } : context);
  const choose = (next: TabId) => setParams({ tab: next }, { replace: true });
  // The Dictionary tab carries the count of suggestions waiting, re-read as the tab changes (owner, 8 Oct 2026).
  const suggestions = useSuggestionCount(true, tab);

  return (
    <div className="page settings settings-side">
      <div className="page-head">
        <h1>Settings</h1>
        {active !== null && <span className="settings-who">{active.email ?? active.label}</span>}
      </div>

      <div className="settings-body">
        <SettingsTabStrip
          active={tab}
          onSelect={choose}
          surface="cockpit"
          context={context}
          grouped
          teamLabel={current?.name}
          badges={{ dictionary: suggestions }}
        />
        <div className="settings-panel" role="tabpanel" aria-label="Settings section" data-testid={`settings-panel-${tab}`}>
          {waiting ? (
            <LoadingState message="Opening the team..." />
          ) : tab === "teamplan" && teamPlan?.kind === "unknown" ? (
            plan.retrying ? (
              <LoadingState message="Reading the team's plan again..." />
            ) : (
              <ErrorBanner message={teamPlan.error} onRetry={plan.retry} />
            )
          ) : (
            <CockpitTab tab={tab} teamId={current?.id ?? null} view={params.get("view")} />
          )}
        </div>
      </div>
    </div>
  );
}

function CockpitTab({ tab, teamId, view }: { tab: TabId; teamId: string | null; view: string | null }) {
  switch (tab) {
    // Demo mode heads the Account tab (owner, 8 Oct 2026): it is an account-wide switch, and the presenter needs to
    // find it in one click before a demo.
    case "account":
      return (
        <>
          <DemoModeCard />
          <AccountTab />
        </>
      );
    case "usage":
      return <YourThrottleView />;
    case "dictionary":
      return <DictionaryView />;
    // Injected text is a COCKPIT-ONLY tab (issue #550), so the Cockpit renders it itself.
    case "injectedtext":
      return <InjectedTextTab />;
    case "devices":
      return <PhoneView />;
    // The shared Transcription tab - the model and the two checks - and, beneath it on the desktop, the Transcription
    // Health report over time that used to be its own page. Rule 8 lets the desktop go deeper inside a tab; the phone
    // has the shared part.
    case "transcription":
      return (
        <>
          <SettingsTabPanel tab="transcription" />
          <TranscriptionHealthView />
        </>
      );
    case "members":
    case "teamplan":
      if (teamId === null) throw new Error(`Settings: the ${tab} tab was selected with no team on screen`);
      if (tab === "members" && view === "invite") return <InviteView teamId={teamId} />;
      return <TeamPageView key={`${teamId}-${tab}`} teamId={teamId} section={tab === "members" ? "members" : "plan"} />;
    case "governance":
      if (teamId === null) throw new Error("Settings: the governance tab was selected with no team on screen");
      return <TeamGovernanceView key={teamId} teamId={teamId} />;
    default:
      return <SettingsTabPanel tab={tab} sessionHref={cockpitSessionHref} />;
  }
}
