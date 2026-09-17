import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { SettingsTabPanel, SettingsTabStrip } from "@devthrottle/client-core/settings/SettingsTabs";
import { tabFromParam, type TabId } from "@devthrottle/client-core/settings/tabs";

// The mobile Settings screen.
//
// It was "AI settings": one untabbed scroll holding the AI models, with the two dictation checks linked
// out to screens of their own, no notification settings at all, and no way to set the Car Mode end
// phrase - while the desktop Cockpit had all of those, arranged differently. The phone and the desktop
// are the same account, so that difference was never a design, only drift.
//
// It is the SAME body as the Cockpit's Settings page now: the same tabs, the same cards, one
// implementation in client-core. This file is only the phone's frame - the back link and the title.
// Only the layout differs (styles.css re-tunes the shared cards for touch: the tab strip scrolls
// sideways, labels sit above their controls, and every control is full width).
//
// The tab rides in ?tab= exactly as it does on the desktop, so the same deep link opens the same tab on
// either surface, and the retired /mic-test and /transcription-test screens can redirect into it.
// The phone's route to one session (main.tsx: "/session/:sessionId"), for the Fleet Manager tab's "Open it".
const mobileSessionHref = (sessionId: string) => `/session/${encodeURIComponent(sessionId)}`;

export function Settings() {
  const [params, setParams] = useSearchParams();
  const [tab, setTab] = useState<TabId>(() => tabFromParam(params.get("tab"), "mobile"));

  // The tab is written back to the address so the phone's own Back gesture and a re-opened app land on
  // the tab that was being used, rather than snapping to Notifications. `replace` keeps tab switching
  // out of the history stack - Back should leave Settings, not walk back through four tabs.
  const choose = (next: TabId) => {
    setTab(next);
    setParams({ tab: next }, { replace: true });
  };

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Settings</h1>
      </header>

      <SettingsTabStrip active={tab} onSelect={choose} surface="mobile" />

      {/* sessionHref is the phone's own session screen (main.tsx: "/session/:sessionId"), for the Fleet Manager
          tab's "Open it".

          No accountHref or transcriptionHealthHref: the phone has neither an account page nor the
          Transcription Health report, and a settings screen must never offer a link to a route that
          does not exist here.

          surface="mobile" above is what keeps a Cockpit-only tab (Injected text) off this screen, both
          in the strip and in what ?tab= can resolve to - see client-core/settings/tabs.ts. */}
      <SettingsTabPanel tab={tab} sessionHref={mobileSessionHref} />
    </div>
  );
}
