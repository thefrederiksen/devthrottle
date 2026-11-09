# Dev reports - how you report to the owner

**When the owner asks you for a report, a write-up, a mission document, a design, findings, or
"tell me what you found" - you publish a DEV REPORT. One HTML file, published with
`cc-dev-reports`. That is the default and it needs no permission.**

```bash
cc-dev-reports open <file.html>  # publish it. Prints the one address the owner clicks.
cc-dev-reports open <file.html> --mission-document  # publish it as this session's Mission's document
cc-dev-reports reply "<text>"    # answer the owner inside the report, later
cc-dev-reports answers <file.html>  # read his stored answers - from any session, even a later run
cc-dev-reports answers <file.html> --claim  # reserve unacted answers before acting on them
```

Publishing the same file again from the same session makes a new version; publishing it from a later
session makes a new report.

## Two commands both called "report". Do not confuse them

| Command | Who reads it |
|---|---|
| `cc-dev-reports open` | **THE OWNER.** A human. On his phone or in the Cockpit. |
| `cc-devthrottle session handback` | **Another session** - the one that started you. Not a human. (Was called `session report`; the old name still works, which is why the confusion persists.) |

If a person asked you for the report, it is the first one. `session handback` is a handback between
sessions; it will never reach the owner and he will never see it.

## What a dev report is, and why it is not any of the alternatives

A dev report reaches the owner's phone, it carries questions he answers **by tapping an option**,
and you can reply inside it afterwards. Nothing else on this list does any of that.

| Instead of a dev report | When it is right |
|---|---|
| **A file path on disk** | Never, on its own. You always write the file first, then publish it. **Handing over a path instead of publishing is the defect this skill exists to stop.** |
| **Markdown in the repository** | For the NEXT SEAT, not the owner: mission documents, handovers, quality assurance results, anything another agent will read. Legitimate, different reader. |
| **cc-pdf / cc-html / cc-word** | For a document that LEAVES the fleet: a client deliverable, a board paper, something the owner forwards to someone else. |
| **A hosted artifact (claude.ai)** | Never for reporting. No session, no questions, no reply, and it publishes outside the fleet. |
| **A chat message** | Fine for a sentence. Anything with sections, tables, or a question the owner has to decide is a report. |

The dividing line is **who reads it**. The owner reads a dev report. The next seat reads repository
markdown. Someone outside the fleet reads a rendered document.

## The shape

A publish runs a **shape check**, and a report of the wrong shape is refused - nothing is published.
The rules below are the whole of what an author needs.

It is **one self-contained HTML file**. No `<script>` elements and no inline event handlers
(`onclick=` and friends): they are blocked when the report is shown, so the check refuses them rather
than let the owner open a page that silently does less than you meant. Draw everything with HTML, CSS
and inline SVG. Images must be `data:` URLs. Styles are free - a report can look however you like.

The structure is carried by `data-dev-report` attributes, not by class names or headings:

| Marker | Rule |
|---|---|
| `data-dev-report="header"` | Exactly one, first. Must carry `data-dev-report-status`. |
| `data-dev-report-status` | On the header. **One of exactly three words: `waiting-on-you`, `agent-working`, `done`.** |
| `data-dev-report="summary"` | Exactly one, right after the header. Must have words in it. |
| `data-dev-report="questions"` | Exactly one, right after the summary. **Present even when you have no questions.** |
| `data-dev-report="detail"` | At least one, all after the questions section. |
| `data-dev-report="why"` | Optional, at most one, after the questions section. Expected in a mission document. |
| `data-dev-report="goal"` | Optional, at most one, after the questions section. Expected in a mission document. |
| `data-dev-report="evidence"` | Optional, at most one, and last if present. |

"Right after" is about the order of the MARKERS, not the HTML in between. Sections may not contain
other sections - close one before the next begins.

**Inventing a fourth status word is the single most common way a first publish fails.**

### Questions - the surface built for a decision

If you need a decision from the owner, put it here: he taps an option instead of typing a reply.

```html
<div data-dev-report-question="deploy-window" data-dev-report-question-text="When should we deploy?">
  <h3>When should we deploy?</h3>
  <label><input type="radio" name="deploy-window" value="tonight" data-recommended> Tonight - quiet traffic</label>
  <label><input type="radio" name="deploy-window" value="monday"> Monday - the team is around</label>
  <textarea data-dev-report-comment placeholder="Anything to add (optional)"></textarea>
</div>
```

- Each question has a unique id (letters, digits, `-`, `_`) and lives inside the questions section.
- **At least two `<input type="radio">` options**, all sharing one `name` that no other question uses.
- **Every option has its own `value`** - never empty, never the same as another option's in the question.
  An answer names its option by value, so a report breaking this is refused at publish.
- **Exactly one option carries `data-recommended`** - your recommendation, preselected for him. Always
  give one, and say why in the option's own words.
- A `<textarea data-dev-report-comment>` is optional.
- Questions may not be nested, and no radio may sit outside a question.
- **A question a contributor may answer carries `data-dev-report-for="contributors"`** on the
  element with `data-dev-report-question`. Leave it off every question only the owner's own people
  should decide - that is the default. In a team, a report is sent to a contributor only when every
  question in it is marked, and a contributor's answer to an unmarked question is refused. When a
  contributor answers, you are told a contributor answered, never who: their choice is input to
  weigh, not the owner's decision. `contributors` is the only value; any other is refused at publish.

