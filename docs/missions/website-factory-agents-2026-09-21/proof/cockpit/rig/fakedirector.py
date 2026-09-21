"""A stand-in Director for the QA rig: it opens the real Director stream (SignalR, JSON protocol), says Hello, and
pushes a session snapshot, so the Gateway's roster holds real sessions for Screen 6 and for the trigger lock.
It never starts a session - the rig has no real Director behind it."""
import os
import threading
import time
from datetime import datetime, timezone

from signalrcore.hub_connection_builder import HubConnectionBuilder

GW = "http://127.0.0.1:7899"
DIRECTOR = "qa-rig-director"
MACHINE = os.environ["COMPUTERNAME"]


def iso(dt):
    return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def session(sid, name, state="Working", created=None):
    return {"sessionId": sid, "directorId": DIRECTOR, "agent": "ClaudeCode", "agentToolDisplay": "Claude Code",
            "repoPath": r"D:\sites\website-business", "status": "Running", "activityState": state,
            "createdAt": iso(created or datetime.now(timezone.utc)), "name": name}


class FakeDirector:
    def __init__(self):
        self.ready = threading.Event()
        self.hub = HubConnectionBuilder().with_url(f"{GW}/director-stream").build()
        self.hub.on_open(lambda: self.ready.set())
        self.seq = 0

    def start(self):
        self.hub.start()
        if not self.ready.wait(15):
            raise SystemExit("the Director stream did not open")
        hello = {"directorId": DIRECTOR, "version": "qa-rig", "machineName": MACHINE, "user": "qa",
                 "pid": os.getpid(), "startedAt": iso(datetime.now(timezone.utc))}
        done = threading.Event()
        result = {}
        self.hub.send("Hello", [hello], on_invocation=lambda m: (result.update(r=m), done.set()))
        if not done.wait(15):
            raise SystemExit("Hello got no answer")
        print("hello answered:", str(result.get("r"))[:120])

    def push(self, sessions):
        self.seq += 1
        done = threading.Event()
        self.hub.send("PushSnapshot", [self.seq, sessions], on_invocation=lambda m: done.set())
        if not done.wait(15):
            raise SystemExit("PushSnapshot got no answer")
        print(f"pushed {len(sessions)} session(s), sequence {self.seq}")

    def stop(self):
        self.hub.stop()


if __name__ == "__main__":
    import requests
    d = FakeDirector()
    d.start()
    d.push([session("134", "website-business front-desk: New business mail")])
    time.sleep(2)
    print(requests.get(f"{GW}/sessions").text[:600])
    d.stop()
