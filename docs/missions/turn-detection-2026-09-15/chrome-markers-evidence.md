# Where the self-describing row markers come from

Work item #2856 asks two agents for a list of the rows their own interface draws about itself.
This file is the evidence behind that list, taken on 15 September from the labelled corpus in
the private repository by counting which rows the rule would wrongly call new. Only the
interface strings are reproduced here; the conversation content those screens also contained is
real client work and stays in the private repository.

## Claude Code

One thousand two hundred and ninety-two repaint-labelled episodes in the rebuilt corpus. The
rows the rule would wrongly have counted as new, most frequent first:

- `new task? /clear to save <n>k tokens` - by a wide margin the most common, and it appears
  truncated at several widths, so the marker has to match a fragment rather than the whole row.
- `+<n> lines (ctrl+o to expand)`
- `Continuing shortly - esc to cancel`
- `bypass permissions on (shift+tab to cycle)`
- The thinking line: a glyph, a past-tense verb, `for <n>m <n>s`, then `done <time>`. The verb
  rotates through a large vocabulary, so the stable part is `ed for ` followed by a duration -
  not the verb.

**`esc to cancel` is not `esc to interrupt`.** The marker expression behind the published
measurement carries only the second. The first is a distinct row and the published numbers were
taken without it, which means the row rule's real score is very slightly better than published
rather than worse. Do not quietly fold that into the published figure - re-take the number.

Two more rows show up often and are deliberately NOT markers. A box rule drawn from line glyphs
carries no letters or digits at all, so the three-character condition already removes it. A
workspace indicator row carries a project name, which no general marker list can match; the
exact-key and near-duplicate conditions remove it instead, because it is present on both
screens. A marker list that tried to cover those would be matching content.

## Codex

Forty-three repaint-labelled episodes. That is thin, and the list below should be read as what
forty-three cases support rather than as a survey:

- `background terminal running`, with `/ps to view` and `/stop to close` on the same row
- `Token usage: total=... input=... output=...`
- The resume banner: `To continue this session, run:` followed by `codex resume <id>`

## What this evidence does not establish

It is one machine. Of the repaint-labelled population, one thousand two hundred and ninety-two
are Claude Code, forty-three are Codex and two are the one continuously-repainting agent. Every
other supported agent has no cases at all, which is exactly why they inherit an empty marker
list rather than a guessed one - an empty list leaves them no worse off than today, and a
guessed list could suppress their real output.

Most of the remaining rows in both samples are not interface chrome at all. They are real replies
that arrived in a short burst, which the eleven-second labelling rule cannot tell apart from a
repaint. So the measured suppression figure is a floor, not a ceiling, and a marker list tuned
until this sample is empty would be tuned to suppress real work.
