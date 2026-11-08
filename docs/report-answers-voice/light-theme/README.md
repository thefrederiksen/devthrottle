# The Something else box and the microphone row take the report's light look

The coordinator's ruling on pull request 4749: match the approved mock-up. Before, the "Something else" box, the
microphone button and the state chip under each box were drawn in the app's dark palette, so on a light report the
box was black with white text. After, they use the mock-up's light values: a white field, dark text, a grey border, a
light grey microphone button and chip. The recording state stays red. The notes tray and the note box float over the
report and keep the app's theme.

Taken on 8 November 2026 with `../proof-run.mjs` against the rig in `docs/missions/dev-reports/proof/`, the apps
built from this branch, in headless Chromium.

| Before | After |
|---|---|
| `before-cockpit-own-recording.png` | `after-cockpit-own-recording.png` |
| `before-cockpit-own-landed.png` | `after-cockpit-own-landed.png` |
| `before-phone-comment-recording.png` | `after-phone-comment-recording.png` |
| `before-phone-comment-landed.png` | `after-phone-comment-landed.png` |

The pieces are fixed light, not taken from the report: their shadow roots are reset with `all: initial` against a
hostile report, so the report's own colours cannot be inherited. On a report drawn dark, the box therefore stays
white.
