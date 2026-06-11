# Proof harness for issue #325: drives the break/flag/restore/clear timeline against a
# LOCAL, ISOLATED proof Gateway (CC_GATEWAY_NO_TAILSCALE=1, CC_TURNBRIEFS=0).
#
# It simulates exactly the scenario of the acceptance criteria: registered Directors whose
# HEARTBEATS CONTINUE the whole time while their advertised endpoint is broken and then
# restored. The Director side of the wire contract (register / heartbeat / healthz with
# directorId echo) is the same one a real Director uses; the network hop (loopback vs
# tailnet) does not change the Gateway logic under test.
#
# Phases:
#   A  spawn listeners, register 3 synthetic Directors, heartbeat every 10 s forever
#   B  wait 40 s with the breakable endpoint healthy (probe stamps "ok")
#   C  BREAK: kill the breakable listener (heartbeats continue) -> expect
#      advertisedEndpointState = "unreachable-by-name" within 30 s
#   D  RESTORE: respawn the listener -> expect state back to "ok" within 30 s
#   E  BREAK again and HOLD (for the Cockpit screenshot), until this process is killed
#
# The timeline (2 s polls of GET /directors) is written to timeline.txt next to this file,
# with explicit BREAK/RESTORE/FLAGGED/CLEARED marker lines and UTC timestamps.
#
# Usage: python driver.py
import datetime
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request

GW = "http://127.0.0.1:7978"
HERE = os.path.dirname(os.path.abspath(__file__))
TIMELINE = os.path.join(HERE, "timeline.txt")
PY = sys.executable

BREAKABLE = {"directorId": "proof-dir-325", "port": 7984, "machineName": "PROOF-BREAKABLE"}
HEALTHY = {"directorId": "proof-dir-healthy", "port": 7981, "machineName": "PROOF-HEALTHY"}
CTLLOST = {"directorId": "proof-dir-ctllost", "port": 7982, "machineName": "PROOF-CTLLOST"}


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"


_log_lock = threading.Lock()


def log(line):
    with _log_lock:
        with open(TIMELINE, "a", encoding="ascii") as f:
            f.write(line + "\n")
    print(line, flush=True)


def post(path, payload=None):
    data = json.dumps(payload).encode() if payload is not None else b""
    req = urllib.request.Request(GW + path, data=data, method="POST",
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=5) as resp:
        return resp.status


def get_directors():
    with urllib.request.urlopen(GW + "/directors", timeout=5) as resp:
        return json.loads(resp.read().decode())


def register(d):
    status = post("/directors/register", {
        "directorId": d["directorId"],
        "pid": os.getpid(),
        "tailnetEndpoint": f"http://127.0.0.1:{d['port']}",
        "machineName": d["machineName"],
        "user": "proof-harness",
        "version": "proof-325",
    })
    log(f"{now()} REGISTERED {d['directorId']} endpoint=http://127.0.0.1:{d['port']} -> HTTP {status}")


def heartbeat_forever():
    while True:
        time.sleep(10)
        for d in (BREAKABLE, HEALTHY, CTLLOST):
            try:
                post(f"/directors/{d['directorId']}/heartbeat")
            except urllib.error.HTTPError as e:
                if e.code == 410:  # swept: re-register, same as a real Director's client
                    register(d)
                else:
                    log(f"{now()} HEARTBEAT {d['directorId']} unexpected HTTP {e.code}")
            except Exception as e:
                log(f"{now()} HEARTBEAT {d['directorId']} failed: {e}")


def spawn_listener(d, extra=None):
    args = [PY, os.path.join(HERE, "listener.py"), str(d["port"]), d["directorId"]]
    if extra:
        args.append(extra)
    return subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def state_of(director_id):
    for d in get_directors():
        if d.get("directorId") == director_id:
            return d.get("advertisedEndpointState"), d.get("advertisedEndpointError")
    return "(not registered)", None


def watch_until(director_id, want_state, marker, timeout_s=90):
    deadline = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < deadline:
        state, error = state_of(director_id)
        if state != last:
            log(f"{now()} STATE {director_id} advertisedEndpointState={state} error={error}")
            last = state
        if state == want_state:
            log(f"{now()} {marker}")
            return True
        time.sleep(2)
    log(f"{now()} TIMEOUT waiting for {director_id} -> {want_state}")
    return False


def main():
    open(TIMELINE, "w").close()
    log(f"{now()} PROOF issue #325 against {GW} (isolated proof gateway)")

    # Phase A: listeners + registration + heartbeats (heartbeats NEVER stop below).
    breakable_proc = spawn_listener(BREAKABLE)
    spawn_listener(HEALTHY)
    spawn_listener(CTLLOST, "sessions500")
    time.sleep(1)
    for d in (BREAKABLE, HEALTHY, CTLLOST):
        register(d)
    threading.Thread(target=heartbeat_forever, daemon=True).start()

    # Phase B: prove the healthy stamp first.
    if not watch_until(BREAKABLE["directorId"], "ok", "BASELINE-OK (probe verified the advertised endpoint)"):
        return 1

    # Phase C: the break - the advertised endpoint dies, heartbeats continue.
    breakable_proc.kill()
    break_at = time.monotonic()
    log(f"{now()} BREAK advertised endpoint of {BREAKABLE['directorId']} killed (heartbeats continue)")
    if not watch_until(BREAKABLE["directorId"], "unreachable-by-name", "FLAGGED"):
        return 1
    log(f"{now()} FLAG-LATENCY {time.monotonic() - break_at:.1f}s after the break (criterion: <= 30s)")

    # Phase D: the restore - no restart of Gateway or Director.
    breakable_proc = spawn_listener(BREAKABLE)
    restore_at = time.monotonic()
    log(f"{now()} RESTORE advertised endpoint of {BREAKABLE['directorId']} relaunched")
    if not watch_until(BREAKABLE["directorId"], "ok", "CLEARED"):
        return 1
    log(f"{now()} CLEAR-LATENCY {time.monotonic() - restore_at:.1f}s after the restore (criterion: <= 30s)")

    # Phase E: break again and hold for the Cockpit screenshot.
    breakable_proc.kill()
    log(f"{now()} BREAK-AND-HOLD for the Cockpit screenshot (kill this process to end)")
    watch_until(BREAKABLE["directorId"], "unreachable-by-name", "SCREENSHOT-READY")
    while True:
        time.sleep(30)


if __name__ == "__main__":
    sys.exit(main())
