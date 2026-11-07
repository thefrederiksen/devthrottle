# Mission Button on the Web - quality assurance screenshots

Taken on 7 November 2026 by the Delivery Lead (session 113) on the live Gateway at commit 43172929e, in a signed-in
Chrome on the computer. The full report, with these images and its findings, is published at
https://gateway.devthrottle.com/r/ee5cee8d-05aa-4165-ad32-f1bd7d01b9cc.

Sessions used: 113 is in this Mission (which has a document), 133 is in no Mission, and 163 is in the Factory
Failures Mission (no document yet). The mobile app was opened at phone size (390 by 844) with touch emulation on.

## The Cockpit

- `01b-cockpit-mission-closeup.png` - session header close-up: the indigo Mission button with the Mission's name.
- `01-cockpit-mission-with-document.png` - full page, session 113: the indigo button at the top right of the session.
- `03-cockpit-click-opens-mission-document.png` - the new tab the click opened: this Mission's document.
- `04b-cockpit-no-mission-closeup.png` - close-up, session 133: the yellow "No mission" button.
- `04-cockpit-no-mission.png` - full page, session 133 with "No mission".
- `05b-cockpit-mission-without-document-closeup.png` - close-up, session 163: the indigo outline button.
- `05-cockpit-mission-without-document.png` - full page, session 163 with the outline button.
- `06-cockpit-no-mission-tooltip-bubble.png` - the explanation bubble after clicking "No mission".

## The mobile app

- `07-mobile-mission-with-document.png` - session 113: the indigo button in the session app bar.
- `08-mobile-long-press-tooltip.png` - a long press shows the Mission's name and goal in the bubble.
- `09-mobile-tap-opens-mission-document.png` - the report page a tap opened, at phone width (the right document; the
  report page itself is not readable at that width, which predates this change).
- `10-mobile-no-mission.png` - session 133: the yellow "No mission" button.
- `11-mobile-mission-without-document.png` - session 163: the indigo outline button.

## What the report found wrong

Two problems predate this change: on the phone the license problems banner covers part of the app bar, and the
report page is unreadable at phone width. Two are small and cosmetic in the new button: a long Mission name is cut
off with no ellipsis in the Cockpit header, and the phone bubble runs a few pixels past the left edge of the screen.
