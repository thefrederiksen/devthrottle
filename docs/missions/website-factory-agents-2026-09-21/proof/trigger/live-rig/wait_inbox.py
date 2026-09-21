"""Foreground wait for a message: checks the inbox every 45 seconds, exits when one arrives, under 9 minutes."""
import json, subprocess, time
end = time.time() + 8.5 * 60
while time.time() < end:
    r = subprocess.run(["C:/Users/soren/AppData/Local/cc-director/bin/cc-devthrottle.cmd", "message", "inbox", "--json"], capture_output=True, text=True)
    try:
        d = json.loads(r.stdout)
    except ValueError:
        print("inbox read failed:", r.stdout[:500], r.stderr[:500], flush=True)
        raise SystemExit(2)
    if d.get("unreadCount", 0) > 0:
        print(json.dumps(d["unread"], indent=1))
        raise SystemExit(0)
    print(time.strftime("%H:%M:%S"), "no message yet", flush=True)
    time.sleep(45)
print("no message within this run")
