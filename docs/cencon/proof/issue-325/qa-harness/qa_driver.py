# QA Agent INDEPENDENT verification driver for issue #325 (written by the QA Agent,
# not reusing the Developer's harness run). Drives break/flag/restore/clear against
# an ISOLATED QA Gateway on port 7975 (CC_GATEWAY_NO_TAILSCALE=1, CC_TURNBRIEFS=0).
#
# Verifies, with UTC-timestamped GET /directors polls (1 s cadence):
#   AC1: break the advertised endpoint while heartbeats continue ->
#        advertisedEndpointState = "unreachable-by-name" within 30 s
#   AC2: restore the endpoint -> state back to "ok" within 30 s, no restarts
#   Impostor guard live check: an endpoint answering as a DIFFERENT directorId is flagged
#   Heartbeats observed continuing during the flagged window (director never swept)
#
# Phases:
#   A  spawn listeners, register 3 synthetic Directors, heartbeat every 10 s forever
#   B  wait for baseline "ok" on the breakable Director
#   C  BREAK (kill the listener), measure flag latency
#   D  RESTORE (respawn), measure clear latency
#   E  swap the restored listener for a WRONG-ID impostor listener -> expect flagged
#   F  restore the true listener, expect cleared, then BREAK-AND-HOLD for the
#      Cockpit screenshot (3 distinct states side by side)
#
# Usage: python qa_driver.py
import datetime
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request
import urllib.error

GW = "http://127.0.0.1:7975"
HERE = os.path.dirname(os.path.abspath(__file__))
TIMELINE = os.path.join(HERE, "qa-timeline.txt")
PY = sys.executable

BREAKABLE = {"directorId": "qa-dir-breakable", "port": 7985, "machineName": "QA-BREAKABLE"}
HEALTHY = {"directorId": "qa-dir-healthy", "port": 7986, "machineName": "QA-HEALTHY"}
CTLLOST = {"directorId": "qa-dir-ctllost", "port": 7987, "machineName": "QA-CTLLOST"}

heartbeat_count = {}


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
        "user": "qa-agent",
        "version": "qa-proof-325",
    })
    log(f"{now()} REGISTERED {d['directorId']} endpoint=http://127.0.0.1:{d['port']} -> HTTP {status}")


def heartbeat_forever():
    while True:
        time.sleep(10)
        for d in (BREAKABLE, HEALTHY, CTLLOST):
            try:
                post(f"/directors/{d['directorId']}/heartbeat")
                heartbeat_count[d["directorId"]] = heartbeat_count.get(d["directorId"], 0) + 1
            except urllib.error.HTTPError as e:
                if e.code == 410:
                    log(f"{now()} HEARTBEAT {d['directorId']} got 410 (SWEPT - heartbeat contract broken?) re-registering")
                    register(d)
                else:
                    log(f"{now()} HEARTBEAT {d['directorId']} unexpected HTTP {e.code}")
            except Exception as e:
                log(f"{now()} HEARTBEAT {d['directorId']} failed: {e}")


def spawn_listener(d, mode=None):
    args = [PY, os.path.join(HERE, "qa_listener.py"), str(d["port"]), d["directorId"]]
    if mode:
        args.append(mode)
    return subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def state_of(director_id):
    for d in get_directors():
        if d.get("directorId") == director_id:
            return d.get("advertisedEndpointState"), d.get("advertisedEndpointError"), d.get("advertisedEndpointUnreachableSince")
    return "(not registered)", None, None


def watch_until(director_id, want_state, marker, timeout_s=90):
    deadline = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < deadline:
        state, error, since = state_of(director_id)
        if state != last:
            log(f"{now()} STATE {director_id} advertisedEndpointState={state} unreachableSince={since} error={error}")
            last = state
        if state == want_state:
            log(f"{now()} {marker}")
            return True
        time.sleep(1)
    log(f"{now()} TIMEOUT waiting for {director_id} -> {want_state}")
    return False


def main():
    open(TIMELINE, "w").close()
    log(f"{now()} QA INDEPENDENT PROOF issue #325 against {GW} (isolated QA gateway, port 7975)")

    # Phase A
    breakable_proc = spawn_listener(BREAKABLE)
    spawn_listener(HEALTHY)
    spawn_listener(CTLLOST, "sessions500")
    time.sleep(1)
    for d in (BREAKABLE, HEALTHY, CTLLOST):
        register(d)
    threading.Thread(target=heartbeat_forever, daemon=True).start()

    # Phase B: baseline ok
    if not watch_until(BREAKABLE["directorId"], "ok", "BASELINE-OK"):
        return 1

    # Phase C: BREAK
    hb_before = heartbeat_count.get(BREAKABLE["directorId"], 0)
    breakable_proc.kill()
    break_at = time.monotonic()
    log(f"{now()} BREAK advertised endpoint of {BREAKABLE['directorId']} killed (heartbeats continue)")
    if not watch_until(BREAKABLE["directorId"], "unreachable-by-name", "FLAGGED"):
        return 1
    flag_latency = time.monotonic() - break_at
    log(f"{now()} FLAG-LATENCY {flag_latency:.1f}s after the break (criterion: <= 30s) -> {'PASS' if flag_latency <= 30 else 'FAIL'}")

    # Heartbeats during flagged window: director must still be registered + heartbeating
    time.sleep(11)
    hb_during = heartbeat_count.get(BREAKABLE["directorId"], 0)
    state, _, _ = state_of(BREAKABLE["directorId"])
    log(f"{now()} HEARTBEAT-CHECK during flagged window: count {hb_before} -> {hb_during}, state={state} "
        f"-> {'PASS (alive + flagged, distinct from heartbeat-loss)' if hb_during > hb_before and state == 'unreachable-by-name' else 'FAIL'}")

    # Phase D: RESTORE
    breakable_proc = spawn_listener(BREAKABLE)
    restore_at = time.monotonic()
    log(f"{now()} RESTORE advertised endpoint of {BREAKABLE['directorId']} relaunched (no restarts of gateway/director)")
    if not watch_until(BREAKABLE["directorId"], "ok", "CLEARED"):
        return 1
    clear_latency = time.monotonic() - restore_at
    log(f"{now()} CLEAR-LATENCY {clear_latency:.1f}s after the restore (criterion: <= 30s) -> {'PASS' if clear_latency <= 30 else 'FAIL'}")

    # Phase E: impostor guard, live - the endpoint ANSWERS but as the wrong process
    breakable_proc.kill()
    time.sleep(0.5)
    impostor_proc = spawn_listener(BREAKABLE, "wrongid")
    log(f"{now()} IMPOSTOR listener answering at {BREAKABLE['directorId']}'s endpoint as 'qa-impostor-process'")
    if not watch_until(BREAKABLE["directorId"], "unreachable-by-name", "IMPOSTOR-FLAGGED"):
        return 1
    _, err, _ = state_of(BREAKABLE["directorId"])
    log(f"{now()} IMPOSTOR-ERROR-TEXT: {err}")

    # Phase F: back to the true listener, prove clear, then break-and-hold for Cockpit
    impostor_proc.kill()
    time.sleep(0.5)
    breakable_proc = spawn_listener(BREAKABLE)
    if not watch_until(BREAKABLE["directorId"], "ok", "CLEARED-AFTER-IMPOSTOR"):
        return 1
    breakable_proc.kill()
    log(f"{now()} BREAK-AND-HOLD for the Cockpit screenshot (kill this process to end)")
    watch_until(BREAKABLE["directorId"], "unreachable-by-name", "SCREENSHOT-READY")
    while True:
        time.sleep(30)


if __name__ == "__main__":
    sys.exit(main())
