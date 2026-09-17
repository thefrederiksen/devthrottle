import { AiTab } from "./AiTab";
import { AssistantTab } from "./AssistantTab";
import { FleetManagerTab } from "./FleetManagerTab";
import { LanguageTab } from "./LanguageTab";
import { NotificationsTab } from "./NotificationsTab";
import { TranscriptionTab } from "./TranscriptionTab";
import { visibleTabs, type Surface, type TabId } from "./tabs";
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
}

export function SettingsTabStrip({ active, onSelect, surface }: SettingsTabStripProps) {
  return (
    <div className="settings-tabs" role="tablist" aria-label="Settings sections">
      {visibleTabs(surface).map((t) => (
        <button
          key={t.id}
          type="button"
          role="tab"
          aria-selected={active === t.id}
          className={active === t.id ? "settings-tab active" : "settings-tab"}
          onClick={() => onSelect(t.id)}
        >
          {t.label}
        </button>
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
    case "assistant":
      return <AssistantTab />;
    case "fleetmanager":
      return <FleetManagerTab sessionHref={sessionHref} />;
    // Cockpit-only tabs are rendered by the Cockpit shell, not from here: their content is desktop-only
    // code and has no business in the library both shells load. The shell checks for them BEFORE calling
    // this panel (see the Cockpit's SettingsView), so reaching this line means a tab was selected on a
    // surface whose strip does not list it - which tabFromParam already prevents. Return nothing rather
    // than invent a panel.
    case "injectedtext":
      return null;
  }
}
