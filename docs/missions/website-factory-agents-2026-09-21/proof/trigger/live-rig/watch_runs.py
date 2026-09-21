"""Foreground watcher for one trigger on the isolated rig: every 10 seconds, print each NEW run row of the trigger and
the sessions the rig Gateway knows. Usage: watch_runs.py <trigger id> <seconds>. Reads only; changes nothing."""
import json, sys, time, urllib.request

GW = "http://127.0.0.1:7898"


def get(path):
    with urllib.request.urlopen(GW + path, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


trigger, secs = sys.argv[1], int(sys.argv[2])
seen = set()
end = time.time() + secs
while time.time() < end:
    runs = get(f"/triggers/{trigger}/runs?limit=20")["runs"]
    for r in reversed(runs):
        if r["id"] in seen:
            continue
        seen.add(r["id"])
        print(time.strftime("%H:%M:%S"), "RUN checked", r["checkedUtc"], "recorded", r["recordedUtc"], "|", r["outcome"],
              "| count", r["count"], "| session", r["sessionId"], "|", r["reason"], flush=True)
    sessions = get("/sessions")
    rows = sessions if isinstance(sessions, list) else sessions.get("sessions", [])
    live = [(s.get("sessionId", "")[:8], s.get("name") or s.get("customName"), s.get("activityState")) for s in rows]
    print(time.strftime("%H:%M:%S"), "sessions:", live, flush=True)
    time.sleep(10)
