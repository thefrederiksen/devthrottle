#!/usr/bin/env bash
# Build the Cockpit design doc: render the diagram (PNG + crisp SVG) and the
# HTML, then post-process so the diagram is full-width and click-to-open.
set -e
cd "$(dirname "$0")"

D2="/d/Tools/d2/d2.exe"
"$D2" --theme=0 --layout=elk cockpit-topology.d2 cockpit-topology.png
"$D2" --theme=0 --layout=elk cockpit-topology.d2 cockpit-topology.svg

cc-html from-markdown COCKPIT_DESIGN.md -o COCKPIT_DESIGN.html --theme boardroom
python build-html.py

echo "Done: COCKPIT_DESIGN.html"
