import { describe, it, expect } from "vitest";
import { markdownToHtml } from "./historyMarkdown";

// The Brief and History bodies render agent-authored Markdown. marked 14's own URL cleaner only runs
// encodeURI and does NOT block dangerous link schemes, so markdownToHtml owns that guard: it allowlists
// http/https/mailto and neutralizes everything else, and it keeps the long-standing "raw HTML disabled"
// posture. These tests lock both behaviors down (issue #1030).

describe("link scheme allowlist", () => {
  it("neutralizes a javascript: link into inert text (no live href)", () => {
    const html = markdownToHtml("[x](javascript:alert(1))");
    // The visible text survives, but there is NO anchor and NO javascript: href to click.
    expect(html).toContain("x");
    expect(html.toLowerCase()).not.toContain("javascript:");
    expect(html).not.toContain("<a ");
  });

  it("neutralizes a data:text/html link into inert text", () => {
    const html = markdownToHtml("[click](data:text/html,<script>alert(1)</script>)");
    expect(html).toContain("click");
    expect(html.toLowerCase()).not.toContain("data:text/html");
    expect(html).not.toContain("<a ");
  });

  it("does not emit a live anchor for a tab-obfuscated scheme", () => {
    // marked will not even tokenize a URL containing a raw tab, so this renders as inert literal text;
    // sanitizeHref's control-char stripping is the second line of defense. Either way: no live link.
    const html = markdownToHtml("[x](java\tscript:alert(1))");
    expect(html).not.toContain("<a ");
    expect(html).not.toContain("href=");
  });

  it("renders a normal https link with a new-tab anchor", () => {
    const html = markdownToHtml("[docs](https://example.com/guide)");
    expect(html).toContain('href="https://example.com/guide"');
    expect(html).toContain('target="_blank"');
    expect(html).toContain('rel="noopener noreferrer"');
    expect(html).toContain(">docs</a>");
  });

  it("renders a normal http link", () => {
    const html = markdownToHtml("[site](http://example.com)");
    expect(html).toContain('href="http://example.com"');
    expect(html).toContain(">site</a>");
  });

  it("renders a mailto link", () => {
    const html = markdownToHtml("[mail](mailto:team@example.com)");
    expect(html).toContain('href="mailto:team@example.com"');
    expect(html).toContain(">mail</a>");
  });

  it("keeps a schemeless relative link (no scheme to reject)", () => {
    const html = markdownToHtml("[rel](/local/path)");
    expect(html).toContain('href="/local/path"');
    expect(html).toContain(">rel</a>");
  });

  it("preserves inline markup inside a safe link's text", () => {
    const html = markdownToHtml("[**bold**](https://example.com)");
    expect(html).toContain("<strong>bold</strong>");
    expect(html).toContain('href="https://example.com"');
  });
});

describe("raw HTML stays disabled", () => {
  it("renders an embedded <script> as inert escaped text", () => {
    const html = markdownToHtml("hello <script>alert(1)</script>");
    expect(html).not.toContain("<script>");
    expect(html).toContain("&lt;script&gt;");
  });

  it("returns empty string for empty/null input", () => {
    expect(markdownToHtml("")).toBe("");
    expect(markdownToHtml(null)).toBe("");
    expect(markdownToHtml(undefined)).toBe("");
  });
});

describe("line breaks", () => {
  it("markdownToHtml_SingleLineBreaksByDefault_JoinIntoOneParagraphAsTheHistoryChatHasAlwaysShownThem", () => {
    expect(markdownToHtml("1\n2\n3")).toBe("<p>1\n2\n3</p>\n");
  });

  it("markdownToHtml_KeepLineBreaks_KeepsEverySingleLineBreak", () => {
    expect(markdownToHtml("1\n2\n3", { keepLineBreaks: true })).toBe("<p>1<br>2<br>3</p>\n");
  });

  it("markdownToHtml_KeepLineBreaks_StillDisablesRawHtmlAndUnsafeLinks", () => {
    const html = markdownToHtml("<script>x</script>\n[a](javascript:alert(1))", { keepLineBreaks: true });
    expect(html).not.toContain("<script>");
    expect(html).not.toContain("javascript:");
  });
});
