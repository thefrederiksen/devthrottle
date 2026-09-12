# Codex input routing - mission brief

Mission: codex-input-routing. Branch: `fix/codex-input-routing`, worktree
`D:\ReposFred\worktrees\devthrottle-codex-input-routing`. One code slice, commit
`629db45` ("Reduce unnecessary temporary prompt wrappers").

Conduct: `cc-devthrottle workflow instructions mission` (pinned v14). This brief
describes the work only; it grants nothing and restates no conduct rules.

## Status

Code complete and pushed; independent inspection in progress at the time this
record was opened. The final status, and the QA report, are recorded at the end
of `state.md` when the mission lands.

## Why

The owner, in his own words: "Why are we getting all these input read files in
these Codex sessions? Where are all those temp input read files coming from? It
makes it really confusing and hard to understand what's going on."

Before this mission, `TerminalSubmit.ShouldUseInstructionFile` routed EVERY
Codex prompt through a temporary payload file, however short - a single-line
"stop" became "Read file input_....txt in the .temp directory. ... reply with
the requested strings only.", the agent spent a tool call reading the file
before it could answer, and the transcript the owner reads showed the wrapper
instead of his message.

What is true when this is finished: a single-line prompt up to the large-input
threshold is typed straight into the Codex terminal and answered in place; only
multiline or genuinely large input takes the payload file; and the transcript
the owner reads shows his original message either way.

## Design rulings

Rulings 1-3 were inferred by the Manager during the run and accepted by the
owner when he approved the recommendations. Ruling 4 follows from the same
approval. None of them was stated by the owner unprompted.

1. Single-line input is submitted directly to the Codex terminal up to and
   including 1000 characters (the existing `LargeInputHandler.LargeInputThreshold`).
2. Multiline input, or input over 1000 characters, still goes through the
   temporary payload file. The instruction the agent receives is now short and
   truthful ("Read and respond to the complete incoming message in <path>.")
   instead of the old "This file was explicitly created as the user-provided
   message payload ... reply with the requested strings only." wording.
3. Payload files are NOT deleted after submission: the file may be the only
   durable copy of the original prompt once it has been replaced in readable
   history by ruling 4.
4. History reading resolves Director-owned payload references back to the
   original message: the current instruction, the legacy instruction, and the
   bare `@.temp/input_...` reference, for the Director-owned
   `input_YYYYMMDD_HHMMSS_xxxxxx.txt` filename pattern only, when the payload
   file still exists in the repository. Files the Director did not create are
   never substituted; a missing file leaves the recorded instruction verbatim.

## Work, in landing order

One slice: `629db45`.

- `TerminalSubmit.ShouldUseInstructionFile`: Codex takes the file route only
  for multiline or over-threshold input; Copilot and OpenCode keep their
  previous route (file when over 300 characters single-line, or
  multiline/large).
- `LargeInputHandler`: new `FormatInputFileInstruction` (the short truthful
  instruction), wording fixes, threshold constants unchanged.
- `StoredPromptPayloadResolver` (new): history substitution per ruling 4,
  with a repository-escape guard, missing/empty file handling, a 4000-character
  display cap, and per-outcome logging.
- `SessionHistoryReader`: both `Read` paths run the resolver.
- Tests: routing boundary theory (301/500/750/1000 direct, 1001 and multiline
  via file), truthful-instruction assertions, resolver substitution /
  unowned-file / missing-file cases.

## Out of scope

- Sweeping or deleting the existing `.temp/input_*.txt` backlog.
- The outer Gateway framing of user messages to the Director (named as not
  proven in the Manager's handoff).
- Any behavior change for Claude Code, Gemini, or Grok submission paths.
