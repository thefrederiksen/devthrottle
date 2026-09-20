# Phase A sweep - where else the rail writes its own sentence about a session

**Asked for by:** the Architect, as part of Phase A. The owner's ruling is wider than the item it
was asked about: *"Nobody should give any local reason for anything. It should always be the
gateway."*

**What this file is:** a report, not a change. Nothing listed here has been touched. Phase A changed
exactly one of these - the colour hover - because that was the item.

**How it was swept:** every tooltip and every text binding on the session rail
(`src/CcDirector.Avalonia/MainWindow.axaml`, the row template and its header), then every
user-visible string property on `SessionViewModel`, then where each of those strings is composed.
Sentences about a CONTROL ("Open or close the sessions under this one", "Your own order - drag the
rows where you want them") are out of scope: they describe the app, not a session.

---

## A. Sentences this Director writes about a session's state

These are the real hits. Each is words the desktop composes itself and shows about a session, with
no Gateway fold behind them - so the rail can say something the Cockpit and the phone do not, and
can say it before the Gateway has judged anything.

| # | Where | What it says | Note |
|---|---|---|---|
| A1 | `SessionViewModel.PendingDeletionTooltip` (`SessionViewModel.cs:395`) | `"Marked for deletion - {reason}"`, or `"Marked for deletion - reaping shortly"` when no reason was given | The rail composes the sentence AND supplies a whole sentence of its own when the fact is missing. The reason itself is a local field (`Session.DeletionReason`); the fold has no words for this state at all. Closest relative of the hover that Phase A just fixed. |
| A2 | `SessionViewModel.HoldTimeLabel` (`SessionViewModel.cs:560`) | `"wakes in 3h 48m"`, `"wakes in <1m"`, `"waking up"` | The Gateway owns the clock (`SnoozeUntil`) and the desktop owns the WORDS. The phone and the Cockpit format the same deadline themselves, so three surfaces each write this sentence. |
| A3 | `SessionViewModel.WaitingDurationLabel` (`SessionViewModel.cs:538`) | `"waiting 11m"`, `"waiting <1m"`, `"waiting 2h"` | Same shape as A2, from `NeedsYouSince`. |
| A4 | `MainWindow.axaml.cs:4902` | `"{n} need you"` in the SESSIONS header | A count the rail derives and a phrase it writes. The Gateway already folds the triage bucket every surface counts from. |
| A5 | `MainWindow.axaml:626` | `"In voice mode - a phone is driving this session by voice"` | A literal in the XAML, about what a session is doing. |
| A6 | `MainWindow.axaml:712` | `"Snooze ended - this session came back on its own and is still waiting on you"` | A literal in the XAML, about what a session is doing. The FACT is the Gateway's (`SnoozeExpired`); the sentence is the rail's. |
| A7 | `MainWindow.axaml:195/215/234` (row template) | The badge words `NOT DELIVERED`, `WINDING DOWN`, `SNOOZE ENDED` | Badge captions rather than sentences, and the Cockpit spells two of them the same way by coincidence rather than by construction. Phase B is already collapsing these into one ranked slot, so it will pass through here anyway. |

**One that looks like a hit and is not:** `SessionViewModel.RoleTooltip` (`:830`) renders the role
name the Gateway resolved. It is a Gateway fact rendered verbatim; the badge goes in Phase B in any
case.

**Two that are dead:** `VerificationStatusText` (`:1037`) and `TerminalVerificationStatusText`
(`:1051`) are locally written sentences about a session - `"Session file not found"`, `"Waiting for
Claude session ID..."`, `"Verification Failed"` - and a whole-repository search finds **no binding
and no other reader**. They are only raised as property changes. They are unreachable prose, not a
live sentence, and are better deleted than moved.

---

## B. The Gateway's words, but compiled in rather than pushed

A second and softer class. These ARE the shared fold's words - written once in
`CcDirector.Gateway.Contracts`, so the three surfaces cannot disagree about what to say. But the
desktop reads them by COMPILING that assembly, not by asking the Gateway, so a Director older than
its Gateway shows the older wording. That is a smaller version of exactly the defect this mission
exists to close, and it is worth naming rather than discovering later.

| # | Where | Words from |
|---|---|---|
| B1 | `SessionViewModel.CrewLineText` / `CrewAgeText` | `SessionTree.CrewSummaryLine`, `SessionTree.CrewAge` |
| B2 | `SessionViewModel.AgentModelTooltip` / `ModelLabel` | `ModelDisplayFold.For` |
| B3 | `SessionViewModel.UndeliveredPromptTooltip` | `SessionOrdering.PromptDeliveryNotice` |

Phase A did not add to this class: the colour hover reads the legend over the wire
(`GET /gateway/session-colours`) precisely so an old build cannot explain a colour with old words.
The only compile-time read it introduces is `SessionColourLegend.SharesAnEntry`, which is a
name-to-name alias, not words.

---

## C. The one gap Phase A itself opens, and it needs the Architect

Phase A left the desktop with **two rendering sentinels the Gateway has no words for**, and neither
can be given words without changing the Gateway - which Phase A excludes.

1. **The magenta sentinel.** The Gateway's legend carries a note about magenta, worded for the
   browser clients: *"Not a state. The screen received a colour this app does not understand, so
   reload and report it if it stays."* On the desktop that is no longer what magenta means - it now
   means the Gateway stamped NOTHING. Rendering that note in the desktop legend window would put a
   sentence on screen that is false about this surface, so **the window leaves the magenta note
   out**, and an unstamped session's hover is **empty** (the Gateway said nothing, so the rail says
   nothing).
2. **The neutral.** There is no legend entry for it at all, because it is not a colour the Gateway
   decides.

Nothing is lost against today - the desktop had no legend at all before this phase, and the hover it
had was the local sentence the ruling removes. But a person can now see two pixels the app cannot
explain.

**Recommendation, for a later phase and not for this one:** give the Gateway's legend a note per
RENDERING sentinel rather than one note for "magenta", each saying what that pixel means and what to
do - so every surface renders whichever ones it paints, verbatim, and the desktop's window and hover
stop being silent about them. That is a Gateway change, and it is the Architect's call.
