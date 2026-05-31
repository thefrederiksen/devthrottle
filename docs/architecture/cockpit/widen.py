"""Widen a cc-html boardroom doc to use the full screen (tables especially).

cc-html centers content in an 800px column, which crams wide tables. This injects
a style override (after the theme, so it wins) that lets the content fill the
viewport. Usage: python widen.py <htmlfile>
"""

import sys
import pathlib

OVERRIDE = """
<style>
/* --- full-screen override --- */
.markdown-body { max-width: none; width: 94vw; margin: 0 auto; }
.markdown-body table { width: 100%; }
.markdown-body img { max-width: 100%; height: auto; }
</style>
"""


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: python widen.py <htmlfile>")
    path = pathlib.Path(sys.argv[1])
    html = path.read_text(encoding="utf-8")
    if "full-screen override" not in html and "</head>" in html:
        html = html.replace("</head>", OVERRIDE + "</head>", 1)
        path.write_text(html, encoding="utf-8")
        print(f"OK: widened {path.name}")
    else:
        print(f"skip: {path.name} already widened or no </head>")


if __name__ == "__main__":
    main()
