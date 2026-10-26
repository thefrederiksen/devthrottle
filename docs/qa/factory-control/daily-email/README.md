# The one daily factory email - rendered against the approved mockup

The owner approved the format on 26 October 2026 (Factories Run Alone mission): the 08:00 factory email with a
"Needs you" section and a line for every factory saying what its runs did.

| File | What it is |
|---|---|
| `mockup-daily-0800.html` | The mockup the owner approved, copied unchanged from `devthrottle_internal/docs/missions/factories-run-alone-2026-10-26/daily-email-mockup.html`. |
| `composed-daily-0800.html` | What `FactoryEmailComposer` produces from the mockup's own sample data (test `Compose_TheApprovedDailyMockupsSampleData_RendersEverySectionOfTheMockup`). |
| `composed-daily-0800.txt` | The plain-text body sent with it. |
| `*-desktop.png`, `*-phone.png` | Both, full page, at 900 and 390 pixels wide (headless Chrome). |

Differences from the mockup, all deliberate:

- Factories are listed in name order, not in the mockup's hand-picked order.
- Each seat's part of a factory's line starts with its run count, for example "Reply Desk (5 runs):", so that
  "4 runs reported nothing" has something to count against. The words after it are the runs' own reported lines,
  verbatim; the mockup paraphrased them ("Sender sent 18 emails").
- The summary says "0 runs did not run", as the 08:00 email always has, where the mockup says "0 did not run".
