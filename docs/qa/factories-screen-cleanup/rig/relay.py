"""Read-only relay for screenshots: forwards GET requests to the live Gateway with this session's key, and refuses
everything else. MODE (a file next to this script) picks what is changed in the Factories list view:
  before          - nothing: the live answer as it is.
  after-live      - the fields this branch adds (tab, draft pill and seat count), on the live data.
  after-published - as after-live, with Erik Laursen's factory drawn as published (a stand-in row, written here).
"""
import json
import os
import sys
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

GW = os.environ["CC_GATEWAY_URL"].rstrip("/")
KEY = os.environ["CC_GATEWAY_SESSION_KEY"]
HERE = Path(__file__).resolve().parent


def mode() -> str:
    return (HERE / "MODE").read_text().strip()


def patch_list(d: dict, m: str) -> dict:
    if m == "before":
        return d
    if not any(t["key"] == "manager" for t in d["tabs"]):
        d["tabs"].append({"key": "manager", "label": "Factory Manager"})
    for r in d["draftRows"]:
        r["stateWord"] = "DRAFT"
        r["stateTone"] = "grey"
        r["seatCountText"] = "1 seat"
    for r in d["rows"]:
        r.setdefault("seatCountText", "1 seat")
        r.setdefault("nextStartText", None)
        r.setdefault("lastRunText", None)
        r.setdefault("lastRunTone", None)
    if m == "after-published":
        d["draftRows"] = [r for r in d["draftRows"] if r["id"] != "erik-laursen"]
        d["emptyText"] = None
        d["rows"] = [{
            "id": "erik-laursen", "title": "Erik Laursen",
            "statusWord": "RUNNING", "statusTone": "ok", "statusRank": 3,
            "statusReason": "Nothing failed and nothing is waiting on you.", "statusLine": None, "statusHref": None,
            "waitingText": "-", "waitingCount": 0, "waitingHref": None,
            "href": "/factories/erik-laursen",
            "talk": {"label": "Talk to the boss", "busyLabel": "Starting the talk with the boss...",
                     "factoryId": "erik-laursen", "seatId": "boss"},
            "noBossText": None,
            "purpose": "Answers Erik Laursen's website requests from the duksrevo inbox",
            "bossName": "Boss", "leftOpenText": None, "leftOpenHref": None,
            "seatCountText": "1 seat",
            "nextStartText": "Next start tomorrow 08:00 (Boss)",
            "lastRunText": "Last run: Boss, today 08:00 - succeeded",
            "lastRunTone": "ok",
        }]
    return d


def get_live(path: str):
    req = urllib.request.Request(GW + path, headers={"Authorization": f"Bearer {KEY}", "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def manager_card() -> bytes:
    """The Factory Manager card as FactoryManagerEndpoints.CardOf folds it for this account today: no session bound and
    no computer or folder saved (cc-devthrottle factory manager show), the running Directors' computers, and the live
    name check. A session key may read each of those, but not the card itself."""
    directors = get_live("/directors")
    directors = directors if isinstance(directors, list) else directors.get("directors", [])
    computers = sorted({d["machineName"] for d in directors if d.get("machineName") and not d.get("stoppedAtUtc")},
                       key=str.lower)
    card = {
        "title": "Factory Manager",
        "purpose": "The one seat over all your factories. Ask it about any factory: it points you to that factory's boss, "
                   "chases each open problem with the boss concerned, and deals itself with what no single boss owns.",
        "stateText": "Not started yet", "stateTone": "grey", "sessionHref": None,
        "actionLabel": "Talk to the Factory Manager", "busyLabel": "Starting the Factory Manager...",
        "needsPlacement": True, "computers": computers,
        "placementText": "Choose the computer it runs on and a folder there for it to work in - for example the folder "
                         "that holds your factories. They are saved for every later start.",
        "computerLabel": "Computer", "folderLabel": "Folder", "placementSavedText": None,
        "nameCheck": get_live("/gateway/factory/name-check"),
    }
    return json.dumps(card).encode()


class Relay(BaseHTTPRequestHandler):
    def do_GET(self):
        # The one route a session key may not read. Factories is on for this account (the owner's own screenshot shows
        # the page), so the relay answers it rather than leave the page on its error.
        if self.path.split("?")[0] == "/gateway/factory-agents/switch":
            body = b'{"enabled":true,"howToStart":null}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.path.split("?")[0] == "/gateway/factory-agents/factory-manager":
            body = manager_card()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        req = urllib.request.Request(GW + self.path, headers={"Authorization": f"Bearer {KEY}", "Accept": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                status, body, ctype = resp.status, resp.read(), resp.headers.get("Content-Type", "application/json")
        except urllib.error.HTTPError as e:
            status, body, ctype = e.code, e.read(), e.headers.get("Content-Type", "application/json")
        if status == 200 and self.path.split("?")[0] == "/gateway/factories":
            body = json.dumps(patch_list(json.loads(body), mode())).encode()
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def refuse(self):
        body = b'{"error":"the screenshot relay is read-only"}'
        self.send_response(405)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    do_POST = do_PUT = do_DELETE = do_PATCH = refuse

    def log_message(self, fmt, *args):
        sys.stderr.write("[relay] " + (fmt % args) + "\n")


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Relay).serve_forever()
