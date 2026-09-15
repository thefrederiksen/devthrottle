# Turn detection phase one - the shipped rules scored on the pinned corpus

Work item five, issue #2858. Scored 15 September 2026 on branch `mission/turn-detection-scorer` at
commit `b6648890c`. The commit after it only swaps two literal prompt glyphs in the scorer's source
for ASCII escape sequences. The scorer was rerun on that build, and its whole printed output was
byte-for-byte identical to the run quoted here.

## Read these three things before any number

1. **The body split is a guess, and it controls the result. The corpus has NOT scored the rule that
   actually ships.** A saved screen does not record where the cursor was. Production splits the
   screen at the real cursor. The scorer has to guess where the input box is: it takes the last row
   that starts with a prompt glyph and treats everything above it as the conversation. Of 8,656
   screens, only 1,674 (19.3 percent) had exactly one such row. 2,404 (27.8 percent) had none, so
   the whole screen, footer included, became the body. 4,578 (52.9 percent) had several, and the
   last one was taken. **The guess had nothing to go on, or had to choose, on 6,982 screens: 80.7
   percent.** The 1,674 matches the earlier measurement's figure exactly, which is evidence that
   the scorer reproduces the harness's guess. It is not evidence that the guess is right.
2. **The labels are behaviour classes, not causes.** `short-unexplained` means blue lasted eleven
   seconds or less with no submission to explain it. That is a shape the rule being replaced
   produces. `long-unexplained` means blue lasted over two minutes with no submission. Nothing in
   the records says a screen repainted. Nothing in the records says anything came back from
   anywhere. The tables below report what was measured against those two durations, and nothing
   more.
3. **This is a regression gate, not proof.** It can catch a change that stops holding the short
   class red or stops opening the long class. It cannot show the new rule is better than the old
   one. It can say nothing at all about the settling window: the two screens in a pair are about ten
   seconds apart and carry no byte timing. **The live shadow comparison on the owner's Director is
   still outstanding.** The owner has ruled that it happens by cutting a release once this lands,
   and that is not this item's to trigger.

## What was run, and how it was checked

The tool is `src/CcDirector.TurnRuleScorer`. It constructs the real rule objects,
`TerminalContentNovelty.RowRule` and `TerminalContentNovelty.SizeRule(threshold)`, and asks them
through `ITerminalNoveltyRule`. That is the same interface `TerminalStateDetector` holds them
through. The row rule gets the real driver's `SelfDescribingRowMarkers` for the agent the manifest
recorded. The tool contains no copy of either rule.

    dotnet run --project src/CcDirector.TurnRuleScorer -- ^
      --manifest <devthrottle_internal>\corpus\state-switching\wakes-2026-09-15.jsonl ^
      --screens  %LOCALAPPDATA%\cc-director\instances\default\turn-review ^
      --sizes 200,400,800

Both paths are arguments with no defaults. The corpus never entered this repository.

- **The corpus is intact.** 6,953 wakes, 4,328 pairs with both screens, **4,328 scored, zero
  corpus misses.** Every one of the 8,656 screens was hashed before it was read, and every hash
  matched the pinned SHA-256. A screen that does not match is reported, left out of every table, and
  makes the run exit non-zero.
- **The scorer's own logic can fail its tests.** The local gate, `.\scripts\test-local.ps1`, passed
  with 8 suites and 1,780 tests, every suite `outcome=Completed`. The result file for
  `CcDirector.Core.UnitTests` contains all 27 new scorer tests. Three reverts were each watched
  going red with the symptom they claim, with the controls still green:
  - raising the near-duplicate similarity to 0.95 failed exactly the two torn-repaint tests;
  - disabling the hash comparison failed the hash-mismatch test (`The collection was empty`);
  - taking the first prompt row instead of the last failed the body-split test.

  The tree was verified clean against the commit after each revert.
