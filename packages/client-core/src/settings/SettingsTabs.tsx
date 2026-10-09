import { AiTab } from "./AiTab";
import { FleetManagerTab } from "./FleetManagerTab";
import { LanguageTab } from "./LanguageTab";
import { NotificationsTab } from "./NotificationsTab";
import { TranscriptionTab } from "./TranscriptionTab";
import { GROUP_LABELS, visibleTabs, type Surface, type TabContext, type TabGroup, type TabId } from "./tabs";
import "./settings.css";

// The Settings tab strip and the panel it selects - the whole page body, shared by the Cockpit and the
// phone. Each shell supplies only its own frame around this: a page heading and left rail on the
// desktop, a back link and app bar on the phone.
//
// Why the switch lives here rather than in each app: the two surfaces have to offer the SAME settings.
// A tab list shared but a switch copied is a switch that grows a branch on one surface only.

export interface SettingsTabStripProps {
  active: TabId;
  onSelect: (tab: TabId) => void;
  /** Which shell is rendering. Decides which tabs the strip lists - see tabs.ts. */
  surface: Surface;
  /** The team on screen, when there is one - see TabContext. Omitted means no team tabs. */
  context?: TabContext;
  /**
   * Lay the tabs out down the side under their small group headings (the Cockpit), rather than as one
   * strip (the phone). Layout only: the same tabs, in the same order, with the same names either way.
   */
  grouped?: boolean;
  /** The heading over the team's tabs - the team's name, which only the shell knows. Required with a team. */
  teamLabel?: string;
  /** A count of things waiting on a tab - the Gateway's number, rendered verbatim (the Dictionary tab's pending
   *  suggestions, owner, 8 Oct 2026). Zero or absent draws nothing. */
  badges?: Partial<Record<TabId, number>>;
}

function groupLabel(group: TabGroup, teamLabel: string | undefined): string {
  if (group !== "team") return GROUP_LABELS[group];
  if (teamLabel === undefined) throw new Error("SettingsTabStrip: the team's tabs need the team's name as their heading");
  return teamLabel;
}

export function SettingsTabStrip({ active, onSelect, surface, context, grouped = false, teamLabel, badges }: SettingsTabStripProps) {
  const tabs = visibleTabs(surface, context);
  const button = (t: { id: TabId; label: string }) => {
    const count = badges?.[t.id] ?? 0;
    return (
      <button
        key={t.id}
        type="button"
        role="tab"
        aria-selected={active === t.id}
        className={active === t.id ? "settings-tab active" : "settings-tab"}
        onClick={() => onSelect(t.id)}
      >
        {t.label}
        {count > 0 && (
          <>
            <span className="settings-tab-badge" title={`${count} pending`} aria-hidden="true" data-testid={`settings-tab-badge-${t.id}`}>
              {count}
            </span>
            <span className="settings-visually-hidden">{` (${count} pending)`}</span>
          </>
        )}
      </button>
    );
  };

  if (!grouped) {
    return (
      <div className="settings-tabs" role="tablist" aria-label="Settings sections">
        {tabs.map(button)}
      </div>
    );
  }

  // Grouped: one tab list, with each group's heading drawn above its first tab. The headings are
  // presentation only - the list a screen reader walks is still the tabs, in order - so they sit in the
  // tablist as non-tab rows rather than splitting it into several lists.
  return (
    <div className="settings-tabs settings-tabs-side" role="tablist" aria-orientation="vertical" aria-label="Settings sections">
      {tabs.map((t, i) => (
        <div key={t.id} role="presentation" className="settings-tab-row">
          {(i === 0 || tabs[i - 1].group !== t.group) && (
            <div className="settings-tab-group" role="presentation" data-testid={`settings-group-${t.group}`}>
              {groupLabel(t.group, teamLabel)}
            </div>
          )}
          {button(t)}
        </div>
      ))}
    </div>
  );
}

export interface SettingsTabPanelProps {
  tab: TabId;
  /** Routes that exist on the mounting surface only. Omitted means "this surface has no such page", and
   *  the line that would link to it is not rendered - never a link to a route that does not exist. */
  accountHref?: string;
  transcriptionHealthHref?: string;
  /** The surface's route to one session, for the Fleet Manager tab's "Open it". */
  sessionHref?: (sessionId: string) => string;
}

export function SettingsTabPanel({ tab, accountHref, transcriptionHealthHref, sessionHref }: SettingsTabPanelProps) {
  switch (tab) {
    case "notifications":
      return <NotificationsTab />;
    case "ai":
      return <AiTab accountHref={accountHref} />;
    case "language":
      return <LanguageTab />;
    case "transcription":
      return <TranscriptionTab healthHref={transcriptionHealthHref} />;
    case "fleetmanager":
      return <FleetManagerTab sessionHref={sessionHref} />;
    // Cockpit-only tabs are rendered by the Cockpit shell, not from here: their content is desktop-only
    // code and has no business in the library both shells load. The shell checks for them BEFORE calling
    // this panel (see the Cockpit's SettingsView), so reaching this line means a tab was selected on a
    // surface whose strip does not list it - which tabFromParam already prevents. Return nothing rather
    // than invent a panel.
    case "account":
    case "usage":
    case "dictionary":
    case "injectedtext":
    case "devices":
    case "members":
    case "teamplan":
    case "governance":
      return null;
  }
}
