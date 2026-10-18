# Factories screen cleanup - screenshots

Taken on 17 October 2026 from the Cockpit's development server, at desktop width (1400 pixels) and phone width
(390 pixels, the side rail collapsed as a person would).

| File | What it shows |
|---|---|
| `before-desktop.png`, `before-phone.png` | The Factories tab on origin/main: the Factory Manager panel first, then New factory, the "No factory is registered yet" sentence, the drafts as underlined links, archived and the schedules outside any factory. |
| `after-live-desktop.png`, `after-live-phone.png` | This branch on today's live data: no factory published yet, so the two drafts lead as cards with a DRAFT pill and their seat count; the Gateway's empty sentence is a footnote under them; archived and the schedules outside any factory sit under a rule. |
| `after-live-manager-desktop.png`, `after-live-manager-phone.png` | The new Factory Manager tab. The card is unchanged. |
| `after-published-desktop.png`, `after-published-phone.png` | How a published factory looks: the count strip, then the factory's card - name, status pill, purpose, seats and next start, last run and how it went, the boss and Talk to the boss - then the drafts, then the secondary part. |

## How the data was made, said plainly

- The development server's Gateway routes went to `rig/relay.py`, a read-only relay that forwards GET requests to
  the live Gateway with a session key and refuses every write. So the archived count, the drafts, their file counts,
  and the schedules outside any factory are the live account's, read at the time of the shot.
- A session key may not read two routes, so the relay answers them itself: the Factories switch (answered "on", as it
  is for this account), and the Factory Manager card, built exactly as `FactoryManagerEndpoints.CardOf` builds it from
  facts a session key can read - no session bound and no computer or folder saved (`cc-devthrottle factory manager
  show`), the running Directors' computers, and the live name check.
- The live Gateway does not have this branch yet, so for the "after" shots the relay adds the fields this branch
  adds. **The draft seat counts ("1 seat") are written by the relay**, not read: a session key may not read a
  draft's definition.
- **Erik Laursen's published factory in `after-published-*` is a stand-in row written by the relay** (one seat, the
  boss, a next start at 08:00 tomorrow and a successful run at 08:00 today). It shows the layout of a published
  factory; it is not his factory's real state. The words follow the Gateway's fold in this branch
  (`FactoriesScreenFold.NextStartText` and `FactoryLastRun`), whose unit tests pin them.
- `before-*` were taken with this branch's changes stashed, so they show origin/main's Cockpit.
