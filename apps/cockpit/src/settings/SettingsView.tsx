import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { SettingsTabPanel, SettingsTabStrip } from "@devthrottle/client-core/settings/SettingsTabs";
import { tabFromParam, type TabId } from "@devthrottle/client-core/settings/tabs";
import { InjectedTextTab } from "./InjectedTextTab";

// The Cockpit Settings page (issue #1025, epic #967) - the React port of the retired Blazor
// wwwroot/pages/settings.html.
//
// The page BODY is now shared with the mobile app (client-core/settings): the same tabs, the same
// cards, one implementation. This file is only the desktop frame around it - the page heading, the
// lede, and the pointers to the sibling pages that own the settings which are not here.
//
// The tabs, and what belongs on each:
//
//   Notifications - how a session that needs you reaches you: snooze length, display time zone,
//                   notifications on this device
//   Transcription - the transcription model, how your microphones are measuring, and the two on-demand
//                   checks (Test microphone, Test transcription)
//   Fleet Manager - where the account's Fleet Manager runs (agent and computer), starting, restarting and
//                   moving it, and the Wingman's turn verdict switches
//
// The AI tab is no longer offered - not in the strip, and not by ?tab= either. It named the hosting
// models to customers, which the hosting layer itself refuses to do, and most of it was inert on the
// hosted Gateway anyway. The component is kept so this is reversible by one word, not rebuilt from
// scratch - see client-core/settings/tabs.ts.
//
// The Transcription tab is new here and it is the reason the two surfaces were unified: the checks used
// to live ONLY on the Transcription Health page on the desktop and on two standalone screens on the
// phone, so neither surface's Settings could answer "why is my dictation wrong?". The health page keeps
// the report over time - speed, failures, most-corrected words - and no longer duplicates the checks.
//
// Issue #2022 - self-host IS the hosted Gateway with one tenant, so this page is IDENTICAL on both
// surfaces: per-account, no surface branching. The machine settings left the web page (diagnostics to
// the About page, autostart to the installer + the `cc-devthrottle autostart` command, addressing
// dropped, brain removed), so there is no "This machine" tab.
//
// A pure client of existing Gateway endpoints, same-origin (root-relative URLs, never a Director
// address). Responsive (CodingStyle.md): each tab renders immediately with a loading line and loads
// asynchronously; on a failure it shows an explicit error banner, never a fabricated value.

// The Cockpit's route to one session (main.tsx: "session/:sessionId"), for the Fleet Manager tab's "Open it".
const cockpitSessionHref = (sessionId: string) => `/session/${encodeURIComponent(sessionId)}`;

export function SettingsView() {
  const [params] = useSearchParams();
  // The initial tab is resolved straight from ?tab= with no Gateway round-trip first - the page renders
  // its tabs immediately, and each card loads its own data and shows its own error banner. Resolution is
  // scoped to this surface, so a tab this shell does not list can never be selected here either.
  const [tab, setTab] = useState<TabId>(() => tabFromParam(params.get("tab"), "cockpit"));

  return (
    <div className="page settings">
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <p className="settings-lede">Your DevThrottle account settings.</p>

      <p className="settings-relocated">
        Looking for something else? Your <Link to="/account">DevThrottle account</Link> and{" "}
        <Link to="/about">Gateway diagnostics</Link> each have their own page.
      </p>

      <SettingsTabStrip active={tab} onSelect={setTab} surface="cockpit" />

      {/* Injected text is a COCKPIT-ONLY tab (issue #550), so the Cockpit renders it itself rather than
          putting desktop-only code in the library the phone also loads. Everything else comes from the
          shared panel. */}
      {tab === "injectedtext" ? (
        <InjectedTextTab />
      ) : (
        <SettingsTabPanel
          tab={tab}
          accountHref="/account"
          transcriptionHealthHref="/transcription"
          sessionHref={cockpitSessionHref}
        />
      )}
    </div>
  );
}
