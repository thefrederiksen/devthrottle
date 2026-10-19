import { Marked, type Tokens } from "marked";

// Renders History-bubble bodies as Markdown, the mobile twin of
// src/CcDirector.Cockpit/Services/HistoryMarkdown.cs. The desktop uses Markdig with
// UseAdvancedExtensions().DisableHtml(); here we use `marked` with GitHub-flavored Markdown and the
// same "HTML disabled" posture: raw HTML in a message is rendered INERT (escaped) rather than
// executed, so a transcript can never inject live markup into the page. Anchors are rewritten to
// open in a new browser tab (the app may run in a remote browser), mirroring the desktop AnchorOpen
// post-pass.

// Escape the five HTML-significant characters so a raw-HTML token renders as visible, inert text.
function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

// The only URL schemes a Markdown link may carry. marked 14's cleanUrl only runs encodeURI - it does
// NOT block dangerous schemes, so a reply could otherwise emit a live <a href="javascript:..."> or a
// data:text/html link. We allowlist the three safe schemes and neutralize everything else (issue #1030).
const SAFE_SCHEMES = new Set(["http", "https", "mailto"]);

// Return the safe, marked-cleaned href, or null when the scheme is not allowlisted. A URL with NO
// scheme (a relative path, a #fragment, or a //protocol-relative link) is always safe. Control and
// whitespace characters are stripped before the scheme test so an obfuscated scheme (for example
// "java\tscript:") cannot slip past the allowlist while the browser would still act on it.
function sanitizeHref(href: string): string | null {
  const stripped = href.replace(/[\u0000-\u0020]/g, "");
  const scheme = /^([a-zA-Z][a-zA-Z0-9+.-]*):/.exec(stripped);
  if (scheme && !SAFE_SCHEMES.has(scheme[1].toLowerCase())) return null;
  try {
    // Mirror marked's own cleanUrl so an accepted URL renders exactly as marked would emit it.
    return encodeURI(href).replace(/%25/g, "%");
  } catch {
    return null;
  }
}

// A configured marked instance. GFM gives autolinked URLs, tables, and fenced code; overriding the
// `html` renderer to escape its token is how we DISABLE raw HTML (marked passes it through by
// default). Code and codespans are already escaped by marked's built-in renderer.
//
// Two instances, alike but for `breaks`. The history Chat keeps Markdown's own rule (a single line break joins
// the lines into one paragraph), matching the desktop's Markdig. The API-mode live Chat keeps every line break
// the agent sent: "one per line" has to arrive one per line (API mode phase 6 QA, finding 3).
function configuredMarked(breaks: boolean): Marked {
  const instance = new Marked({ gfm: true, breaks });
  instance.use({
    renderer: {
      // Block- and inline-level raw HTML tokens both flow through here; escaping them makes any
      // <script>, <img>, etc. show as literal text instead of executing - the DisableHtml behavior.
      html(token: { text: string }): string {
        return escapeHtml(token.text);
      },
      // Allowlist link schemes. When the href is unsafe (javascript:, data:, ...), drop the anchor and
      // render only its inert text so a click can do nothing. Safe links render as marked's default
      // would (this.parser.parseInline preserves any inline markup inside the link text).
      link(token: Tokens.Link): string {
        const text = this.parser.parseInline(token.tokens);
        const href = sanitizeHref(token.href);
        if (href === null) return text;
        const title = token.title ? ` title="${escapeHtml(token.title)}"` : "";
        return `<a href="${escapeHtml(href)}"${title}>${text}</a>`;
      },
    },
  });
  return instance;
}

const md = configuredMarked(false);
const mdKeepingLineBreaks = configuredMarked(true);

// Add target/rel to every anchor that does not already declare a target, so links open in a new tab
// and never leak the opener (Markdig emits <a href="...">, marked the same, so the insert is safe).
const AnchorOpen = /<a (?![^>]*\btarget=)/gi;

export interface MarkdownOptions {
  /** Keep a single line break as a line break, instead of joining the lines into one paragraph. */
  keepLineBreaks?: boolean;
}

/** Render Markdown text to sanitized HTML with new-tab anchors. Empty in -> empty out. */
export function markdownToHtml(text: string | null | undefined, options?: MarkdownOptions): string {
  if (!text) return "";
  const html = (options?.keepLineBreaks ? mdKeepingLineBreaks : md).parse(text, { async: false }) as string;
  return html.replace(AnchorOpen, '<a target="_blank" rel="noopener noreferrer" ');
}
