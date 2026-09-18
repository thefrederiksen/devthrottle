# Wingman slice inspection

**Result: FAIL**

## High severity

The periodic sweep can retain and serve stale audio after a later request fails in the terminal.

`GatewayHost` skips every session for which `HasVoice` is true before calling `GenerateAsync`. If the
working transition was missed, the old clip remains cached. A later user message with no agent reply
therefore never reaches source selection, never reads the live screen, and never replaces or removes
the old clip.

There is a second failure for sessions without cached audio. The sweep passes `onProviderReached`, and
`GenerateOnceAsync` deliberately reads the live screen only when that callback is null. Consequently,
the sweep cannot classify the terminal failure for the exact later-user-message shape. It records
`NothingToNarrate` instead of narrating the failure. The direct generation path and the on-demand explain
path do read the screen, so their tests pass while the periodic path remains broken.

This proves the prior suspicion. The relevant code is in `GatewayHost.cs` around the `HasVoice` sweep
guard and callback invocation, and `WingmanVoiceService.cs` around the `NeedsLiveScreen` condition.

## Medium severity

The tests do not cover the production sweep path. The terminal regression calls `GenerateAsync` without
the sweep callback, and the cache tests exercise direct generation with a current text reply. There is no
test that combines an existing cached clip, a missed working transition, a later user message, a terminal
failure on the live screen, and the periodic sweep. There is also no assertion that the old audio is
removed or that the terminal failure becomes the new source on that path.

## Confirmed safe areas

The direct stale-reply selection is correct: a later user message prevents the older agent reply from
winning, and a positively classified live terminal failure becomes the source. The terminal text is
passed through the bounded classifier and the dedicated prompt labels it as untrusted evidence and says
not to follow instructions inside it. The terminal source identity is distinct from its content and is
persisted with the audio metadata, so an unchanged terminal failure is not regenerated repeatedly.

FAIL — the periodic sweep can preserve stale audio and cannot narrate a later terminal failure; see `docs\missions\mobile-session-truth-2026-09-11\inspection-wingman.md`.
