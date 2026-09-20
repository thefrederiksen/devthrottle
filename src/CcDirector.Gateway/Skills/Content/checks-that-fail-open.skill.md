# Checks That Fail Open

**A check FAILS OPEN when its pass condition is an ABSENCE, and FAILS CLOSED when its pass condition is a specific PRESENCE.**

That is the whole law. Doing nothing at all satisfies an absence. Doing nothing produces the wrong value against a presence.

Written up 2026-08-07 after this fleet hit the same defect eight times in one day, in eight different media, and nobody recognised it as one thing until they were listed side by side. The ninth will be somewhere nobody predicted, which is why this is a skill and not an issue.

## The root

**Every instance is a measurement standing in for the thing it measures.**

| The measurement | What it stood in for |
|---|---|
| "Delivered" | received |
| a working spinner | doing the work you asked for |
| a count | the list |
| a tail | the whole output |
| a comment | the code |
| ancestry (`git branch --merged`) | the work landed |
| "no defects posted" | reviewed |
| "the bad string is gone" | the true string is present |
| an empty roster row | nothing to say |
| an error category | the answer the component actually produced |

And the honest reason it goes unnoticed: **the tool usually makes the weaker claim cheap and the stronger claim effortful.** `message send` reported delivery in one command; the blocking ask (since removed) waited for the agent's own answer. Two careful sessions used only the first, all night, without noticing. Today `message send` answers only "queued", and the stronger claim is a reply you asked for with `--reply-wanted` and read from your inbox.

## The eight that cost a day

1. **A sweep that searched nothing.** Run by absolute path from outside a repository, `git grep` errored, the return code was ignored, and it printed `files searched: 0 / UNANNOTATED CLAIMS: 0` and exited 0 - certifying the whole repository while searching nothing.
2. **An `awk` range that never matched.** It printed no delete-shaped calls and looked exactly like proof - inside a writer's own verification of a fix to an overclaim.
3. **`git grep` with a leading slash, on Windows.** Git Bash rewrites a leading-slash argument to a Windows path before git sees it, so `/docs/start` arrives as `C:/Program Files/Git/docs/start`. **No error, no warning, exit 0.** A sweep run exactly as written in the plan reports a clean pass having searched for the wrong string. Guard with `MSYS_NO_PATHCONV=1`, drop the slash, bracket it as `"[/]docs/start"`, or use ripgrep.
4. **A guard that cried wolf on prose ABOUT the defect.** A grep for a bad address also matched the comment discussing that address - so it fired on exactly the file it existed to guard, teaching whoever ran it to wave the hit away.
5. **A range anchored on a heading.** Rename the heading and the range yields nothing, the grep prints zero lines, zero lines contain no bad string - a pass, while the check ran over nothing.
6. **A review.** Three review seats died in one night; one had completed its review and recorded the verdict NOWHERE. "No defects were posted" is an absence, and is satisfied by a review that never ran.
7. **A success test reading a truncated tail.** `Delivered to <long name> (1 session(s)).` wraps; `| tail -1` showed only `session.`; the test looking for `Delivered` failed, and four identical messages all landed while the check said none had.
8. **A shell that ate the content.** A `gh pr comment --body` containing a backticked filename had it command-substituted away. `gh` posted, returned a URL, and exited 0 - and the published sentence was missing the two words it existed for. **A tool that returns a link is not a tool that returned your content.** Use `--body-file` with a quoted heredoc.

## The repair, which is always the same

**Do not fix an absence-shaped check with more care. Restate it as a presence.**

**And the presence must not filter on the property the defect would fail.** This qualifier is
load-bearing, and it was learned by watching the law applied mechanically produce a new hole.
Restating "the stylesheet is stamped" as the specific presence
`count(stamped URLs) == count(stamp placeholders in the template)` is a presence, it is specific,
and it CANNOT FAIL: add a file that was never stamped at all and both numbers are unchanged. The
failing case sits outside both sides of the comparison. **A presence filtered on the property
under test is the same hole with a number in front of it** - see the count-shaped section below.

**The presence that works is a DERIVED enumeration with a per-item assertion**: not "four URLs
are stamped" but "here is every static URL found ON THE PAGE ITSELF, and each one ends with the
stamp".

