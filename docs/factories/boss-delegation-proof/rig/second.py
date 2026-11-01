"""The second live round: the Boss answers the owner while a seat it started is working."""
import json
import os
import re
import time
import urllib.request
from pathlib import Path

ROOT = Path(os.environ["LOCALAPPDATA"]) / "cc-director-restart-qa-rig"
TOKEN = (ROOT / "config" / "director" / "gateway-token.txt").read_text().strip()
DIRECTOR = "94977813-bd4f-45f2-bbeb-0ff2ffe9e121"
SCRIBE = "0d07efa8-55b9-4ed3-8585-c6ea992a492e"
LOG = Path(__file__).resolve().parent.parent / "transcript.txt"


def call(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request("http://127.0.0.1:7911" + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=60) as resp:
        return resp.read().decode()


def screen(sid):
    text = call("GET", f"/sessions/{sid}/buffer")
    text = re.sub(r"\\u001b\[[0-9;?]*[a-zA-Z]", " ", text)
    return re.sub(r"[^\x20-\x7E]", "", text)


def say(line):
    print(line)
    with LOG.open("a", encoding="ascii", errors="replace") as out:
        out.write(line + "\n")


def wait(what, test, limit):
    end = time.time() + limit
    while time.time() < end:
        found = test()
        if found:
            say(f"[{time.strftime('%H:%M:%S')}] {what}: {found}")
            return found
        time.sleep(4)
    say(f"[{time.strftime('%H:%M:%S')}] TIMED OUT: {what}")
    return None


def sessions():
    return json.loads(call("GET", "/sessions"))


say("--- round two: a Boss that stays open, as an owner's talk does ---")
BOSS = "db3c7f18-d0f0-44e5-b3e2-a2876223501c"
EARLIER = {s["sessionId"] for s in sessions()}

call("POST", f"/sessions/{BOSS}/prompt", {"text": "Run: cc-devthrottle factory seat start --seat scribe --name slow-count --prompt \"Run the shell command sleep 120 and then hand back the word FINISHED\" . Do not wait for it and do not watch it. End your turn as soon as it is started."})
wait("a second Scribe is running", lambda: next((s["sessionId"] for s in sessions() if s.get("factorySeat") == "scribe" and s.get("factoryKit") == "seat" and s["sessionId"] not in EARLIER), None), 240)
second = next(s["sessionId"] for s in sessions() if s.get("factorySeat") == "scribe" and s.get("factoryKit") == "seat" and s["sessionId"] not in EARLIER)
wait("the Boss ended its turn", lambda: next((s["activityState"] for s in sessions() if s["sessionId"] == BOSS and s["activityState"] != "Working"), None), 120)

state = next(s["activityState"] for s in sessions() if s["sessionId"] == second)
say(f"the second Scribe is {state} as the owner asks the Boss a question")
call("POST", f"/sessions/{BOSS}/prompt", {"text": "Owner here. Quick question: what is 7 times 6? Answer with the word ANSWER then the number."})
wait("the Boss answered", lambda: (m.group(0) if (m := re.search(r"ANSWER\W{0,3}42", screen(BOSS))) else None), 120)
state = next((s["activityState"] for s in sessions() if s["sessionId"] == second), "gone")
say(f"the second Scribe is {state} when the Boss has answered")
wait("the second Scribe came back to the Boss", lambda: "FINISHED" if screen(BOSS).count("FINISHED") > 2 else None, 300)
say("final sessions: " + json.dumps([{k: s.get(k) for k in ("sessionId", "name", "activityState", "controllerSessionId", "factorySeat", "factoryKit", "originKind")} for s in sessions()], indent=1))
say("runs: " + call("GET", "/cron/jobs/cj_8d2546/runs")[:900])
say("activity: " + call("GET", "/gateway/factory/activity?factory=delegation-proof")[:1800])
