# docs/archive - Frozen historical documents

This folder holds documents that recorded work that is now complete. They are kept for
historical value - to show what was planned, how a migration progressed, and what was handed
off between sessions - but they are NOT live reference and are NOT maintained.

If you are looking for the current state of the system, do not start here. Start with the
live docs in `docs/` (for example `docs/VisualStyle.md` for the current design language and
`docs/cencon/INDEX.md` for the architecture and security index).

## What lives here

- `handovers/` - session-to-session handover notes for work that has since landed
  (the engine handover, the UI redesign handover, the GitHub releases handover, and the
  communication and chat dispatch handovers).
- `plans/` - completed implementation plans (the Avalonia port, the ConPty integration,
  the voice mode plan, and the Mac support plan).
- `trackers/` - progress trackers for finished efforts (the Avalonia migration tracker,
  the tool test tracker, and the LinkedIn enrichment tracker).
- `specs/` - superseded design specifications (the WPF-era design spec, replaced by
  `docs/VisualStyle.md`, and the Avalonia migration spec).

## Rules

- These documents are frozen. Do not update them to match the current system. If a fact here
  is out of date, that is expected - the current truth lives in the live docs.
- Do not delete them. They have historical value.
- Links inside these documents may point at paths that no longer exist; that is acceptable
  for a frozen record.

Moved here by issue #402 (part of issue #395) to keep the live `docs/` tree lean.
