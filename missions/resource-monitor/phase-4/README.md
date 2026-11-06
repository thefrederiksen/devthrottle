# Phase 4 screenshots: Fleet, Machines against a local Gateway seeded with three machines

Taken on devlinux on 2026-11-06 from this branch's own build: the Gateway (Debug), and the Cockpit built with
`vite build` and copied into the Gateway's `wwwroot/c`. Every figure on screen went through the real
`GET /machines` and `GET /machines/{machine}/resources` folds. Nothing was mocked in the browser.

How the three machines were seeded:
- Each one registered a Director with `POST /directors/register` on a self-hosted Gateway
  (`CC_GATEWAY_NO_AUTH=1`, scratch `CC_DIRECTOR_ROOT` and `HOME`, port 7955). The two online Directors were
  heartbeated every 15 seconds.
- **demo-win11 is offline for real.** Its Director was never heartbeated, so after two minutes the Gateway
  calls it "Not heard from", and the capacity fold sends dashes and its reason - and, since the second review
  round, no "Offline" pill of its own, because the reach line already owns that verdict. Its rows stop ten hours
  ago, so its expanded chart shows ten hours of silence up to the Gateway's now.
- `seed-three-machines.py` wrote 24 hours of five-minute rows into the Gateway's statistics file
  (`gateway-stats.db`) for devthrottle-mac-mini and devlinux. The rows have the shape `ResourceSummaryStore`
  writes, but they went straight into the file, not through the hub method. The figures are made up. The
  devlinux rows include an amber processor crossing in the newest window ("CPU hot"), a red crossing, a
  memory amber crossing, twenty minutes where the processor was not read, one window the Gateway refused, and two
  hours (seven to nine hours ago) with no row at all, which the Gateway folds into a gap.
- `screenshots.cjs` drove Chrome headless at 1600 by 1000 pixels, at twice the pixel density.

| File | Shows |
|------|-------|
| 01-machines-three-machines.png | The three machines. demo-win11 shows dashes, never zeros, with the Gateway's reason |
| 02-machines-sorted-by-spare.png | Sorted by Spare, most room first. The machine whose Spare is not known is last |
| 03-devlinux-expanded.png | The hot machine expanded: charts, crossings, the refused window, cost per factory ("at least", because the span holds a refused window) |
| 04-demo-win11-offline-expanded.png | The offline machine expanded: its history stops ten hours ago, and the chart runs to the Gateway's now with the silence drawn as the Gateway's gap |
| 05-mac-mini-expanded.png | The online machine expanded |
