# Features Overview

DevThrottle (the cc-director desktop app) is mission control for Claude Code: it
runs and supervises many coding sessions at once, embeds a real terminal per
session, tracks git changes, and adds a voice/wingman layer for hands-free use.

This section documents the user-facing features, with screenshots of the running
app. Every feature listed here is backed by source in this repository - the table
below is generated from `docs/features/feature-inventory.yaml`, which records the
implementing file for each one.

## How this page is maintained

These pages are produced by the `/document-features` pipeline:

1. The code is scanned to decide what is implemented (the inventory).
2. A throwaway slot build of the app is launched, dummy sessions are created, and
   each screen is screenshotted by `scripts/capture-feature-screenshots.ps1`.
3. These markdown pages and the screenshots are regenerated together.

Screenshots are committed PNGs under `assets/` (served directly from GitHub - no
build step). Re-run `/document-features` to refresh them after UI changes.

## Feature matrix

Status reflects the code, not intent: **implemented** = the screen exists and its
actions are wired; **partial** = the surface exists but is still being completed.

| Feature | Status | Page |
|---------|--------|------|
| Main Window | implemented | [Sessions and Console](02-sessions-and-console.md) |
| Session List Sidebar | implemented | [Sessions and Console](02-sessions-and-console.md) |
| Embedded Console Tab | implemented | [Sessions and Console](02-sessions-and-console.md) |
| Prompt Input Bar | implemented | [Sessions and Console](02-sessions-and-console.md) |
| Main Toolbar | implemented | [Sessions and Console](02-sessions-and-console.md) |
| Source Control Tab | implemented | [Source Control and Repositories](03-source-control-and-repos.md) |
| Repository Manager | implemented | [Source Control and Repositories](03-source-control-and-repos.md) |
| Clone Repository | implemented | [Source Control and Repositories](03-source-control-and-repos.md) |
| Home / Status View | implemented | [Built-in Panels](04-panels.md) |
| Tools Catalog | implemented | [Built-in Panels](04-panels.md) |
| Connections | implemented | [Built-in Panels](04-panels.md) |
| Scheduler | implemented | [Built-in Panels](04-panels.md) |
| Communications Manager | partial | [Built-in Panels](04-panels.md) |
| Wingman Briefing Tab | implemented | [Wingman and Voice](05-wingman-and-voice.md) |
| Voice Tab | implemented | [Wingman and Voice](05-wingman-and-voice.md) |
| FIFO Full-Screen Mode | implemented | [Wingman and Voice](05-wingman-and-voice.md) |
| Mobile In-Car Voice (FIFO) | implemented | [Mobile and In-Car](06-mobile.md) |
| Mobile Text (FIFO) | implemented | [Mobile and In-Car](06-mobile.md) |
| Mobile Talk (single session) | implemented | [Mobile and In-Car](06-mobile.md) |
| Mobile Transcripts | implemented | [Mobile and In-Car](06-mobile.md) |
| New Session Dialog | implemented | [Key Dialogs](07-dialogs.md) |
| Settings Dialog | implemented | [Key Dialogs](07-dialogs.md) |
| Workflow Editor | partial | [Key Dialogs](07-dialogs.md) |
