"""CSS generation from canonical themes for cc-pdf and cc-html.

Replaces static CSS files with programmatic generation from
the canonical theme definitions in cc_shared.themes.
"""

try:
    from .themes import get_theme, CanonicalTheme
except ImportError:
    from themes import get_theme, CanonicalTheme


# Base structural CSS -- shared across all themes.
# Theme values are referenced via CSS custom properties (var(--name)).
BASE_CSS = """/* Base styles for all themes */

*,
*::before,
*::after {
    box-sizing: border-box;
}

html {
    font-size: 16px;
    line-height: var(--line-height);
    -webkit-font-smoothing: antialiased;
    -moz-osx-font-smoothing: grayscale;
    text-rendering: optimizeLegibility;
}

body {
    margin: 0;
    padding: 0;
    font-family: var(--font-body);
    color: var(--text);
    background: var(--background);
}

.markdown-body {
    max-width: 800px;
    margin: 0 auto;
    padding: 3rem 2.5rem;
}

/* Typography */
h1, h2, h3, h4, h5, h6 {
    font-family: var(--font-heading);
    color: var(--heading);
    margin-top: 1.8em;
    margin-bottom: 0.6em;
    font-weight: 600;
    line-height: 1.25;
    letter-spacing: var(--heading-letter-spacing);
    orphans: 3;
    widows: 3;
}

h1 {
    font-size: 2.25em;
    margin-top: 0;
}

h2 { font-size: 1.65em; }
h3 { font-size: 1.35em; }
h4 { font-size: 1.1em; }
h5 { font-size: 0.95em; }
h6 { font-size: 0.85em; color: var(--blockquote-text); }

p {
    margin: 1em 0;
    orphans: 3;
    widows: 3;
}

/* Links */
a {
    color: var(--link);
    text-decoration: none;
    border-bottom: 1px solid transparent;
    transition: border-color 0.2s ease;
}

a:hover {
    border-bottom-color: var(--link);
}

/* Lists */
ul, ol {
    margin: 1em 0;
    padding-left: 2em;
    line-height: var(--line-height);
}

li {
    margin: 0.35em 0;
}

li > ul,
li > ol {
    margin: 0.2em 0;
}

/* Inline code */
code {
    font-family: var(--font-code);
    font-size: 0.875em;
    padding: 0.15em 0.4em;
    border-radius: var(--radius);
    background: var(--code-bg);
    color: var(--code-text);
}

/* Code blocks */
pre {
    margin: 1.5em 0;
    padding: 1.25em 1.5em;
    overflow-x: auto;
    border-radius: var(--code-radius);
    background: var(--code-bg);
    border: 1px solid var(--border);
    box-shadow: var(--shadow-sm);
    position: relative;
}

pre code {
    padding: 0;
    background: transparent;
    border-radius: 0;
    font-size: 0.875em;
    line-height: 1.55;
}

/* Blockquotes */
blockquote {
    margin: 1.5em 0;
    padding: 0.75em 1.25em;
    border-left: 4px solid var(--blockquote-border);
    background: var(--blockquote-bg);
    color: var(--blockquote-text);
    border-radius: 0 var(--radius) var(--radius) 0;
}

blockquote p {
    margin: 0.4em 0;
}

blockquote p:first-child {
    margin-top: 0;
}

blockquote p:last-child {
    margin-bottom: 0;
}

/* Tables */
table {
    width: 100%;
    border-collapse: separate;
    border-spacing: 0;
    margin: 1.5em 0;
    border-radius: var(--radius);
    overflow: hidden;
    box-shadow: var(--shadow-sm);
    border: 1px solid var(--border);
}

th, td {
    padding: 0.75em 1em;
    text-align: left;
    border-bottom: 1px solid var(--border);
}

th {
    font-weight: 600;
    background: var(--table-header-bg);
    color: var(--table-header-text);
}

tr:last-child td {
    border-bottom: none;
}

/* Horizontal rule */
hr {
    border: none;
    height: 2px;
    max-width: 200px;
    margin: 2.5em auto;
    background: var(--border);
    border-radius: 1px;
}

/* Images */
img {
    max-width: 100%;
    height: auto;
    border-radius: var(--radius);
    box-shadow: var(--shadow-sm);
}

/* Task lists */
.task-list-item {
    list-style-type: none;
}

.task-list-item input[type="checkbox"] {
    margin-right: 0.5em;
}

/* Strong and emphasis */
strong {
    font-weight: 700;
}

/* Definition lists (if supported by parser) */
dt {
    font-weight: 600;
    margin-top: 1em;
}

dd {
    margin-left: 1.5em;
    margin-bottom: 0.5em;
}
"""


