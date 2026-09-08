<!-- copied verbatim from the internal repository tools/mentor/mentor_tools/skill/rubric.md at commit 80d2897d; the rubric is single-sourced there, the product carries this copy the way it carries workflow instructions -->
# Prompting rubric - v1.1 (for the slots)

Six dimensions, judged in words, over the developer's own prompts of the week. Each has what
good looks like, what to look for, and an invented example pair (weak and strong) about an
imaginary project; none is from any developer.

## Specific target

What good looks like: the more precise the instruction, the fewer corrections it takes. A prompt
that names the file, function, issue number, URL, exact error text or a quoted string gives the
agent a place to start. A vague target makes the agent guess, and a wrong guess costs a turn plus
the correction that follows.

What to look for:

- A file path, a function or class name, an issue number, or a URL in the prompt.
- Error text or a log line pasted as it appeared, not retold from memory.
- A quoted string the agent can search for.
- "The thing", "that page", "it" with no earlier prompt in the session to resolve them.

Example pair (library checkout kiosk):

- Weak: "The return screen is broken again, please fix it."
- Strong: "In `kiosk/screens/return_item.py`, `confirm_return` raises `KeyError: 'due_date'` when
  the scanned barcode belongs to a reference-only book. Reproduce with barcode 0042 and fix it."

## A check the agent can run

What good looks like: give the agent a check it can run. A prompt that says what done looks like
- which test must pass, which command must print what, which page must show what - lets the agent
verify its own work before it answers. Without a check the developer becomes the test runner, and
every miss comes back as another prompt.

What to look for:

- A named test, command, or expected output.
- A sentence starting "done when", "it should", "verify by" or "the page must show".
- An acceptance condition the agent could execute, not one only a person can judge.
- A request for a proof (test output, a diff, a screenshot) alongside the change.

Example pair (garden-watering controller):

- Weak: "Make the valves stop overwatering."
- Strong: "Change `valves/timer.py` so no zone runs more than 20 minutes per cycle. Done when
  `pytest tests/test_timer.py -k max_run` passes and `python simulate.py --days 3` prints no
  line with `run_minutes` over 20."

## One task per prompt

What good looks like: one prompt asks for one thing. When several unrelated asks share one
message, the agent has to hold and order all of them, and the developer has to check which were
done and which were dropped. One task at a time keeps the turn short, the check clear, and the
correction, if one is needed, aimed at one thing.

What to look for:

- "Also", "and while you are at it", "then", "after that" joining unrelated work.
- Numbered lists of asks that touch different files or different concerns.
- A question and a change request in the same message.
- Several steps toward one outcome is fine; the miss is unrelated asks.

Example pair (library checkout kiosk):

- Weak: "Fix the return screen crash, also rename the admin menu, check why the receipt printer
  test is flaky, and add a dark theme."
- Strong: "Fix the `KeyError: 'due_date'` in `confirm_return` for reference-only books. I will
  send the receipt printer flakiness separately."

## Corrections that carry the reason

What good looks like: when a prompt redirects the agent, it says what was wrong, not only that it
was. "No" or "try again" gives the agent nothing to steer by, so the second attempt is another
guess. A correction that names the wrong assumption, file, or missed constraint lets the next
attempt land, for one sentence more than the bare "no".

What to look for:

- "No", "wrong", "not that", "try again", "undo" with nothing after them.
- A correction that names what the agent assumed and what is true instead.
- The constraint the agent broke stated in the correction, so it can be kept.
- The same correction appearing twice in one session: the reason never got through.

Example pair (garden-watering controller):

- Weak: "No, that is not it. Do it again."
- Strong: "Not that: you moved the cap into `zones.py`, but zones do not know the cycle length.
  The cap belongs in `timer.py` where the cycle is started, so the same rule covers every zone."

## Not re-explaining

What good looks like: context the agent already has is not typed again. What an earlier prompt
in the same session established stays established, and an instruction given in session after
session (the repeated-instruction clusters `week_overview` reports) belongs in a standing instruction file or a
skill the agent reads at the start, stated once and applied every time. Re-typing costs the words
each time and drifts: the tenth version is never quite the first.

What to look for:

- The same rule or preference appearing in prompts across several sessions of the week.
- A prompt that restates the layout, rules, or goal already given earlier in the same session.
- Long preambles before the actual ask.
- A prompt that says "as I said before" or "again".

Example pair (library checkout kiosk):

- Weak: in five sessions, "Remember: no external packages, tests under `tests/`, ASCII only,
  never touch the printer driver."
- Strong: those lines in the project's standing instruction file; the prompt is "Add a barcode
  validity check to `scan.py` and its test."

## Session hygiene

What good looks like: the agent's performance degrades as its context fills; a clean session
with a better prompt almost always outperforms a long one with accumulated corrections. After
two failed corrections on one issue, start fresh with what was learned folded into a better first
prompt. Before an unrelated task, start fresh too, so it does not carry the old context. Where the
`session_index` row reports a peak context, use it.

What to look for:

- Correcting the same issue past the second attempt instead of restarting.
- An unrelated task started in a session already deep in another.
- A high `peak_context` in the `session_index` row for a session with repeated corrections.
- A fresh session opened with a better-stated version of a failed earlier ask.

Example pair (garden-watering controller):

- Weak: the ninth prompt in one session, "still wrong, the rain sensor is still ignored", after
  eight rounds on the same bug, then "now also add the frost mode".
- Strong: a new session opened with "In `sensors/rain.py`, `should_skip` returns False when the
  gauge reports over 5 mm because the reading is compared as a string. Fix and prove with
  `pytest tests/test_rain.py`." Frost mode gets its own session.

## Judgement rule

For each dimension you write one level word from exactly this set - `rarely`, `sometimes`,
`mostly` - meaning how often the developer's prompts show the practice. The word goes in the
dimension's `level` field and nowhere else: the Level line the report prints is rendered from
that word and from the week's prompt count, and you never type the line or the count. When the
count `week_overview` reports is under ten, the word is `too few prompts to judge`, on every
dimension, never a level.

Beside the word, an `observation` with at least two citations, each the string `cite` returned
for that session and minute, at least one carrying a fragment that `verify_quote` answered true
for before you wrote it; and one `step`. Never a number as a grade, never a comparison to anyone.
