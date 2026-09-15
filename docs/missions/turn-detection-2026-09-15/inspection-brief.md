# Inspection brief - phase one, work items one to four

Give this to the Inspector. The Inspector is a different agent family from the Manager that
built the work - if the Manager was Claude Code, the Inspector is Codex. The Inspector does not
fix anything. It writes its review to a FILE and replies with one single line, because fleet
messages truncate at the first newline.

**Be adversarial, and do not trust the mission's own report.** The build report in this folder
is self-testimony: written by the seat that did the work, about its own work, and exactly as
persuasive as it is unreliable.

## What the work claims

That a settled session now opens a turn when the conversation gained content rather than when a
byte arrived, behind a switch that is off, with the behaviour when the switch is off unchanged
byte for byte.

## The sharp questions

**On the switch being genuinely off.** The claim is that with the switch off the state writes
are byte for byte what they are today. Is the proof a test that would actually fail if the
claim were false, or is its pass condition an absence - "no unexpected write was seen" - which
certifies a run that never happened? Find the assertion and say what specific presence it
checks. Does the off path still read the screen, and if so, on whose thread and at what cost?

**On the similarity function.** The eighty percent near-duplicate threshold is Python
`difflib.SequenceMatcher.ratio`, which is not a longest-common-subsequence ratio; the two
disagree on twenty-eight percent of short pairs. Does the C# implement difflib's greedy
longest-matching-block recursion, or an easier thing that resembles it? Is there a test pinning
exact ratio values against Python's answers, and are those values actually Python's? A wrong
similarity function makes every number in work item five meaningless while every test stays
green.

**On a miss losing a turn.** The review found the first draft would let one filtered burst leave
a working session red indefinitely. Trace it: after a check finds nothing, what guarantees the
rule is asked again? What happens when bytes arrive inside the settling window - are they
dropped, or do they push the check out? What caps the deferral so a continuously chattering
agent is still judged? Construct the sequence of bytes that leaves a working session red and
say whether the code survives it.

**On the continuous-idle path.** That path was folded into the new code. It compared the joined
body for ordinal equality - a one-row shift defeated it - and the body hash beside it fed the
evidence record, not the decision. Did the fold preserve what that path was actually doing for
the one agent that never goes byte-silent, or did it change that agent's behaviour while the
switch is supposedly off?

**On what a constant could hide.** Where could a threshold, a marker list or a delay be replaced
with a different constant and the whole suite stay green? Name each one.

**On unguarded paths.** This code runs on the pseudo-terminal producer thread. What happens when
the screen snapshot throws, when the settled body was never captured, when the rows are empty,
when the cursor is at the top? Is any of that a swallowed exception that reads as "no content
gained"?

**On the claims in the comments and the report.** Read every sentence that asserts something is
proven, and check it against the code beneath it. An unproven claim in a comment is worse than
no comment, because the next reader spends their scepticism somewhere else.

## Where to read from

`origin/main` for the shipped code (`git show origin/main:path`), and the branch
`mission/turn-detection-phase-one` for the change. Never the shared working tree at
`D:\ReposFred\devthrottle` - it is not this work and it is not current.