# Print-specific CSS appended when for_pdf=True.
# NOTE: the @page rule (page size and margin) is injected dynamically at convert
# time by cc-pdf from the --page-size / --margin options, so it is intentionally
# not hardcoded here.
PRINT_CSS = """
/* Print / PDF styles */
@media print {
    h1, h2, h3, h4, h5, h6 {
        page-break-after: avoid;
        break-after: avoid;
    }

    pre, blockquote, table, img, figure {
        page-break-inside: avoid;
        break-inside: avoid;
    }

    tr {
        page-break-inside: avoid;
        break-inside: avoid;
    }

    p {
        orphans: 3;
        widows: 3;
    }

    .markdown-body {
        max-width: none;
        padding: 0;
    }
}
"""


def _generate_custom_properties(theme: CanonicalTheme) -> str:
    """Generate CSS custom properties on :root from theme values."""
    c = theme.colors
    f = theme.fonts
    s = theme.style

    return f"""/* {theme.name.title()} Theme - {theme.description} */

:root {{
    /* Colors */
    --primary: {c.primary};
    --primary-light: {c.primary_light};
    --accent: {c.accent};
    --text: {c.text};
    --heading: {c.heading};
    --background: {c.background};
    --code-bg: {c.code_bg};
    --code-text: {c.code_text};
    --border: {c.border};
    --link: {c.link};
    --blockquote-text: {c.blockquote_text};
    --blockquote-border: {c.blockquote_border};
    --blockquote-bg: {c.blockquote_bg};
    --table-header-bg: {c.table_header_bg};
    --table-header-text: {c.table_header_text};
    --alt-row-bg: {c.alt_row_bg};
    --shadow-color: {c.shadow_color};

    /* Fonts */
    --font-heading: "{f.heading}", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    --font-body: "{f.body}", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    --font-code: "{f.code}", "JetBrains Mono", "Fira Code", Consolas, monospace;

    /* Style */
    --heading-letter-spacing: {s.heading_letter_spacing};
    --line-height: {s.body_line_height};
    --radius: {s.border_radius};
    --code-radius: {s.code_border_radius};
    --shadow-sm: {s.shadow_sm};
    --shadow-md: {s.shadow_md};
}}
"""