**When you have no questions**, the section is still there and says so in words, with an element
marked `data-dev-report-no-questions`: "No questions - nothing needed from you." A missing or empty
section does not read as "no questions", and the check refuses both.

Do not write your questions as prose in a detail section. That throws away the whole point of the
format.

### Say what you have NOT proved

Put an evidence section at the end and say plainly what your claims rest on and what you did not
check. A report that lists only what went right is not a report the owner can act on.

## A mission document - publish it with `--mission-document`

A mission document is a dev report that is linked to its Mission. Publish it from a session that is
attached to that Mission:

```bash
cc-dev-reports open mission.html --mission-document
```

- **It links the report to the publishing session's own Mission.** The request names no Mission; the
  Gateway uses the one this session is attached to. Every session on that Mission then gets a Mission
  button that opens this report.
- **One document per Mission.** Republishing the same report updates the same link. Publishing a
  different report this way replaces the old document, and the output says which one it replaced.
- **"The same report" means the same session and the same file.** From any other session on the
  Mission - a Delivery Lead republishing the Architect's document, say - publish WITH the flag: that
  makes a new report, which takes the link over. Without the flag it is an ordinary new report, and
  the Mission button keeps opening the old one.
- **Mark the why and the goal** with their own sections, after the questions. On every publish the
  Gateway reads them and stamps them on the Mission, so the goal on the Mission button is the goal in
  the document. A republish of the Mission's document from the session that published it
  refreshes them, with or without the flag. A
  document with no goal section leaves the Mission with no goal, and the output says so.

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

- **"This session is attached to no Mission" means nothing was published.** If the Mission does not
  exist yet, create it (`cc-devthrottle mission create "<name>"`, which prints its id and does not
  attach you). Attach your own session (`cc-devthrottle mission attach <your session> <mission id>`)
  and publish again, or publish without the flag
  if it is not a mission document. Right after an attach, wait a few seconds: the attachment can take
  a moment to reach the Gateway.

## Reading his answers in a LATER run - publish, close, read next time

A run that needs the owner does not wait for him. It publishes its report with the questions in it and
closes. He answers whenever he likes; the answer is stored, never lost, even though your session has
ended. The NEXT run reads it and acts:

```bash
cc-dev-reports answers <file.html> --claim        # reserve unacted answers before acting
cc-dev-reports answers --report <id> --ack        # after the action succeeds
```

- **Name the same file every run.** Each run's publish is a report of its own. `answers <file.html>`
  finds the newest report plus every older one still holding an unacted answer or note. It returns all
  such reports; it does not stop at a fixed count. A factory seat reads only reports stamped with its
  factory and seat identity, even when another seat uses the same absolute path.
- **Claim before you act.** Two runs can overlap after the scheduler finishes starting the first
  session. Use `--claim` and act only on ids listed under `claimed for this session`. A claim shown for
  another session belongs to that session; do not act on it. The Gateway allows a new claim after the
  previous claiming session has ended.
- Each question prints his chosen option (label and value), his comment and when he answered - or
  `not answered yet`. His notes on the report print too, with the words they were attached to.
- **Acknowledge only after the action succeeds**: `--ack` marks this session's claimed answers and
  notes on that report. `--item <id>` (repeatable) marks only those. An unclaimed item or an item
  claimed by another session cannot be acknowledged by this session. A later answer to the same
  question is new and shows as not acted on.
- `--json` gives the same thing as data. `--report <id>` reads one report by its id.
- You read only your own account's reports; in a team, only the reports of the person behind your
  session.

## If you cannot publish

`cc-dev-reports` needs `CC_GATEWAY_URL`, `CC_GATEWAY_SESSION_KEY` and `CC_SESSION_ID`. Every
DevThrottle session has all three. A session that was NOT started by a Director - Claude Code running
in the cloud, for instance - has neither the tool nor those variables, and cannot publish.

**Then say so plainly and hand back the HTML.** Do not quietly write a file somewhere and call it a
report, and do not substitute an artifact or a markdown document without saying you did. "I could not
publish this, here is the report, here is why" is a good answer. A file path presented as a delivered
report is not.

## Reading the result

The publish prints one address, whole: `<gateway>/r/<report id>`. That is the only one anyone needs -
it lands the owner inside the report, on whatever device he opens it with.

`openItems` in the output is **not** a count of your questions. It counts notes and answers the OWNER
has sent back. On a fresh publish it is 0, and that is correct.

## Why this skill exists

A session was asked for a mission document "in the new dev report format". It did not know the tool
existed. It found an older session's leftover builder script in a temporary folder, reverse-engineered
the format out of it, wrote a file into the repository, and handed over a path. When it finally
published, the shape check refused it: the status word it had invented was not one of the three.

The format was guessed accurately enough to look right and was still wrong. The specification existed
the whole time, in the product's own source, where only that feature's own authors would look. Now it
is here, where you are.