**DERIVED is the whole of it, and it is what reconciles this with "an enumeration is an
absence claim wearing a list's clothes" under the table.** An enumeration you HAND-KEEP is
exactly that - it is true only if
exhaustive and it cannot prove its own exhaustiveness, and the hand-kept list in the
count-shaped section below went stale exactly that way: it named three files while the
template stamped four. An enumeration DERIVED
from the artifact under test - every URL the rendered page actually contains, every input tag
the template actually declares - is exhaustive by construction, because the thing being checked
is the thing being enumerated. **If you had to type the list, it will go stale. If the check
reads the list off the artifact, it cannot.**

| Fails open | Fails closed |
|---|---|
| "no bad string in the output" | "exactly these two lines, and here they are" |
| "zero unannotated claims" | "N files searched, and here they are" |
| "no defects were posted" | "PASS - here is what I read, and what I could not reach" |
| "nothing currently writes to it" | "no write route exists - here is the search" |
| "its only handlers are X, Y, Z" | "no `OnPaste`, no `PasteEvent`, no Ctrl+V branch - here is the search" |
| "the bad string is gone" | "the corrected string reaches the built artifact" |
| "`git branch --merged` says merged" | "the changed files are byte-identical to `origin/main`" |
| "N of them carry the stamp, and N is what I expected" | "here are ALL of them, and each one carries it" |

**An enumeration is an absence claim wearing a list's clothes** - true only if exhaustive, and an enumeration cannot prove its own exhaustiveness. One page asserted five event handlers where there were six: the conclusion was right and the evidence was wrong, which is the worst combination, because it survives review.

## Five rules that fall out of it

1. **State all three arms.** A reader assumes two outcomes. There are three: the expected result is a pass, a bad result is the defect, and **an EMPTY result is a repair job on the instrument - never a clean run.**
2. **Quote the actual output, never the word "passed".** A load-bearing count is only load-bearing if it is visible in the record.
3. **Reconcile a sweep against its own expected inventory.** Name what you expect it to find FIRST, run it, then compare. A count with nothing to compare against proves only that the tool ran - and a truncated sweep is indistinguishable from a clean file. One sweep covered 23 of 37 and reported clean; it was caught only because the missing items were known to exist.
4. **A control proves the check RAN, not that it reached FAR ENOUGH.** A line count is a control for TRUNCATION, not for SCOPE. An event, a callback, a hook, a registration - any fan-out is a REACH problem, and only enumerating the subscribers answers it.
5. **Run it against a known-BAD input.** A check only ever run against the state you hope passes has demonstrated nothing. Prove it FIRES as well as passes.

## The subtler sibling: a claim that is RIGHT resting on evidence that is WRONG

Harder to catch, because **the conclusion survives scrutiny precisely because it is correct - so nobody re-examines what it stands on.**

Seen four times in one day, every one found only because a reviewer opened the cited range: a correct negative proved by an incomplete enumeration; a correctly narrowed claim citing a comment about a different class; a correct exoneration resting on a comment saying so; and a real experiment with a correct conclusion whose one wrong detail would have argued for the wrong practice **while citing the experiment as proof**.

A wrong conclusion gets argued with. A right conclusion with false evidence gets agreed with, cited, and inherited - and the falsehood travels inside it.

**Agreeing with a conclusion is not a reason to skip its citation - it is the situation where skipping is most likely and most costly.** And when you cannot find evidence for something you believe is true, **say less** rather than reaching for the nearest plausible citation.

## Applying it to review, which is where it costs most

- Post findings to the pull request **as you go**, and post a **PASS** there too, naming what you checked AND what you could not reach. A pass with no stated scope cannot be judged by anyone who was not there - and everybody who was there is exactly the population that disappears.
- **Name the artifact, not just "durably".** The tiers are not equal: a pull request comment beats a pushed file beats a committed file beats a local one beats a message. "Durably" with no named target lets someone pick the weakest rung and believe they complied.
- A verdict on file that covers a commit which is no longer the head **is not a verdict on the head**. A verification has a timestamp.
- **A comment describing a fix is itself a claim, and goes stale like any other text.** A durability rule manufactures this surface: telling a fleet to write everything down produces comment blocks, inventory rows and defect narratives, all of which read as commentary and none of which is exempt. The rule ships with the leg-hunt attached.

## The COUNT-SHAPED check, and three siblings (2026-08-20, new-studio maps)

Four more, in one evening, between two sessions. The first three are one shape and it is not
covered above: it is not an absence, it is a **comparison of two numbers where BOTH SIDES FILTER
ON THE PROPERTY THE DEFECT WOULD FAIL**, so the failing case sits outside both sides and is
invisible to the comparison. The check is a presence, it is specific, and it still cannot fail.

