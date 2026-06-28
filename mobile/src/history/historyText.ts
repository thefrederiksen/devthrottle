// A 1:1 TypeScript port of src/CcDirector.Cockpit/Services/HistoryText.cs (CleanForReading).
//
// Cleans an agent transcript message into something a human can comfortably read. Raw transcripts
// carry two kinds of eyesore:
//
//   1. MACHINERY a person should never see - the coding-agent's command wrapper tags
//      (<command-name>, <local-command-caveat>, <local-command-stdout>), injected
//      <system-reminder> / <task-notification> context blocks, and terminal ANSI color codes.
//      These are deleted outright.
//
//   2. ANGLE-BRACKET PLACEHOLDER TOKENS inside otherwise-real prose - things like <issue#>,
//      <your reply>, <task-id>. Rendered as Markdown (with raw HTML disabled) these escape to
//      literal <tag> text. They are part of what the agent wrote, so instead each is wrapped as
//      inline code, rendering as a tidy monospace chip.
//
// Real code and types like List<string> are left alone (a tag glued to a word is not rewritten),
// and nothing inside a fenced code block is touched.

// Terminal color / cursor escape sequences: a real ESC byte, then "[ ... letter". Anchored to the
// ESC (\x1B) so it NEVER touches ordinary bracket text like a Markdown "[link]".
const Ansi = /\x1B\[[0-9;?]*[A-Za-z]/g;

// Whole wrapper blocks to delete outright (open tag + content + close tag), across newlines.
const DropBlocks =
  /<(local-command-caveat|command-message|command-args|command-contents|local-command-stdout|system-reminder|task-notification)\b[^>]*>[\s\S]*?<\/\1>/gi;

// A slash-command invocation: keep just the command itself (e.g. "/compact"), drop the wrapper.
const CommandName = /<command-name>\s*([\s\S]*?)\s*<\/command-name>/gi;

// Any leftover stray wrapper tag (e.g. a block truncated mid-way so its close tag was cut off).
const StrayTags =
  /<\/?(local-command-caveat|command-name|command-message|command-args|command-contents|local-command-stdout|system-reminder|task-notification)\b[^>]*>/gi;

// A tag-like placeholder token to present as inline code: a "<", then a letter or "/", then up to
// 60 non-bracket characters, then ">". The negative lookbehind keeps it from firing when the "<"
// is glued to a word - so real generics (List<string>) are left alone, while standalone
// placeholders (<issue#>, <your reply>, </task-id>) are wrapped.
const PlaceholderTag = /(?<![`\w])<([A-Za-z/][^<>\r\n]{0,60})>/g;

// A Markdown code region - a triple-backtick fenced block OR a single-backtick inline span. Tags
// INSIDE these are already deliberate code the agent wrote, so they are skipped.
const CodeRegion = /```[\s\S]*?```|`[^`\r\n]*`/g;

const BlankRuns = /\n{3,}/g;

/**
 * Strip transcript machinery and tidy placeholder tags, returning human-readable Markdown.
 * Returns "" when the whole message was machinery (the caller drops the empty bubble).
 */
export function cleanForReading(text: string | null | undefined): string {
  if (!text) return "";

  let s = text.replace(Ansi, "");
  s = s.replace(DropBlocks, "");
  s = s.replace(CommandName, "$1");
  s = s.replace(StrayTags, "");
  s = wrapPlaceholderTags(s);
  s = s.replace(BlankRuns, "\n\n");
  return s.trim();
}

/**
 * Wrap tag-like placeholder tokens in backticks so Markdown renders them as monospace chips - but
 * ONLY in the prose between code regions, never inside a fenced block or an inline code span
 * (wrapping there would inject stray backticks and break the span).
 */
function wrapPlaceholderTags(s: string): string {
  if (!s.includes("<")) return s;

  let result = "";
  let last = 0;
  CodeRegion.lastIndex = 0;
  let code: RegExpExecArray | null;
  while ((code = CodeRegion.exec(s)) !== null) {
    result += s.slice(last, code.index).replace(PlaceholderTag, wrapMatch); // prose before this code
    result += code[0]; // code region, verbatim
    last = code.index + code[0].length;
  }
  result += s.slice(last).replace(PlaceholderTag, wrapMatch); // trailing prose
  return result;
}

function wrapMatch(_match: string, group1: string): string {
  return "`<" + group1 + ">`";
}
