# Doorbell screen captures

Real screens, not written ones. Each file is one frame of a real agent running in the Director's own
terminal, read with `Session.SnapshotLiveScreen()` - the same call the doorbell's safety check makes -
on the Mac mini on 17 September 2026, at the Director's default 120 by 40 grid. One later capture
(`codex-wrapped-composer`) was taken on Windows on 26 September 2026 at 100 by 30, with the rig's
`--codex-composer-capture` mode (`src/CcDirector.DeliveryQualification`), because a prompt that wraps
needs a screen narrower than 120 columns.

- Claude Code 2.1.274, started with `--dangerously-skip-permissions`.
- Codex 0.154.0. The account hit its usage limit during the capture, so there is no mid-turn Codex
  screen; `codex-menu-rate-limit` is the menu Codex drew instead.

Fields: `rows` (top to bottom, trailing-trimmed), `cursorRow`, `cursorCol`, `cursorVisible`,
`alternateScreen`, and `activity`/`status` as the bare capture harness saw them. The harness ran without
the Director's terminal state detector, so `activity` in these files is NOT the state a running Director
would report; the tests do not read it.

What each file shows:

| File | Screen |
|---|---|
| `claude-idle-empty-after-turn` | A turn has ended ("Brewed for 17s"); empty composer |
| `claude-idle-empty-fresh` | A new session before any prompt; empty composer |
| `claude-owner-text` | One unsent sentence in the composer |
| `claude-owner-text-two-lines` | A two-line unsent draft |
| `claude-collapsed-paste` | A 30-line paste, drawn collapsed as `[Pasted text #1 +29 lines]` (issue 2845) |
| `claude-slash-command-typed` | `/model` typed, not yet entered, with the command list open above |
| `claude-working` | Mid-turn: output streaming, composer drawn empty, footer says `esc to interrupt` |
| `claude-menu-model-picker` | The `/model` picker, cursor hidden (issue 2842) |
| `claude-menu-trust-folder` | The folder-trust dialog, `❯ No, exit` selected |
| `codex-idle-empty-placeholder` | Empty composer showing the placeholder `Ask Codex to do anything` |
| `codex-owner-text` | One unsent sentence in the composer |
| `codex-menu-rate-limit` | The model-switch menu Codex opened at its usage limit |
| `codex-menu-trust-folder` | The folder-trust dialog, `› 1. Yes, continue` selected |
| `codex-wrapped-composer` | Codex 0.157.1 with a 164-character prompt typed (no Enter) into a 100-column composer: the `›` row, then one continuation row indented by the two columns the glyph and its separator take, cursor at the end of the last row - the shape a wrapped Codex composer has, captured for the phase 6 review finding 1 |

Two files are DERIVED, not captured (inspection 4, ruling 3), and say so in their own `derivedFrom` and
`derivation` fields:

| File | Derived from |
|---|---|
| `claude-idle-whitespace-draft-after-turn` | `claude-idle-empty-after-turn` with three spaces typed: the row is unchanged, because rows are trailing-trimmed, and the cursor is at column 5 instead of 2 |
| `claude-idle-whitespace-draft-fresh` | `claude-idle-empty-fresh`, the same way |

The live proof in the fix round typed real spaces into a real Claude Code composer and saved the screen
(`docs/missions/message-load-2026-09-16/slice-2-evidence/fix-round/`), which is where the column claim is
checked against the agent itself.

The capture program is not part of the repository; it created a `SessionManager`, started the agent with
`CreateSession(..., SessionBackendType.ConPty, ...)`, typed with `Session.SendInput`, and wrote
`SnapshotLiveScreen()` to these files.