- A hand-kept list naming three stamped static files while the template stamped FOUR. The fourth
  arrived with a later feature and nobody updated the list; the test passed on the three it knew.
- `assert page.count(stamp) == template.count(stamp_placeholder)`. Add a fifth static file with
  NO stamp and both numbers stay four - counts agree, test passes, page ships an unstamped file.
  **Measured ALIVE** by mutation before the repair.
- `assert page.count("data-default") >= 2` on a slider-reset control. It counts only the sliders
  that HAVE a default, so a slider with none is invisible and never gets reset. **Measured ALIVE.**

**One sentence: a test checking a NUMBER ABOUT the thing instead of the thing.** The repair is
always to enumerate and assert the property of EACH item - every static URL carries the stamp;
every range input carries a default.

**How the third was found, which is the transferable part.** Not by re-reading, and not by
grepping for `count(`. By searching the suite for the DEFECT'S SHAPE: *which assertion compares
two numbers where both sides filter on the property the defect would fail.* Searching by the TOOL
(`ast.walk`, `rglob`, `count(`) finds duplicates of a mechanism. Searching by the SHAPE finds the
same defect in code that shares no tool with it - a stamp count and a slider count have nothing
in common but the shape. **When you fix one instance, search for its shape before you close it.**

Two other count-shaped assertions in the same window were put to the same mutation and CAUGHT it.
Say so. "I examined three and fixed one" and "I fixed one" are different claims.

### A guard whose blind spot is ALIGNED WITH ITS OWN SUCCESS CONDITION

A syntax-tree guard stopped test files building their own template environment, but scanned only
`test_*.py`. `conftest.py` is exactly where a helper LANDS when somebody tidies it out of a test
file - so the guard went blind at precisely the moment its own success condition was being met.

**Ask of any guard: when this succeeds, where does the thing it forbids MOVE TO - and can the
guard see there?** A blind spot pointed at your own success is worse than a random one, because
it opens exactly when the guard appears to be working.

### Prose that contradicted the code IN THE SAME BREATH

The skill's closing law - *prose about a rule is not the rule* - has a sharper form. That finder's
docstring claimed an aliased import was found. True of `import jinja2 as j`; FALSE of
`from jinja2 import Environment as Env`, which binds a different name it never matched. **Not
drift. It was never true**, written wrong alongside the code it described, and no suite anywhere
could have said so.

Unrun prose DRIFTS from the code and someone eventually notices. Prose that was false when written
has no moment of divergence to notice. **Write the reach as a TEST first and the sentence second.**

The executable form: a test named `..._and_that_is_declined` that asserts the CURRENT behaviour of
each known gap, plus a note that if it goes red the guard GREW and the note should be deleted
rather than repaired. **The line for closing a gap versus declining it is ACCIDENT versus
EVASION** - who would do this, and why - rather than what a parser can reach.

### Reading a tool's PROSE when it has an exit code

Instance 7 above is `| tail -1` truncating a delivery message. This is its general
form, and it is worth separating because the repair is one word rather than a habit.

Sweeping nine containers to prove a fixed guard fired on the stale ones, the harness
parsed pytest's own output with `tail -2 | head -1` and read the PROGRESS line
(`.` / `[100%]`) as the summary. It reported the guard firing on all nine, including
the three that were correct - which would have sent somebody to "fix" a guard that
was already right. It did not error. It produced a plausible wrong answer.

It was caught only by contradiction with a full run done minutes earlier. **That is
not a method, it is luck**, and it does not scale to the checks nobody happens to
have a contradicting run for.

**The rule: if the tool sets an exit code, read the exit code.** `pytest`, `git`,
`grep`, `curl`, `docker` all do. Parsing their human-readable output is choosing the
weaker signal when the stronger one is one character away - the skill's own root,
that the tool makes the weak claim cheap and the strong claim effortful, except here
the strong claim is CHEAPER and gets skipped from habit.

| Weak, and what breaks it | Strong |
|---|---|
| `grep -c passed` on pytest output | `pytest ...; echo $?` |
| `tail -1` on anything that wraps | the exit code, or the whole output |
| "the word ERROR does not appear" | the exit code, plus what it printed |
| a JSON tool piped through `head` | parse the JSON |

And when there is genuinely no exit code - a message tool, an API that always
returns 200 - **do not parse the summary line. Read the artifact back.** Fetch the
published page, re-read the row, list the assets. The write's own report of itself
is the thing under test, not the evidence for it.

### "Each of N does X" answered by counting matches of ONE SPELLING of X

