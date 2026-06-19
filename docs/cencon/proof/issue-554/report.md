# Issue 554 - In-voice-mode ear icon on sessions (proof)

## Summary

A session being driven by voice (the phone is on the Voice / in-car tab) now shows a small
vector ear indicator on all three session surfaces, so an operator can glance and see "this one
is in voice mode" consistently. The indicator appears only while voice mode is on and clears the
moment the session leaves voice mode.

The underlying flag already existed: Session.VoiceMode (true when ViewMode == Voice) flows to
SessionDto.VoiceMode. This change only adds the consistent ear visual and a live change
notification for the desktop rail.

## What changed

### Shared model (live update support)

- src/CcDirector.Core/Sessions/Session.cs
  - ViewMode changed from a plain auto-property to a property with a backing field that raises a
    new OnViewModeChanged(old, new) event when it changes. This lets the desktop rail's
    view-model update the ear indicator live as a phone enters and leaves the Voice tab, instead of
    waiting for a list rebuild. VoiceMode / MobileMode remain pure derivations of ViewMode.

### Surface 1 - Desktop Avalonia rail

- src/CcDirector.Avalonia/SessionViewModel.cs
  - Added IsVoiceMode - a pure passthrough of Session.VoiceMode.
  - Subscribes to Session.OnViewModeChanged and raises PropertyChanged(nameof(IsVoiceMode))
    on the UI thread so the ear shows/clears live.
- src/CcDirector.Avalonia/MainWindow.axaml
  - On the badge line (LINE 2) of the session-item template, added a 14x14 Viewbox containing two
    vector Path strokes (outer ear fold + inner canal). IsVisible="{Binding IsVoiceMode}".
    Stroke uses {DynamicResource TextForeground} (no hard-coded color), matching the existing
    badge sizing/placement.

### Surface 2 - Cockpit session rail

- src/CcDirector.Cockpit/Components/SessionRail.razor
  - Replaced the tag-voice "voice" text with an inline SVG ear (same two-path geometry) inside the
    same tag-voice pill, shown when s.VoiceMode. Kept the tag-voice styling hook.
- src/CcDirector.Cockpit/wwwroot/app.css
  - tag-voice is now an inline-flex pill sized to the ear; added .tag-voice .ear-icon
    (12x12). The ear inherits the pill's existing color via currentColor.

### Surface 3 - /m mobile list

- src/CcDirector.Cockpit/wwwroot/m/m.js
  - In the list-row render, an inline SVG ear (same geometry) is prepended to the session name when
    s.voiceMode is true (the DTO field is voiceMode in the JSON). The name text moved into a
    .scard-name-text span so it still ellipsizes around the ear.
- src/CcDirector.Cockpit/wwwroot/m/m.css
  - .scard-name is now a flex row; .scard-name-text keeps the nowrap/ellipsis; added
    .scard-name .ear-icon (16x16, color:var(--accent)).
- Cache-bust bumped v21 -> v22 in m/index.html (css + js) and m/sw.js (CACHE name + SHELL list),
  following the existing pattern, so phones pick up the new m.js/m.css.

## Icon approach (NO emoji / NO unicode)

The ear is a VECTOR drawing on every surface - never a unicode/emoji ear character:

- Desktop: Avalonia Path geometry inside a Viewbox (two stroked paths).
- Cockpit + /m: inline svg with two path elements (currentColor stroke).

All source remains ASCII. The same two-path geometry (outer ear fold curve + inner canal curve) is
used on all three surfaces for a consistent shape.

## How the VoiceMode bool was sourced per surface

- Desktop: new SessionViewModel.IsVoiceMode passthrough of Session.VoiceMode, kept live via the
  new Session.OnViewModeChanged event.
- Cockpit: existing SessionDto.VoiceMode (s.VoiceMode) - already rendered the old text tag.
- /m: existing voiceMode field on the session DTO JSON (s.voiceMode).

## Build

dotnet build cc-director.sln - Build succeeded, 0 Warnings, 0 Errors.
(Built with -p:WarningsNotAsErrors=NU1903 locally to bypass the SQLite NuGet-audit advisory; no
csproj change committed.)

## Tests

New tests added:

- src/CcDirector.Core.Tests/SessionViewModeTests.cs - pins the OnViewModeChanged event
  (raises old/new, no raise on unchanged) and the VoiceMode derivation. 3 tests, all pass.

Affected suites run (all green except one pre-existing, unrelated environmental failure):

- Core.Tests: 2110 passed, 0 failed, 4 skipped.
- Avalonia.Tests: 66 passed, 0 failed.
- Cockpit.Tests: 63 passed, 0 failed.
- Gateway.Tests: 837 passed, 1 failed.
  - The one failure is DictationEndpointTests.FullPipeline_transcribes_phase0_clip2_with_realtime_provider
    ("expected at least 1 partial transcript, got 0"). This is a LIVE speech-to-text streaming test
    that connects to a real provider with a 90-second deadline; it shares no code with this ear-icon
    change and fails for an environmental/provider-timing reason (no partials returned in time). Not
    introduced by this work.

## Visual confirmation still needed (manual)

UI-icon visibility is not unit-testable here, and launching all three user interfaces with a real
voice-mode session is a manual step. A human run should confirm, on each surface, the ear is PRESENT
on a voice-mode session and ABSENT on a non-voice session:

1. Desktop rail (SESSIONS list): put a phone on a session's Voice tab; the ear appears on LINE 2
   next to the agent/type badges, and clears when the phone leaves the Voice tab.
2. Cockpit rail: the same session shows the blue tag-voice pill now containing the ear (no longer
   the word "voice").
3. /m mobile list: the session row shows the accent-colored ear before the name.

Look specifically at: ear present vs absent, the ear renders as a drawn shape (not a box/garbage
glyph), and it does not push the session name off-row.