- **The three parked suites did not run**, which the gate reported as a coverage gap. That covers
  `CcDirector.Core.Tests`, where `ContentTurnRuleTests` lives, and both Gateway suites. On purpose:
  the mandate rules out a full parked run, because the Gateway lock cannot be won on this machine
  (#2862). The only product-code change here is one `InternalsVisibleTo` line in
  `CcDirector.Core.csproj`. Nothing under the Gateway changed.

## The numbers - every agent together

The four columns are: short-unexplained held red, long-unexplained opened, explained opened (the
negative control), and grey opened (blue between eleven seconds and two minutes, unexplained).

| rule | short-unexplained held red | long-unexplained opened | explained opened | grey opened |
|---|---|---|---|---|
| today: any byte opens | 0 / 1,332 (0.0%) | 231 / 231 (100.0%) | 1,975 / 1,975 (100.0%) | 790 / 790 (100.0%) |
| **row** | **1,250 / 1,332 (93.8%)** | **229 / 231 (99.1%)** | **1,914 / 1,975 (96.9%)** | 310 / 790 (39.2%) |
| **size, 200 characters (shipped start)** | **1,184 / 1,332 (88.9%)** | **230 / 231 (99.6%)** | **1,910 / 1,975 (96.7%)** | 319 / 790 (40.4%) |
| size, 400 | 1,267 / 1,332 (95.1%) | 228 / 231 (98.7%) | 1,876 / 1,975 (95.0%) | 296 / 790 (37.5%) |
| size, 800 | 1,284 / 1,332 (96.4%) | 217 / 231 (93.9%) | 1,793 / 1,975 (90.8%) | 262 / 790 (33.2%) |

The aggregate is mostly one agent on one machine. Of the 4,328 pairs, 3,621 are Claude Code, 633
Codex, 55 Pi, 8 Grok, 5 raw command line, 4 Copilot, and 2 have no agent recorded.

## The same table per agent

**Claude Code** (3,621 pairs)

| rule | short held red | long opened | explained opened | grey opened |
|---|---|---|---|---|
| any byte | 0 / 1,289 | 225 / 225 | 1,736 / 1,736 | 371 / 371 |
| row | 1,207 / 1,289 (93.6%) | 223 / 225 (99.1%) | 1,706 / 1,736 (98.3%) | 291 / 371 (78.4%) |
| size 200 | 1,142 / 1,289 (88.6%) | 224 / 225 (99.6%) | 1,702 / 1,736 (98.0%) | 294 / 371 (79.2%) |
| size 400 | 1,224 / 1,289 (95.0%) | 222 / 225 (98.7%) | 1,678 / 1,736 (96.7%) | 276 / 371 (74.4%) |
| size 800 | 1,241 / 1,289 (96.3%) | 215 / 225 (95.6%) | 1,645 / 1,736 (94.8%) | 249 / 371 (67.1%) |

**Codex** (633 pairs)

| rule | short held red | long opened | explained opened | grey opened |
|---|---|---|---|---|
| any byte | 0 / 41 | 4 / 4 | 174 / 174 | 414 / 414 |
| row | 41 / 41 (100%) | 4 / 4 | 173 / 174 (99.4%) | 17 / 414 (4.1%) |
| size 200 | 40 / 41 (97.6%) | 4 / 4 | 173 / 174 (99.4%) | 21 / 414 (5.1%) |
| size 400 | 41 / 41 (100%) | 4 / 4 | 168 / 174 (96.6%) | 17 / 414 (4.1%) |
| size 800 | 41 / 41 (100%) | 1 / 4 (25%) | 127 / 174 (73.0%) | 12 / 414 (2.9%) |

**Pi** (55 pairs). No short-unexplained pairs at all.

| rule | long opened | explained opened | grey opened |
|---|---|---|---|
| any byte | 1 / 1 | 51 / 51 | 3 / 3 |
| row | 1 / 1 | 31 / 51 (60.8%) | 2 / 3 |
| size 200 | 1 / 1 | 31 / 51 (60.8%) | 3 / 3 |
| size 400 | 1 / 1 | 27 / 51 (52.9%) | 2 / 3 |

**Grok** (8 pairs) and **Copilot** (4 pairs): **every pair carries an empty capture. These tables
are not evidence about either agent.** See below.

**Raw command line** (5 pairs): row and size 200 open 1 / 1 long and 4 / 4 explained. Two pairs with
**no agent recorded**, both grey: the row rule opens neither.

## What the numbers establish

- **The shipped rule reproduces the published measurement.** The row rule opens 229 of 231
  long-unexplained pairs, exactly the published figure. It holds 93.8 percent of short-unexplained
  pairs red, against the published 94.0 percent. That 94.0 was taken with a regular expression
  where the product uses a per-agent marker list, so the two were never going to agree to the
  decimal. The C# port of the similarity function and the two conditions around it score as the
  Python did.
- **The shipped size threshold is not the reviewer's.** At its starting 200 characters, the size
  rule holds 88.9 percent of the short class red, not the 97.0 percent quoted for a size threshold.
  The code already says the 200 is unvalidated and not that number. Now there is a measurement of
  it. On this corpus, 400 comes closest to the row rule's trade: more of the short class held (95.1
  against 93.8), a little less of the control opened (95.0 against 96.9), and one fewer long pair
  opened. By 800, both the long class and the control are losing real ground.
