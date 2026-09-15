# Hand-redacted turn-review screen pairs

Four pairs of saved terminal screens, in the same file shape the Director writes into its
`turn-review` directory, each demonstrating one thing the phase-one content rule has to get right:

| pair | what it demonstrates | the rule must |
|---|---|---|
| `repaint` | the same screen drawn again; only the composer and the footer moved | hold red |
| `torn-repaint` | one body row redrawn with characters dropped, so its key DIFFERS | hold red |
| `ticking-clock` | nothing changed but the numbers in a status row and a counter row | hold red |
| `real-reply` | rows of prose that were not there before | open, and say which row |

`torn-repaint` is the one that earns its place. Its changed row gets past the substance floor, past
the marker list and past the exact-key test - the near-duplicate filter at 0.80 is the only thing
holding it, so a change that weakened condition four would show up here and nowhere else.

## These are hand-redacted, and that means more than a search and replace

The labelled corpus these were modelled on is real work on one machine, full of client names, file
paths, session names and identifiers. **None of it is in this directory and none of it is in this
repository, which is public.** What was taken from the real captures is the SHAPE:

- the file format, and one cell per row exactly as the Director writes it;
- how many rows a screen has, and where the composer, the horizontal rules and the footer sit
  relative to the conversation;
- the exact mechanism of each pair - which row changes, and how it changes;
- the footer text an agent draws about itself, which is product text and not anyone's work.

Every line of prose was then WRITTEN for this directory. It is about a made-up defect in a made-up
reader. Read it as a stand-in: it is not a transcript of anything, and nobody should go looking for
the session it came from, because there is not one.

Two deliberate departures from what a real capture looks like, both so a fixture cannot be mistaken
for a verbatim one:

- **Plain ASCII throughout.** Real screens carry box-drawing glyphs, a heavy chevron at the
  composer, and spinner characters. Those are replaced with ASCII that plays the same part. The
  rule's conditions do not depend on which glyph it is - the substance floor and the comparison key
  both discard everything that is not an ASCII letter - so the demonstration survives the swap.
- **The session identifiers are obviously fake** (`...f001` through `...f004`) and the timestamps
  are the first of January.

## If you add one

Read every line of the pair you are copying from, not the diff. The whole reason this bar is set
where it is: a screen is a photograph of somebody's work, and one row of it in a public repository
is one row too many. If you are not certain a pair is clean, leave it out - four fixtures that are
certainly clean are worth more than six that are probably clean.
