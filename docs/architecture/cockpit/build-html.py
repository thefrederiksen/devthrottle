"""Post-process the cc-html boardroom output so the topology diagram is readable.

cc-html base64-embeds images inside an 800px-wide .markdown-body column, which
shrinks the wide landscape diagram to an unreadable thumbnail. This injects a
style override (after the theme, so it wins) that:
  - keeps body text at a comfortable width, but
  - lets the diagram break out to full viewport width, and
  - wraps it in a link to the crisp SVG so a click opens it full screen / zoomable.

Run AFTER generating COCKPIT_DESIGN.html with cc-html. See build-html.sh.
"""

import re
import pathlib

HERE = pathlib.Path(__file__).parent
HTML = HERE / "COCKPIT_DESIGN.html"

OVERRIDE = """
<style>
/* --- diagram-readability override (priority: the diagram) --- */
.markdown-body { max-width: 1100px; }
.markdown-body a.diagram-zoom {
  display: block;
  width: 96vw;
  position: relative;
  left: 50%;
  transform: translateX(-50%);
  margin: 1.5rem 0;
  text-decoration: none;
}
.markdown-body a.diagram-zoom img {
  width: 100%;
  height: auto;
  border-radius: 8px;
  box-shadow: 0 2px 18px rgba(0,0,0,.18);
}
.markdown-body a.diagram-zoom::after {
  content: "Click the diagram to open it full screen (crisp, zoomable SVG)";
  display: block;
  text-align: center;
  font-size: 13px;
  color: #777;
  margin-top: 8px;
}
</style>
"""

IMG_RE = re.compile(
    r'<img alt="Cockpit fleet topology[^"]*"\s+src="data:image/png;base64,[^"]*"\s*/?>'
)


def main() -> None:
    html = HTML.read_text(encoding="utf-8")

    if "</head>" in html and "diagram-zoom" not in html:
        html = html.replace("</head>", OVERRIDE + "</head>", 1)

    def wrap(m: "re.Match[str]") -> str:
        return (
            '<a class="diagram-zoom" href="cockpit-topology.svg" target="_blank" '
            'title="Open full screen">' + m.group(0) + "</a>"
        )

    html, n = IMG_RE.subn(wrap, html, count=1)
    if n == 0:
        raise SystemExit("ERROR: diagram <img> tag not found - did the alt text change?")

    HTML.write_text(html, encoding="utf-8")
    print("OK: diagram is now full-width + click-to-open-SVG")


if __name__ == "__main__":
    main()
