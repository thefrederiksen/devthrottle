import { Marked } from "marked";

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

// A configured marked instance. GFM gives autolinked URLs, tables, and fenced code; overriding the
// `html` renderer to escape its token is how we DISABLE raw HTML (marked passes it through by
// default). Code and codespans are already escaped by marked's built-in renderer.
const md = new Marked({ gfm: true, breaks: false });
md.use({
  renderer: {
    // Block- and inline-level raw HTML tokens both flow through here; escaping them makes any
    // <script>, <img>, etc. show as literal text instead of executing - the DisableHtml behavior.
    html(token: { text: string }): string {
      return escapeHtml(token.text);
    },
  },
});

// Add target/rel to every anchor that does not already declare a target, so links open in a new tab
// and never leak the opener (Markdig emits <a href="...">, marked the same, so the insert is safe).
const AnchorOpen = /<a (?![^>]*\btarget=)/gi;

/** Render Markdown text to sanitized HTML with new-tab anchors. Empty in -> empty out. */
export function markdownToHtml(text: string | null | undefined): string {
  if (!text) return "";
  const html = md.parse(text, { async: false }) as string;
  return html.replace(AnchorOpen, '<a target="_blank" rel="noopener noreferrer" ');
}
