"""Factory Boxes phase 1, the test Director on devlinux: a box id the Director does not list, refused AT THE DIRECTOR.

The Gateway looks a box up in the list the Director pushed before it opens any desktop stream, so a box id nobody lists is
refused by the Gateway (404) and never reaches a Director. The Director's own refusal is reached in the one real case
where the two disagree: the owner takes a box out of hosted-desktops.json. The Director re-reads the file at its next
re-push (every ten seconds) and pushes the shorter list straight after; a desktop request that lands between the
Director's re-read and the Gateway taking the push is sent to the Director, which no longer lists the box and refuses it.

This script holds a valid desktop session (a ticket redeemed for the box cookie), keeps several request loops (four unless a sixth argument says otherwise) running at
the desktop page, empties the list file, and records every answer until the Gateway itself stops routing the box. Then
it puts the file back exactly as it was.

Usage: python3 refusal-at-the-director.py <main origin> <key> <box id> <hosted-desktops.json> <out file> [loops]
"""
import http.client, json, sys, threading, time, urllib.parse, urllib.request

main_origin, key, box, list_file, out = sys.argv[1:6]
lines = []
def say(s):
    line = f"{time.strftime('%H:%M:%S')}.{int(time.time() * 1000) % 1000:03d} {s}"
    lines.append(line); print(line, flush=True)

# 1. A ticket from the main host, redeemed on the desktop host for the box cookie.
req = urllib.request.Request(f"{main_origin}/boxes/{box}/desktop-ticket", method="POST", headers={"Authorization": f"Bearer {key}"})
ticket = json.load(urllib.request.urlopen(req))
u = urllib.parse.urlsplit(ticket["url"])
conn = http.client.HTTPConnection(u.hostname, u.port)
conn.request("GET", f"{u.path}?{u.query}")
r = conn.getresponse(); r.read()
cookie = r.getheader("Set-Cookie").split(";")[0]
say(f"ticket redeemed on the desktop host: HTTP {r.status}, cookie {cookie.split('=')[0]} set")

# 2. Four loops asking for the desktop page with that cookie.
results, stop = [], threading.Event()
def loop():
    c = http.client.HTTPConnection(u.hostname, u.port, timeout=10)
    while not stop.is_set():
        try:
            c.request("GET", u.path, headers={"Cookie": cookie})
            resp = c.getresponse(); body = resp.read()
            results.append((time.time(), resp.status, body[:300].decode("utf-8", "replace")))
        except Exception as e:  # a dropped connection is recorded, and the loop goes on with a fresh one
            results.append((time.time(), -1, repr(e))); c = http.client.HTTPConnection(u.hostname, u.port, timeout=10)
        time.sleep(0.005)
threads = [threading.Thread(target=loop) for _ in range(int(sys.argv[6]) if len(sys.argv) > 6 else 4)]
for t in threads: t.start()
time.sleep(2)
say(f"before the edit: {len(results)} answers, statuses {sorted({s for _, s, _ in results})}")

# 3. Take the box out of the list. The Director reads the file again at its next re-push.
original = open(list_file).read()
edited_at = time.time()
open(list_file, "w").write('{ "desktops": [] }\n')
say("hosted-desktops.json emptied")
try:
    deadline = time.time() + 30
    while time.time() < deadline:
        recent = [s for t, s, _ in results if t > time.time() - 1.0]
        if recent and all(s != 200 for s in recent) and time.time() - edited_at > 2:
            break
        time.sleep(0.2)
    time.sleep(1.5)
finally:
    stop.set()
    for t in threads: t.join()
    open(list_file, "w").write(original)
    say("hosted-desktops.json put back as it was")

# 4. What came back after the edit, grouped by status and answer.
after = [(t, s, b) for t, s, b in results if t >= edited_at]
groups = {}
for t, s, b in after:
    g = groups.setdefault((s, b.strip()), {"count": 0, "first": t, "last": t})
    g["count"] += 1; g["last"] = t
for (s, b), g in sorted(groups.items(), key=lambda kv: kv[1]["first"]):
    say(f"after the edit: HTTP {s} x{g['count']} from +{g['first'] - edited_at:.3f}s to +{g['last'] - edited_at:.3f}s: {b[:200]!r}")
open(out, "w").write("\n".join(lines) + "\n")
