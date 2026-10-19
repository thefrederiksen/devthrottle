# License alarm frame regression check

Run this check when changing the Cockpit frame, its routed-page sizing or the license banner.

From the repository root, with dependencies installed and a Director-owned test browser attached:

```bash
cc-devthrottle browser list --json
cc-devthrottle browser start <test-profile>
eval "$(cc-devthrottle browser attach <test-profile>)"
npm run test:license-layout
cc-devthrottle browser stop <test-profile>
```

The check builds a local page containing the real Cockpit route table, frame, pages and styles. It replaces network reads, uses no live account and makes no external writes. It derives the frame routes from that table rather than maintaining a second list.

Sessions runs first, with 100 rows. Each route is measured at 1440 and 390 pixels wide. The check requires the one-line banner and its link to be visible, the routed content to start below the banner, and the page to end within the viewport. It also requires the Sessions list to scroll. A deliberately oversized page is the failing control: the same measurement must reject it.

Other pages exercise their empty or failed-read layout. Redirects follow the real route table. Sign-in and standalone report pages are outside the frame and outside this check. This is a frame-layout check, not proof of every page's successful data state or of a signed-in production account.

The generated page and per-route measurements are under `artifacts/license-alarm-layout/`.

## Hotfix check, 19 October 2026

- 49 frame routes at each of two widths: 98 measurements passed.
- The Sessions list contained 100 rows and scrolled at both widths.
- The full-height failing control was rejected.
- The phone application keeps its existing no-banner behavior. The phone-width measurements above are of the Cockpit.