Checking a claim that all three syntax-tree guards carried a can-it-actually-see-one self-test, a
session grepped for one spelling and matched one of three - a FALSE NEGATIVE. The other two spell
the same idea `test_an_assignment_counts_and_an_import_does_not` and
`test_the_scan_finds_the_imports_that_are_there`.

**When the claim is "each of N does X", ENUMERATE the N and inspect each**, because X is spelled
differently by whoever wrote it. Counting matches of one spelling is the count-shaped defect again.

And the meta-lesson, which is why that one is in here at all: **a claim that is right by ACCIDENT
teaches nothing and leaves the broken method in place.** Being right because the check worked and
being right while the check was broken look identical in the report. The second is worse than
being wrong, because being wrong gets fixed. It is the mirror of the RIGHT-claim-on-WRONG-evidence
section above, seen from the checker's side rather than the author's.

## An extraction whose END is arbitrary (2026-08-21, new-studio train map)

Different from the range anchored on a heading in the list of eight above, and it is worth
keeping both: there the range failed to match and yielded nothing, which at least LOOKS empty.
Here the range MATCHES and returns text - text that exists nowhere in the file.

```bash
sed -n "/sized like the content/,+14p" UI_STYLE_GUIDE.md
```

The start is anchored on structure. The end is `+14`, a distance. Fourteen lines happened to
cross a section boundary, so the output spliced the tail of one paragraph onto the head of
another and read as a single fluent sentence:

```text
...the movement of the element BELOW it, which must be
already rejects. A single-section screen gets the skeleton...
```

Both halves are real. The sentence is not. The reader was one message away from reporting a
freshly-merged document as containing garbled prose, on evidence their own command had
manufactured.

**An extraction whose end is an arbitrary distance can manufacture a plausible reading that
exists in no file.** A truncation you can SEE is a nuisance; a splice you cannot see is false
evidence, and it is worse the more fluent the result.

**Anchor BOTH ends on structure, or read the whole thing.** `sed -n '/^## 8/,/^## 9/p'` has an
end that means something. `+14` means "as far as I guessed".

The general form is the same root as everything else here: the window stood in for the document.
And the tell is the one below - `+14` is a specific value, so it is a hand-kept list of one.

## The CATEGORY standing in for the ANSWER (2026-08-21, new-studio train map)

A screen pointed at a wide log returned `HTTP 500`. It was not a crash. The renderer had
DECLINED, deliberately, above a threshold of `len(model.stations) > 60`, and had written the
reader a sentence with the fix in it - verbatim from the source and confirmed identical in the
live response:

```text
Refusing to lay out 135 stations - the result would be a hairball that
discredits the data. Fold the activity vocabulary harder (see
abstract(fold=...)) or restrict object_types.
```

What reached the reader was `Error: HTTP 500`.

The log was the German Bundestag object-centric dataset, period 20
(DOI 10.5281/zenodo.16811928, CC-BY 4.0): 62,719 events, 44 object types, 148 event types. The
135 is what THIS log produced, not a constant.

**A route that turns a deliberate, actionable refusal into a generic failure has thrown away the
only useful thing on the screen.**

It belongs in this skill because **no check anywhere was failing, and every layer was locally
correct.** The engine correctly refused and said why. The client correctly declines to render a
5xx body, because a 5xx body is a stack trace and not a message for a reader. The route
"correctly" reported an error for an exception. Three right answers compose into a screen that
tells the reader nothing.

It is the root of this skill with the CATEGORY standing in for the ANSWER. A status code, an
exception class, an error enum, a log level - each is metadata ABOUT an answer, and a pipeline
that propagates only the metadata destroys the answer while every component reports success.
The observable was present; the presence was of the wrong thing.

**The defect lives in the SEAM, and nothing tests seams.** The presence that fails closed is
end-to-end and is about content, not status: assert that the SENTENCE the component wrote
reaches the rendered page. Not that the call returned. Not that the status was appropriate.

### The half that transfers furthest: an instance fix is a hand-kept list of one

**That defect had already been fixed once, on that same route, and shipped as done.** On
2026-08-20, in `def train_map_canvas`, a single `except _trainmap.TooFewObjectTypes` was added,
returning an `HTMLResponse` with no `status_code` - which is 200. Every other refusal the engine
can write went on failing for another day, and it surfaced only when somebody finally pointed a
44-object-type log at the screen - the sample could not produce a refusal at all.

