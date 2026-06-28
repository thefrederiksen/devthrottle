// A focused TypeScript port of src/CcDirector.Core/Utilities/LinkDetector.cs, covering exactly the
// call the History view makes: FindAllLinkMatches(line, repoPath = null, pathExistsCheck = null).
//
// With no repo root and no existence check, the C# detector yields URLs and ABSOLUTE paths only -
// relative paths are deliberately not guessed (there is no existence check to validate them), so
// there are no false positives. The Cockpit/HistoryLinks wrapper runs this per line and dedups by
// first-seen order; this port keeps that behavior identical (Cockpit Services/HistoryLinks.cs).

export interface HistoryLink {
  text: string;
  isUrl: boolean;
}

// Absolute Windows paths (e.g. C:\path\to\file or C:/path/to/file). In C# the verbatim string's
// "" is a single quote char inside the negated class.
const AbsoluteWindowsPathRegex = /[A-Za-z]:[/\\][^\s"'`<>|*?()[\]]+/g;
// Unix-style absolute paths (e.g. /c/path/to/file for Git Bash / WSL).
const AbsoluteUnixPathRegex = /\/[a-z]\/[^\s"'`<>|*?()[\]]+/gi;
// URLs (http/https or git@).
const UrlRegex = /https?:\/\/[^\s"'`<>()[\]]+|git@[^\s"'`<>()[\]]+/gi;
// Characters that look like a path start inside a quoted span.
const PathLikeRegex = /^(?:[A-Za-z]:[/\\]|\/[a-z]\/|\.{0,2}[/\\]|[A-Za-z_][A-Za-z0-9_-]*[/\\])/i;

interface QuotedSpan {
  outerStart: number;
  outerEnd: number;
  innerStart: number;
  innerEnd: number;
  innerText: string;
}

interface Range {
  start: number;
  end: number;
}

interface LinkMatch {
  text: string;
  isUrl: boolean;
}

/** Distinct links in the body, in first-seen order. URLs + absolute paths only. */
export function extractLinks(body: string | null | undefined): HistoryLink[] {
  const result: HistoryLink[] = [];
  if (!body || body.trim().length === 0) return result;

  const seen = new Set<string>();
  // LinkDetector scans a single line at a time; a bubble body is multi-line.
  for (const line of body.replace(/\r\n/g, "\n").split("\n")) {
    for (const match of findAllLinkMatches(line)) {
      const key = (match.isUrl ? "u:" : "p:") + match.text;
      if (!seen.has(key)) {
        seen.add(key);
        result.push({ text: match.text, isUrl: match.isUrl });
      }
    }
  }
  return result;
}

// Port of FindAllLinkMatches with repoPath = null, pathExistsCheck = null. The C# claimedRanges /
// Overlaps priority ordering is preserved: quoted absolute paths, then URLs, then absolute Windows
// paths, then absolute Unix paths. Relative paths require a repo root + existence check, so they
// are skipped (as the C# does with those nulls).
function findAllLinkMatches(lineText: string): LinkMatch[] {
  const matches: LinkMatch[] = [];
  if (!lineText || lineText.trim().length === 0) return matches;

  const claimed: Range[] = [];

  // 1. Quoted paths (highest priority - handles spaces). Only ABSOLUTE paths survive without a
  //    repo root + existence check.
  for (const span of extractQuotedSpans(lineText)) {
    const inner = span.innerText;
    if (PathLikeRegex.test(inner)) {
      const path = stripTrailingPunctuation(stripLineNumber(inner));
      if (path.length > 0 && !isRelativePath(path)) {
        matches.push({ text: path, isUrl: false });
        claimed.push({ start: span.outerStart, end: span.outerEnd });
      }
    }
  }

  // 2. URLs.
  for (const m of lineText.matchAll(UrlRegex)) {
    const start = m.index ?? 0;
    const end = start + m[0].length;
    if (overlaps(claimed, start, end)) continue;
    const url = stripTrailingPunctuation(m[0]);
    matches.push({ text: url, isUrl: true });
    claimed.push({ start, end });
  }

  // 3. Absolute Windows paths.
  for (const m of lineText.matchAll(AbsoluteWindowsPathRegex)) {
    const start = m.index ?? 0;
    const end = start + m[0].length;
    if (overlaps(claimed, start, end)) continue;
    const path = stripTrailingPunctuation(stripLineNumber(m[0]));
    matches.push({ text: path, isUrl: false });
    claimed.push({ start, end });
  }

  // 4. Unix-style absolute paths.
  for (const m of lineText.matchAll(AbsoluteUnixPathRegex)) {
    const start = m.index ?? 0;
    const end = start + m[0].length;
    if (overlaps(claimed, start, end)) continue;
    const path = stripTrailingPunctuation(stripLineNumber(m[0]));
    matches.push({ text: path, isUrl: false });
    claimed.push({ start, end });
  }

  return matches;
}

/** Strip line-number suffix from a path ("file.cs:42" -> "file.cs"), keeping a drive-letter colon. */
export function stripLineNumber(path: string): string {
  for (let i = path.length - 1; i > 1; i--) {
    const c = path[i];
    if (c >= "0" && c <= "9") continue;
    if (c === ":") continue;
    // Found a non-digit, non-colon character.
    if (i + 1 < path.length && path[i + 1] === ":") return path.substring(0, i + 1);
    return path;
  }
  return path;
}

/** Strip trailing sentence punctuation (comma, semicolon, period) that is not part of the path/URL. */
export function stripTrailingPunctuation(text: string): string {
  if (!text) return text;
  while (text.length > 0) {
    const last = text[text.length - 1];
    if (last === "," || last === ";" || last === ".") {
      text = text.slice(0, -1);
      continue;
    }
    break;
  }
  return text;
}

// Extract quoted spans from text. Supports double quotes, single quotes, and backticks.
function extractQuotedSpans(text: string): QuotedSpan[] {
  const spans: QuotedSpan[] = [];
  if (!text) return spans;

  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (c === '"' || c === "'" || c === "`") {
      const closeIndex = text.indexOf(c, i + 1);
      if (closeIndex > i + 1) {
        spans.push({
          outerStart: i,
          outerEnd: closeIndex + 1,
          innerStart: i + 1,
          innerEnd: closeIndex,
          innerText: text.substring(i + 1, closeIndex),
        });
        i = closeIndex + 1;
        continue;
      }
    }
    i++;
  }
  return spans;
}

function isRelativePath(path: string): boolean {
  if (path.length >= 2 && path[1] === ":") return false;
  if (path.startsWith("/") && path.length >= 3 && path[2] === "/") return false;
  return true;
}

function overlaps(ranges: Range[], start: number, end: number): boolean {
  for (const r of ranges) {
    if (start < r.end && end > r.start) return true;
  }
  return false;
}