# Per-theme structural CSS that gives each theme its unique visual identity.
# These reference CSS custom properties set on :root.
THEME_EXTRAS: dict[str, str] = {
    "boardroom": """
/* Boardroom: corporate authority with accent borders */
h1 {
    border-bottom: 3px solid var(--accent);
    padding-bottom: 0.5em;
}

h2 {
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3em;
}

pre {
    border-left: 4px solid var(--primary);
    border-radius: 0 var(--code-radius) var(--code-radius) 0;
}

pre::before {
    content: "";
    position: absolute;
    top: 0;
    left: 0;
    width: 4px;
    height: 100%;
    background: var(--accent);
    border-radius: 0;
}

blockquote {
    font-style: italic;
}

hr {
    background: var(--accent);
    max-width: none;
    height: 2px;
}

tr:nth-child(even) td {
    background: var(--alt-row-bg);
}
""",
    "paper": """
/* Paper: elegance through restraint */
h1 {
    font-weight: 300;
    font-size: 2.5em;
}

h2 {
    font-weight: 400;
}

pre {
    border: 1px solid var(--border);
    box-shadow: none;
}

table {
    box-shadow: none;
}

img {
    box-shadow: none;
}

blockquote {
    border-left-width: 2px;
}
""",
    "terminal": """
/* Terminal: monospaced hacker aesthetic */
body {
    font-size: 14px;
}

.markdown-body {
    background: var(--background);
}

h1, h2, h3, h4, h5, h6 {
    font-weight: 700;
}

h1::before { content: "# "; opacity: 0.5; }
h2::before { content: "## "; opacity: 0.5; }
h3::before { content: "### "; opacity: 0.5; }

pre {
    border: 1px solid var(--border);
    box-shadow: none;
}

pre::before {
    content: "";
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 3px;
    background: var(--primary);
}

code {
    border: 1px solid var(--border);
}

pre code {
    border: none;
}

/* Terminal-style list markers */
ul {
    list-style-type: none;
    padding-left: 1.5em;
}

ul > li {
    position: relative;
}

ul > li::before {
    content: "> ";
    color: var(--primary);
    position: absolute;
    left: -1.5em;
}

table {
    box-shadow: none;
    border-radius: 0;
}

img {
    border-radius: 0;
    box-shadow: none;
}

hr {
    max-width: none;
    background: var(--border);
    height: 1px;
}

blockquote {
    border-radius: 0;
}

/* Scanline overlay effect */
.markdown-body::before {
    content: "";
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    pointer-events: none;
    background: repeating-linear-gradient(
        0deg,
        transparent,
        transparent 2px,
        rgba(0, 0, 0, 0.03) 2px,
        rgba(0, 0, 0, 0.03) 4px
    );
    z-index: 1000;
}
""",
    "spark": """
/* Spark: creative gradients and playful curves */
h1 {
    background: linear-gradient(135deg, var(--primary), var(--accent));
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    font-weight: 700;
}

a {
    font-weight: 500;
}

a:hover {
    color: var(--accent);
    border-bottom-color: var(--accent);
}

pre {
    background: linear-gradient(135deg, #faf5ff, #fdf4ff);
    border: 1px solid #e9d5ff;
}

pre::before {
    content: "";
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    height: 4px;
    background: linear-gradient(90deg, var(--primary), var(--accent));
    border-radius: var(--code-radius) var(--code-radius) 0 0;
}

blockquote {
    border-left-width: 4px;
    border-image: linear-gradient(to bottom, var(--primary), var(--accent)) 1;
}

th {
    background: linear-gradient(135deg, var(--primary), var(--accent));
}

hr {
    background: linear-gradient(90deg, var(--primary), var(--accent));
    height: 3px;
    max-width: 120px;
    border-radius: 2px;
}

tr:nth-child(even) td {
    background: var(--alt-row-bg);
}
""",
    "thesis": """
/* Thesis: academic precision and readability */
body {
    font-size: 12pt;
}

.markdown-body {
    max-width: 6.5in;
    text-align: justify;
    hyphens: auto;
    -webkit-hyphens: auto;
}

h1, h2, h3, h4, h5, h6 {
    text-align: left;
    hyphens: none;
}

h1 {
    text-align: center;
    margin-top: 2em;
    margin-bottom: 1em;
    font-variant: small-caps;
    font-size: 1.8em;
    letter-spacing: 0.05em;
}

h2 {
    margin-top: 1.5em;
}

code {
    font-size: 10pt;
}

pre {
    font-size: 10pt;
    box-shadow: none;
    border-radius: 0;
}

pre code {
    font-size: 10pt;
}

blockquote {
    font-style: italic;
    margin: 1.5em 2em;
    border-radius: 0;
}

th, td {
    font-size: 11pt;
}

table {
    box-shadow: none;
    border-radius: 0;
}

img {
    border-radius: 0;
    box-shadow: none;
}

hr {
    max-width: none;
    height: 1px;
    margin: 2em 0;
}

/* Footnote styling */
.footnote {
    font-size: 10pt;
}
""",
    "obsidian": """
/* Obsidian: dark elegance with purple glow */
.markdown-body {
    background: var(--background);
}

h1 {
    color: var(--primary);
}

h1, h2 {
    text-shadow: 0 0 30px rgba(168, 85, 247, 0.3);
}

a:hover {
    color: var(--accent);
    border-bottom-color: var(--accent);
}

code {
    border: 1px solid var(--border);
}

pre code {
    border: none;
}

pre {
    border: 1px solid var(--border);
    box-shadow: 0 0 20px rgba(168, 85, 247, 0.08);
}

pre::before {
    content: "";
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    height: 3px;
    background: linear-gradient(90deg, var(--primary), var(--accent));
    border-radius: var(--code-radius) var(--code-radius) 0 0;
}

table {
    box-shadow: 0 0 20px rgba(168, 85, 247, 0.06);
}

tr:nth-child(even) td {
    background: var(--alt-row-bg);
}

hr {
    background: linear-gradient(90deg, transparent, var(--primary), transparent);
    max-width: none;
    height: 1px;
}

img {
    box-shadow: 0 0 20px rgba(168, 85, 247, 0.1);
}
""",
    "blueprint": """
/* Blueprint: precise technical documentation */
h1 {
    border-bottom: 2px solid var(--primary);
    padding-bottom: 0.5em;
}

h2 {
    color: var(--primary);
}

a {
    font-weight: 500;
}

code {
    font-size: 0.85em;
}

pre {
    border-left: 4px solid var(--primary);
    border-radius: 0 var(--code-radius) var(--code-radius) 0;
}

blockquote {
    background: var(--blockquote-bg);
}

/* Callout styling: blockquote with bold first line */
blockquote strong:first-child {
    color: var(--primary);
}

th {
    text-transform: uppercase;
    font-size: 0.8em;
    letter-spacing: 0.06em;
}

tr:nth-child(even) td {
    background: var(--alt-row-bg);
}

hr {
    background: var(--primary);
    max-width: 80px;
    height: 3px;
}
""",
}


def get_theme_css(theme_name: str, for_pdf: bool = False) -> str:
    """Get complete CSS for a theme.

    Returns CSS custom properties + base structural CSS + theme extras.
    Optionally appends @page/@media print rules for PDF generation.

    Args:
        theme_name: Name of the theme
        for_pdf: If True, append print/PDF-specific CSS rules

    Returns:
        Complete CSS string

    Raises:
        ValueError: If theme name is not recognized
    """
    theme = get_theme(theme_name)

    # CSS custom properties (must come first so var() references resolve)
    css = _generate_custom_properties(theme)

    # Base structural CSS
    css += "\n" + BASE_CSS

    # Per-theme structural extras
    extras = THEME_EXTRAS.get(theme_name, "")
    if extras:
        css += "\n" + extras

    # Print/PDF rules
    if for_pdf:
        css += "\n" + PRINT_CSS

    return css
