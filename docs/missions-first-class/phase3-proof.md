# Missions First-Class - Phase 3 proof (the skills teach the flag)

## What changed

Two built-in skills, each edited only in its one source in `src/CcDirector.Gateway/Skills/Content/`. Neither has a
`.claude/skills` copy, and none was created. No code changed.

### `dev-reports.skill.md`

The command block at the top gains one line:

```
cc-dev-reports open <file.html> --mission-document  # publish it as this session's Mission's document
```

The marker table gains two rows:

```
| `data-dev-report="why"` | Optional, at most one, after the questions section. Expected in a mission document. |
| `data-dev-report="goal"` | Optional, at most one, after the questions section. Expected in a mission document. |
```

A new section, "A mission document - publish it with `--mission-document`", before "Reading his answers in a LATER
run". It says, in full:

- A mission document is a dev report linked to its Mission, published from a session attached to that Mission with
  `cc-dev-reports open mission.html --mission-document`.
- It links the report to the publishing session's own Mission; the request names no Mission. Every session on that
  Mission then gets a Mission button that opens the report.
- One document per Mission. Republishing the same report updates the same link; publishing a different report this
  way replaces the old document, and the output says which one it replaced.
- Mark the why and the goal with their own sections, after the questions. On every publish the Gateway reads them and
  stamps them on the Mission. A republish of the Mission's document refreshes them, with or without the flag. A
  document with no goal section leaves the Mission with no goal, and the output says so.
- An example of the two sections:

```html
<section data-dev-report="why">
  <h2>The why</h2>
  <p>Why this is worth building at all, in the owner's words.</p>
</section>
<section data-dev-report="goal">
  <h2>The goal</h2>
  <p>What must be true at the end.</p>
</section>
```

- "This session is attached to no Mission" means nothing was published. Attach the session first
  (`cc-devthrottle mission attach <session> <mission>`) and publish again, or publish without the flag if it is not a
  mission document. Right after an attach, wait a few seconds: the attachment can take a moment to reach the Gateway.

Each statement was checked against the phase 1 code on origin/main: the at-most-one and after-the-questions rules in
`DevReportShapeCheck.cs`, the refusal text and the attach command in `MissionDocumentLinker.cs`, the replace and
refresh behaviour in `docs/missions-first-class/phase1-proof.md`. The skill gives no full document template, so the
markers are shown as their own example, next to the existing question example's style.

### `devthrottle-method.skill.md`

One bullet in "If you are the Architect", before the handover bullet:

```
- **Publish the mission document** with `cc-dev-reports open <file> --mission-document`, from a
  session attached to the Mission, with its why and goal marked (the `dev-reports` skill has the
  markers). The Mission is linked to its document from the start, and every session's Mission button
  opens it.
```

Section 5 (the mission document) is unchanged: it lists the ten sections, and the publish belongs to the seat that
publishes, so the Architect's seat is where it fits. Nothing else in the method changed.

## The gate

To be filled with the run on the final commit.

## What this rests on, and what I have not checked

- **The Mission button is phase 2, which is not merged yet** (pull request 4619, open when this was written). Both
  skills say every session on the Mission gets a button that opens the document. That is true once phase 2 merges and
  the Gateway is deployed; this pull request should merge after 4619, or the sentence is ahead of the product for that
  window.
- Stale text noticed but not changed, as the brief asks: none in the two skills that touches this mission.
- I did not publish a mission document with the flag; the real republish is the Delivery Lead's.