Teaching ONE exception type to answer correctly is **a hand-kept enumeration at its smallest
possible size**, which this skill already says goes stale and cannot prove its own exhaustiveness.
At length one it is almost invisible, because it arrives with a passing test beside it and looks
exactly like a fix. The derived repair drives the exception TYPE through the route, so every
refusal the engine can write is covered by construction rather than by a list somebody maintains.

**The tell, and it is cheap to check: a fix that names a specific VALUE is a hand-kept list of
one.** One exception class, one status code, one message string, one column name, one file. When
you see that shape, the question is not "is this fix correct" - it usually is - but **"what class
is this an instance of, and what else is in it?"**

Fix the class, or say in the commit that you knowingly fixed one instance and why. An instance fix
with no such note is indistinguishable from a class fix six weeks later, and the difference is a
day of somebody else's time.

### A postscript, because it happened while this entry was being written

The four facts above were taken from a chat message and were about to be published unverified -
into this skill, in the section about correct conclusions resting on evidence nobody checked. They
were sent back to be read off the branch instead. **Three were right. The wrong one was the
verbatim quote**, which is the single thing in an entry like this that most needs to be exact.

The parenthetical `(see abstract(fold=...))` had been dropped, and it had been dropped in THREE
places - the message, the commit message, and the author's own timings document - because it was
copied forward from the first write-up rather than re-read from the source each time. Quoted
whole, the sentence carries a second finding for free: a developer-facing API hint sitting in the
middle of reader-facing prose. Quoted as it had been copied, it reads as clean product writing,
which is the opposite of true.

**Copying a quotation forward is not quoting. Re-read the source every time, including from your
own earlier write-up** - especially from your own earlier write-up, because that is the copy you
trust without noticing you are trusting it.

## SELECTING THE WRONG LINE, which returns something complete and false (2026-08-21, new-studio train map)

Different from the truncated tail in the eight, and the difference is the whole
point. That one LOST content - `Delivered to <name> (1 session(s)).` wrapped and
`tail -1` showed `session.`, which is visibly a fragment. This one returns a
whole line, correctly formatted, that is not the line you wanted and carries no
warning that it is not.

**Produced deliberately for this entry**, on a real suite with a planted failing
test, rather than written from memory of it happening:

```
$ python -m pytest tests/test_train_map_edges.py -q > run.log ; echo $?
1

$ tail -2 run.log | head -1
FAILED tests/test_train_map_edges.py::test_deliberately_failing_...
```

So far so good, by luck. Now the same command on a run that PASSES:

```
$ python -m pytest tests/test_train_map_edges.py -q > run.log ; echo $?
0

$ tail -2 run.log | head -1
-- Docs: https://docs.pytest.org/en/stable/how-to/capture-warnings.html
```

**A documentation URL, presented as a test result.** Nothing is malformed. The
line that `-2` lands on depends on whether pytest emitted a warnings block, so
the reading's meaning changes with the weather. Search that for `failed`, find
nothing, call it green - right by accident. Search it for `passed`, find nothing,
call it red - wrong, on a suite that passed.

### And the short forms are worse - in OPPOSITE directions

All four cases, run rather than reasoned about. Six tests, one `warnings.warn` so
the warnings block exists, one test deliberately NAMED `test_five_handles_failed_login`,
and `assert 1 == 2` planted for the failing rows:

```
case            exit   grep -c passed   grep -c failed   LAST line matching passed|failed
all pass, -q      0          1                0          6 passed, 1 warning in 0.08s
all pass, -v      0          1                1  <-!     ====== 6 passed, 1 warning in ...
one fail, -q      1          1  <-!            1          1 failed, 5 passed, 1 warning ...
one fail, -v      1          1  <-!            2          ====== 1 failed, 5 passed, ...
```

**`grep -c passed` is 1 in ALL FOUR.** It does not distinguish anything. The
summary of a failing run reads `1 failed, 5 passed, 1 warning` - the word `passed`
is inside the failure - so a grep for the good news matches the bad news, always.

**`grep -c failed` is 1 on a fully PASSING verbose run.** The line it matches is:

```
test_probe.py::test_five_handles_failed_login PASSED                     [ 83%]
```

A test NAME. Under `-q` pytest never prints a passing test's name, so there is
nothing to match and the trap is invisible; under `-v` it fires. Which of the two
short forms is wrong therefore depends on **a verbosity flag nobody thinks of as
part of the check** - and one of them is wrong either way.

They fail in opposite directions, which is worse than either alone:

