---
name: document-features
description: Generate and refresh the public feature documentation in docs/public/features/ - scan the code to decide what is implemented, screenshot the running app, and update the pages. Triggers on "/document-features", "document the features", "refresh feature screenshots", "regenerate feature docs".
---

# Document Features

Keep `docs/public/features/` and its screenshots in sync with the app. This is the
"docs-as-code with regenerated screenshots" pipeline: the inventory is decided from
source, the screenshots are captured from a real (throwaway) build, and the pages
are regenerated together. Re-run after UI changes - never hand-edit screenshots.

## Inputs and outputs

| Artifact | Role |
|----------|------|
| `docs/features/feature-inventory.yaml` | Source-of-truth: each feature, its status, and the file that proves it exists |
| `scripts/capture-feature-screenshots.ps1` | Launches a slot build, makes dummy sessions, screenshots each screen |
| `docs/public/features/*.md` | The published pages (raw markdown on GitHub) |
| `docs/public/features/assets/*.png` | Committed screenshots referenced by the pages |
| `docs/public/index.json` | Manifest listing every page |

## Workflow

### STEP 1: Inventory pass (decide what is implemented)

1. Read `docs/features/feature-inventory.yaml`.
2. Scan the UI source the way `/cencon-generate` reasons over the solution. Look in
   `src/CcDirector.Avalonia/` (views, `Controls/`, `*Dialog.axaml`) and
   `phone/CcDirectorClient/` (phone pages).
3. Reconcile:
   - A user-facing view/dialog that exists in source but is NOT in the inventory -> add it.
   - An inventory entry whose `source` file no longer exists -> remove it (or mark `planned`).
   - A surface that is stubbed (TODO / NotImplemented / framework-only) -> `partial`.
4. Keep every `source:` path repo-relative and real. Keep `id`s stable (they are the
   screenshot stems). ASCII only.
5. Report what changed (new / removed / status-changed features) before continuing.

### STEP 2: Capture pass (screenshot the running app)

Run the harness:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/capture-feature-screenshots.ps1
```

- It builds slot 5, launches via the `cc-director-launch` scheduled task (per
  CLAUDE.md - never spawn the app from this process tree for session-creating runs),
  creates dummy sessions, captures the shots, and tears down ONLY slot 5.
- Screens are captured with `cc-click screenshot --pid <slot pid>`, which uses
  Win32 PrintWindow to grab OUR window's own pixels - occlusion-proof, so it can
  never accidentally capture one of the user's other CC Director windows.
- Run it from an INTERACTIVE desktop session. The main-window and tab shots work
  regardless, but the modal dialog shots (New Session, Settings) only render when
  the app can be foregrounded - a background/non-interactive run reports them as
  missing rather than capturing them.
- Default dummy sessions are plain shells (`-Agent RawCli`) - no Claude subscription
  spent. Pass `-Agent ClaudeCode` when you need real agent output for the
  wingman/voice screens.
- Read the harness summary. It prints which inventory screens were NOT auto-captured;
  those need either a new row in the script's `$Shots` table or a manual capture.

To extend automated coverage, add a row to `$Shots` in the harness (each row is a
screenshot name plus the cc-click navigation to reach that screen).

### STEP 3: Document pass (regenerate the pages)

For each category in the inventory, update its page under `docs/public/features/`:

- Embed each captured screenshot with a relative path: `![Alt](assets/<name>.png)`.
- For a feature whose screenshot does not exist yet, write the text and add a line
  `_Screenshot pending - <how to capture it>._` instead of a broken image link.
- Regenerate the feature matrix table in `01-overview.md` from the inventory
  (Feature | Status | Page).
- Follow the writing style of the existing `docs/public/` pages: one-sentence intro,
  then the detail. No filler, no Unicode.

### STEP 4: Manifest pass

Update `docs/public/index.json` so every page under `docs/public/features/` has an
entry in the `features` category. Validate (same rules as `/update-docs`): every
`file` points at a real file, every page is listed, ids are lowercase-kebab-case.

### STEP 5: Report

Summarize: features added/removed/changed status, screenshots refreshed, screens
still pending. Do NOT commit unless the user explicitly asks (repo rule).

## Maintenance model

- Re-run this skill before a release or whenever the README Features change.
- The CI workflow `.github/workflows/docs-drift.yml` runs only STEP 1's reconcile
  (no GUI) on pull requests and flags when the inventory is stale relative to source.
  It cannot screenshot a desktop GUI on a headless runner - it only nudges a human to
  re-run the full local pass.

## Notes

- Only touches `docs/features/`, `docs/public/features/`, `docs/public/index.json`,
  and the capture script. Never product code under `src/`.
- The desktop window title cc-click matches is "CC Director".
- If a required tool (cc-click, git, dotnet) is missing, the harness fails loudly with
  the fix - do not work around it.

---

**Skill Version:** 1.0
**Last Updated:** 2026-06-19