- **For Claude Code the two candidates cost about the same on the control.** The row rule misses 30
  of 1,736 explained pairs and the size rule at 200 misses 34. Neither is free: those are wakes a
  submission explains, where the rule says nothing was gained.
- **Codex barely shows the problem the rule fixes.** 41 short-unexplained pairs against 633, and
  both candidates hold essentially all of them. Codex's large grey class (414 pairs) opens only 4 to
  5 percent under either candidate. The data does not say why, and this report does not supply a
  reason.

## What they do not establish, named

- **Which candidate to turn on.** On the corpus the two sit within a couple of points of each other,
  and which one is ahead depends on the size threshold. The body split beneath both is a guess on
  four screens in five. Production has the cursor and guesses nothing, so the ranking here can
  change on live bytes. The recommendation belongs to the shadow comparison, and it is outstanding.
- **Anything about Grok or Copilot.** 24 of the 8,656 pinned screens have no rows at all. The 12
  pairs they sit in are every Grok pair and every Copilot pair. An empty pair scores as "gained
  nothing" for every candidate and "opened" for the byte rule. In a table, that looks exactly like a
  rule suppressing a real reply. It is not. Nothing was saved. The scorer counts these, names them,
  and prints a warning at the top of each affected agent's table. They are not dropped, because
  dropping them would mean choosing which pinned pairs count. **Why turn-review saved empty screens
  for these two agents is a separate question, and it was not investigated here.**
- **That the rule fails Pi.** Of Pi's 20 explained pairs the row rule does not open, **15 have whole
  screens whose non-blank text is identical before and after.** No rule that reads the screen could
  open those, whatever the rule. Of the remaining 5, 50 of Pi's 51 explained screens have no prompt
  glyph, so the whole screen was used as the body. Pi also has no marker list, which is the shipped
  default for every agent the evidence did not cover. A 60.8 percent control on 51 pairs is a
  question for the shadow run, not a finding against the rule.
- **The settling window.** The corpus carries no byte timing.
- **Causes.** No row of the manifest records a cause. The long class is defined by duration and
  nothing else.

## The four fixtures that crossed into the repository

`src/CcDirector.Core.Tests/TestData/turn-screens/`, linked into `CcDirector.Core.UnitTests`: a
repaint, a torn repaint, a ticking clock and a real reply. Each was modelled on a real pair found in
the corpus, keeping its shape and mechanism. **Every line of prose was written by hand for the
fixture, and no row of any real screen is in them.** That is a stricter reading of "hand-redacted"
than removing names from a real capture. It was chosen because it is the only reading where
"certain it is clean" is a fact rather than a judgement. Their `README.md` says what was kept and
what was replaced. The torn repaint is the one that matters. Its changed row gets past the
substance floor, the marker list and the exact-key test. Only the 0.80 near-duplicate filter holds
it, and its test asserts that state as well as the verdict.