- `grep -c passed` **fails open** - good news on a broken run.
- `grep -c failed` **false-alarms** - a failure count on a run where everything
  passed. A team that meets that a few times learns to wave the count away, which
  is entry 4 of the eight arriving by a different road.

### The cases OUTSIDE those four rows, where every reading says clean

The four rows above all assume the suite RAN. Reproduced across two machines,
here is what happens when it did not:

```
case                                 exit   c:passed  c:failed   LAST passed|failed line
collection error (syntax error)        2        0         0      (empty)
no tests at all (bare directory)       5        0         0      (empty)
-k matching nothing, real suite        5        0         0      (empty)
file present, no test functions        5        0         0      (empty)
-k matching SOME, real suite           0        1         0      1 passed, 5 deselected
```

The real summaries are `1 error in 0.31s`, `no tests ran in 0.00s` and
`6 deselected in 0.01s` - **none of which contains the word `passed` or the word
`failed`**.

So `grep -c failed` returns **0 - clean** for a suite that never ran. That is this
document's headline defect, committed by the check people use to police it.

The realistic instance is the third row: a CI step filtering on a test name, the
test gets renamed, the filter matches nothing, and the step runs zero tests
forever while every prose reading reports clean. Nobody edits that step again.

The sound form - the last line matching `passed|failed` - returns an **empty
string** in the first four. That is rule 1 of this document: an empty result is a
broken instrument, never a clean run. It is sound inside the four rows and SILENT
outside them, and silence is what a caller reads as fine.

### The exit code, corrected twice

The first version of this entry said the exit code is "0 and 1, correct in all
four". True of the four cases that had been run, and **too narrow** - which is the
same defect as everything else here, applied to the advice.

Pytest uses **2** for a collection error and **5** for no-tests-collected. A
script asking `if exit == 1 then failed` calls a suite that never ran
not-failed.

**Exit 0 is the only success. Every non-zero is not. Never test for 1.**

**And that is still not sufficient**, which the last row proves:

```
$ pytest test_probe.py -q -k test_one ; echo $?
1 passed, 5 deselected in 0.06s
0
```

**Exit 0. Five of six tests never ran.** Every check named in this entry passes
that: exit is 0, `grep -c failed` is 0, and the sound form returns a real summary
rather than an empty string. A filter whose name has drifted lands here rather
than on exit 5 the moment it still matches ONE thing.

The complete form is this document's own rule 3 - reconcile a sweep against its
expected inventory - pointed at its own test suites:

**exit 0 AND the collected count reconciled against what you expected to find.**

Exit 0 says nothing failed. It does not say your 812 tests ran.

### The same shape in the skill cache itself

Checking whether an entry had landed, this skill's own local copy at
`~/.claude/skills/checks-that-fail-open/SKILL.md` was a version behind. Searching
it for a section that had just been published returned nothing, and the next step
would have been to report the section missing - from a v4, about a v5.

**A local cache of a shared document is a hand-kept copy that looks
authoritative and carries no version on its face.** Fetch from the source before
saying a section is absent. Same shape as copying a quotation forward: the copy
you trust without noticing is your own.

---

## Care is not the mechanism. Reproduction is.

Two sessions produced everything in the two 2026-08-21 sections above by refusing
to take each other's word. Every single finding came from the other one declining
to accept a report and running it instead:

- three readings reported -> a fourth case found in reproducing them
- that fourth case reported -> a fifth found, `grep -c passed` constant in all four
- the four-row table reported -> the cases OUTSIDE the four rows found
- the exit-code rule recommended -> its own author found it too narrow, and then
  the reader found a case that defeats the corrected version too

**Both parties were being careful the whole time.** Care is what produces a
confident wrong reading - a quotation copied forward three times, a `+14` window,
a grep for one spelling, an exit-code rule true of the cases its author had run.
None of those is carelessness; each is a competent person going one step less far
than the step that would have caught it.

The practice that caught them is cheap and unpleasant: **do not accept a reported
result into anything permanent, and do not accept your own from an earlier
write-up.** Ask for the command. Run it. Then run the case next to it.

Stated as what it cost rather than as a principle: of eleven findings across one
evening, ZERO were found by the person who made the mistake, and every one was
found within minutes of somebody else refusing to take the report on trust.

---

## And the reason this is a skill

**Prose about a rule is not the rule.** A note saying "check both surfaces" cannot check both surfaces. Three times in one night, somebody broke a rule in the same file where they had just written it - because writing the note FEELS like doing the work.

Every law that held that night held because it was a command somebody could run. Every one that failed was prose. **Ship the check, not the reminder.**
