# Missions

A mission is a body of work too big for one session and too long for one sitting. This
directory keeps the mission documents themselves, so the reasoning behind a change lives
beside the code it produced.

A mission document states, before the work starts:

- **why it exists** - the problem in plain words,
- **the goal** - the artifact that means it is finished, usually a QA report showing the
  feature working,
- **the rulings** the Architect settles before anyone writes code, and what each one costs
  if it is decided wrongly,
- **the seats** the work splits into, and what proves each one.

They are kept after the work lands. A merged change tells you what was done; the mission
document tells you what was decided and why, which is the part that is otherwise lost.

How a mission is RUN is not described here - that is one document, held centrally:
`cc-devthrottle workflow instructions mission`.

| Document | Issue |
|---|---|
| [stop-a-session.html](stop-a-session.html) | [#2633](https://github.com/thefrederiksen/devthrottle/issues/2633) - no way to kill a session |
