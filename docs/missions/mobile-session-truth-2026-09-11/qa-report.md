# Mobile session truth QA report

Status: active. The release is deployed and the roster is verified in production. The final live
Wingman check is blocked because the supplied session closed before deployment finished.

## What this changes for the owner

Wingman selects a newer terminal authentication failure instead of narrating an older successful
reply after the session has moved on. The main mobile roster card also shows the running agent tool
as separate metadata instead of making the selected model stand in for it.

The supplied `testing pi` session was not changed by this mission. Its provider credential remains
outside this mission.

## What is proven

- The Wingman regression was seen failing on the guarded old production path while its control stayed
  green, then passing after the repair. Hosted continuous integration passed the repaired slice and
  pull request `#2813` is merged to `main`.
- The roster value is derived from the agent field, serialized through the authenticated and
  tenant-selected `/sessions` route, retained across a missing poll, replaced by a newer live value,
  grouped by the assembled Home page, and rendered by the real roster row.
- Substituting the selected model for the roster tool label made the focused mobile facts fail while
  the session-name control stayed green. Bypassing the Gateway stamp made both response-field facts
  fail while the authentication and tenant-selection control stayed green.
- Fresh independent inspections passed both repaired slices. The earlier failed inspections and the
  later passing inspections are preserved beside this report.
- Pull request `#2814` merged, its unchanged-head continuous-integration rerun passed, and hosted
  deployment run `34679499149` put merge commit `53b8abfc3` into production without rollback.
- The live mobile roster loaded at a 390 by 844 viewport. A derived enumeration found 23 real cards
  and 23 agent-tool chips, including visible `Claude Code`, `Pi`, and `Codex` values. The screenshots
  beside this report preserve the top of the roster and a centered real Pi card.

## What is not yet proven

- The production Wingman has not been observed selecting and speaking the `testing pi` terminal
  authentication failure. The session history says `testing pi` closed at
  `2026-09-12T00:45:31.690641Z`, before deployment finished. Its cached Wingman clip is the old answer,
  and the 390 by 844 live Voice route now says it is reconnecting. With no live terminal, playing that
  old clip or regenerating from stored conversation would not exercise the changed source-selection
  path and cannot be counted as a pass.
- The production narrow-screen proof used a real browser viewport, not physical phone hardware.
- The complete local Gateway gate was not clean because the shared environment lacked its PostgreSQL
  dependency and carried hosted tenant/auth state. Clean hosted runs are the complete-suite evidence.
- The mobile production build retains its existing large-chunk warning. Bundle splitting was outside
  this mission.

## What was wrong along the way

- The first Wingman implementation fixed the direct narration path but allowed the periodic sweep to
  trust a stale cached source. Inspection caught it before merge; the repair makes the sweep read the
  live terminal before choosing a source.
- The first roster proof called an internal enrichment helper and serialized the resulting object,
  so it did not prove the served route it claimed to cover. Inspection caught it before merge; the
  replacement starts the real Gateway host and requests the authenticated route.
- The first hosted roster run spent 81 minutes before an unrelated Launcher fact failed. Waiting on
  it looked like inactivity because the workflow exposes only one monolithic test step. The exact
  failure and rerun are now called out explicitly instead of being represented as a generic pending
  state.
- The final live check was planned against a session the owner intended to leave alone, but that
  session closed while the build, inspection, continuous integration, and deployment were running.
  The remaining evidence therefore covers the code path and production roster, not a production
  terminal-failure narration.

## Decisions for the owner

A live replacement session already showing the same later terminal authentication failure is needed
to finish the Wingman check. Verification must not send it a prompt, alter its provider credential,
or substitute a different model.
