# Cockpit History tab - cross-CLI verification (#742)

Each supported CLI was run as a real session on a slot-6 test Director (this branch), enrolled
with the local Gateway, and its History tab was opened in a real browser against this branch's
Cockpit build. For every CLI the rendered DOM was read back to confirm bubbles, markdown, and
link actions. Screenshots are in `cli-gallery/`.

## Summary table

| CLI | Result | Bubbles | Markdown | Links (Copy URL / Copy path) | Notes |
|---|---|---|---|---|---|
| Claude Code | PASS | You / Assistant | yes (h2, code, bold, list) | yes | Also shows the derived-state pill ("history: Needs you"). |
| Pi | PASS | You / Assistant / Tool result | yes | yes | 4 messages rendered. |
| Codex | PASS | You / Assistant | yes | yes | Session has interactive onboarding gates; once past them the turn renders. |
| Grok | PASS | You / Assistant | yes | yes | 7 messages; directory-trust gate handled at boot. |
| OpenCode | PASS | You / Assistant | yes | yes | Update-prompt gate dismissed at boot. |
| Copilot | PASS | You / Assistant | yes | yes | Trust gate handled at boot. |
| Gemini | PASS | single raw block | n/a (raw `<pre>`) | n/a | No structured transcript - rendered verbatim from the terminal buffer (`IsRawText`). This pass found and fixed a bug where the raw flag was not propagated to the bubble (now rendered as raw, not Markdown). |
| Cursor | NOT SUPPORTED | - | - | - | No history provider / no configured agent (matches desktop #737); the tab shows "History is not available for this agent yet." |

## Detail

- **Markdown (#739)** rendered as formatted HTML (`<h2>`, `<pre><code>`, `<strong>`, `<ul><li>`)
  for every structured-transcript CLI.
- **Links (#740)** were detected by the shared Core `LinkDetector`: the URL
  `https://example.com/docs` got an Open (new tab) + **Copy URL** action, and the path
  `C:\Repos\app\Program.cs` got a **Copy path** action.
- **Derived state (#741)** appears for Claude only (the background-agent signal lives in the
  Claude transcript format); other CLIs correctly show no derived-state pill.
- **Gemini (raw)**: its history is raw terminal scrollback. Verified it renders verbatim in a
  `<pre>` block after the `IsRawText` propagation fix in this pass.

## Bug found and fixed during this verification

`HistoryBubbleMapper` was not propagating `SessionHistoryDto.IsRawText` onto the bubbles, so
Gemini's raw terminal text rendered through the Markdown pipeline instead of verbatim. Fixed
(carry the flag onto every bubble) with regression tests. This is exactly the kind of per-CLI
issue the cross-CLI pass exists to catch.

## Harness note

All seven CLIs are configured and enabled on this machine. Several (Codex, Grok, OpenCode,
Copilot) gate their first turn behind an interactive onboarding/trust/update screen that the
automatic first-prompt dispatch cannot pass; those gates were cleared by sending the terminal the
appropriate key, after which each produced a normal turn that the History tab rendered. This is a
property of the agent CLIs' onboarding, not of the History feature.
