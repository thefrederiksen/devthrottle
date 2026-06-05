# Capture one questionnaire-shape example for TURN_BRIEFING v2:
# the screen grid (what the user sees), the transcript widgets (what the JSONL knows),
# and the v1 /brief output (what the rule-based Brief made of it) - side by side.
#
#   python capture.py <port> <sid> <label>
#
# Writes capture-<label>.md next to this script. ASCII-only output.
import json, re, sys, urllib.request, html, io

port, sid, label = sys.argv[1], sys.argv[2], sys.argv[3]
base = f"http://127.0.0.1:{port}/sessions/{sid}"

def get(path):
    with urllib.request.urlopen(base + path, timeout=30) as r:
        return json.loads(r.read().decode("utf-8"))

grid = get("/buffer/html")
rows = []
for row in grid.get("gridHtml", "").split('<div class="line">'):
    text = html.unescape(re.sub(r"<[^>]+>", "", row)).rstrip()
    if text.strip():
        rows.append(text)
screen = "\n".join(rows[-25:])

turns = get("/turns")
widgets = "\n".join(
    f"  {i}. {w['kind']}{' (pending)' if w.get('isPending') else ''}: "
    f"{(w.get('content') or '')[:100].replace(chr(10), ' ')}"
    for i, w in enumerate(turns.get("widgets", [])))

try:
    brief = get("/brief")
except Exception as e:
    brief = {"error": str(e)}

out = io.StringIO()
out.write(f"# Capture: {label}\n\n")
out.write(f"Session {sid}, Director :{port}, state at capture: {brief.get('activityState','?')}\n\n")
out.write("## What the USER SEES (screen grid, last 25 rows)\n\n```\n" + screen + "\n```\n\n")
out.write("## What the TRANSCRIPT knows (parsed widgets)\n\n```\n" + (widgets or "  (empty)") + "\n```\n\n")
out.write("## What Brief v1 made of it (GET /brief)\n\n```json\n")
out.write(json.dumps({k: brief.get(k) for k in
    ("status", "activityState", "replyPending", "goal", "lastAsk",
     "didBullets", "needsYou", "needsYouSource", "condenser")}, indent=2)[:3000])
out.write("\n```\n\n## Correct TurnBrief (authored by the strong model - the quality bar)\n\nTODO\n")

name = f"capture-{label}.md"
with open(name, "w", encoding="ascii", errors="replace") as f:
    f.write(out.getvalue())
print("wrote", name)
