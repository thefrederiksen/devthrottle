# C1 bracketed-paste end marker check

Run on 24 October 2026 in visible RawCli sessions from product commit `bce8a87ffd52`.

The test text placed C1 CSI `U+009B` followed by `201~` between `before` and `after` inside a desktop bracketed paste.

- The echo agent received the entire text exactly in one framed paste for desktop, Claude Code, Codex and Pi session shapes. The text after `U+009B201~` remained inside the paste.
- Claude Code received the words after the sequence inside its pasted text on both runs. It removed the C1 control character itself, so both results were altered only at that character; it did not recognise `U+009B201~` as a paste end.
- Pi received the full text exactly on both runs.

Therefore no tested agent treated the 8-bit CSI sequence as the end of bracketed paste. The encoder is unchanged: removing C1 would alter a user's paste without evidence that it can escape the paste.

Evidence logs:

- `/tmp/4382-c1.log` (echo layer)
- `/tmp/4382-c1-agents.log` (Claude Code and Pi)
