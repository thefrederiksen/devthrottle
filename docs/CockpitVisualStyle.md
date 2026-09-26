# Cockpit Visual Style Guide

> **Scope:** the React web Cockpit (`apps/cockpit`), served by the Gateway. The desktop app has its
> own, separate guide in [docs/VisualStyle.md](VisualStyle.md) (a charcoal, VS Code-inspired palette).
> The web Cockpit uses a distinct dark navy palette; this document is its written standard, added with
> the shared user-interface kit in issue 1244.

The web Cockpit shares its palette with the mobile shell (`/m`) and the voice prototype so the three
shells read as one product. Every colour is a design token defined in one place -
`apps/cockpit/src/styles.css` (the `:root` block at the top) - and nothing in the app hard-codes a
colour outside that block. The shared kit (`apps/cockpit/src/components`) styles itself only from these
tokens (see `components/components.css`).

---

## 1. Colour palette (the navy tokens)

These are the exact tokens declared in `apps/cockpit/src/styles.css`. This table is the written record
of what the app actually uses; keep it in step with that file.

| Token | Hex | Usage |
|-------|-----|-------|
| `--bg` | `#0b1020` | Page background (the deepest navy) |
| `--surface` | `#141a2e` | Panels, rails, cards, modal bodies |
| `--surface-2` | `#1b2238` | Raised surfaces: secondary buttons, list rows, inputs-on-panel |
| `--border` | `#28304a` | Borders, separators, panel edges |
| `--text` | `#e6e9f2` | Primary text |
| `--text-dim` | `#99a0b8` | Secondary text: descriptions, meta, timestamps, hints |
| `--accent` | `#3b82f6` | Primary action blue: primary buttons, active nav, focus ring |
| `--attention` | `#f14c4c` | Attention / danger red: destructive buttons, error text, needs-you |
| `--warn` | `#f59e0b` | Warning yellow (the Brief accents, issue 973) |
| `--code-bg` | `#0a0f1e` | Inset code / monospace input surface |
| `--needsyou-bg` | `#2a1d1f` | Tinted "needs you" panel background |
| `--needsyou-text` | `#f1d8d8` | "needs you" panel text |
| `--needsyou-fyi-bg` | `#262017` | Tinted "for your information" panel background |
| `--needsyou-fyi-text` | `#e8dcc8` | "for your information" panel text |
| `--owner-bubble-bg` | `#1e3a6b` | The owner's message on the Fleet Manager page |
| `--owner-bubble-border` | `#2c5391` | Its edge; also the recommended option's edge |
| `--accent-text` | `#9cc2ff` | Light accent text: the "(change)" link, a Finding card's heading |
| `--ok` | `#22c55e` | Ready green: a Ready card's edge, a finished dot |
| `--ok-text` | `#6ee79b` | Ready green text |
| `--danger-text` | `#ff8b8b` | A Decision card's heading |
| `--decision-bg` | `#1b1522` | A Decision card's tint |
| `--panel-deep` | `#0f1528` | The Fleet Manager page's right panel (hidden for now), and the walkthrough's round column |
| `--dot-idle` | `#5b6480` | A stopped session's dot |
| `--pin-bg` | `#16254a` | The pinned Fleet Manager row in the session list |
| `--pin-mark-border` | `#3a4f7a` | The edge of its "Fleet Manager" mark |

Non-colour token:

| Token | Value | Usage |
|-------|-------|-------|
| `--rail-left-width` | `220px` | Width of the left navigation rail |

Two literal whites are used deliberately and are not tokens: `#ffffff` for text on top of the accent
and danger fills (a token would only ever hold white here). Everything else is a token.

---

## 2. Typography

- Font family (all text): the system UI stack -
  `-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif`.
- Monospace (paths, session ids, code, cron expressions): `"Cascadia Mono", Consolas, Menlo, monospace`.
- Common sizes: 22px page title, 15-16px section/modal headings, 13-14px body, 11-12px meta and hints.

---

## 3. The shared user-interface kit

Every page imports its building blocks from `apps/cockpit/src/components` instead of hand-rolling a
one-off class. Reach for these before writing a new button class or a new "Loading..." line.

| Component | What it is |
|-----------|------------|
| `Button` | The one button, with four variants (below). Forwards every native button attribute. |
| `PageHeader` | A page title, an optional one-line subtitle, and an optional right-aligned actions slot. |
| `LoadingState` | The single "Loading..." indicator (`role="status"`). |
| `EmptyState` | The "nothing here yet" message, with an optional title and an optional next-step action. |
| `ErrorBanner` | The red failure banner (`role="alert"`), with an optional Retry button. |
| `ConfirmDialog` | The one confirmation dialog every destructive action routes through (section 5). |
| `StatusMessage` + `useFlash` | A small transient status line for action results (replaces `window.alert`). |

### Button variants

| Variant | Colour | Use for |
|---------|--------|---------|
| `primary` | Accent blue (`--accent`) | The main action on a surface. One per surface. |
| `secondary` | Bordered navy (`--surface-2` + `--border`) | Supporting actions. The default. |
| `danger` | Red (`--attention`) | Destructive actions. Always the confirm button of a `ConfirmDialog`. |
| `ghost` | Borderless, accent text | A small, link-like inline control. |

All buttons default to `type="button"` (an action, never an accidental form submit), dim to 0.5 opacity
when disabled, and lighten slightly on hover.

---

## 4. Surfaces and shape

- Page content lives in the padded main pane; full-bleed panes (the terminal, the Sessions screen)
  cancel that padding with negative margins - see the comments in `styles.css`.
- Cards and panels: `--surface` background, 1px `--border`, 8px corner radius.
- Modals: `--surface` body, 1px `--border`, 12px corner radius, dimmed backdrop
  (`rgba(0, 0, 0, 0.55)`), a soft drop shadow.
- Buttons and inputs: 6-8px corner radius; inputs sit on `--code-bg` or `--bg` with a `--border` edge
  that turns `--accent` on focus.

---

## 5. Destructive actions - MANDATORY

Every destructive or irreversible action (delete, clear, kill, remove) must ask for confirmation through
the shared `ConfirmDialog` before it fires. This rule has no exceptions.

- Never use the browser's `window.confirm` or `window.alert`. They are blocking, unstyled, and cannot be
  themed; the lint of this rule is simply that neither call appears anywhere in `apps/cockpit`.
- The confirm button is the `danger` (red) variant for a destructive action; use `danger={false}` (the
  accent-blue primary) only for a heavy-but-safe action such as a rebuild.
- `ConfirmDialog` owns its own busy and error lifecycle. The confirmed action should let a failure throw:
  the dialog stays open and shows exactly what went wrong (fail loudly), so the person can read it and
  retry or cancel. Do not swallow the error into a fallback.
- Report the result of an action with `StatusMessage` / `useFlash`, never a pop-up.

---

## 6. The one hard rule that outranks style

The browser talks ONLY to the Gateway, through root-relative paths - never to a Director directly, never
an absolute `http`/`https`/`ws`/`wss` URL in client code. This is enforced by `eslint.config.js` and is
a correctness rule, not a style preference. See the Cockpit rebuild docs (epic 967) for the reasoning.
