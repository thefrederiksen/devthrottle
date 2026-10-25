"""Alerts Phase 4 QA orchestrator. ONE foreground command: starts the local QA Gateway host for a scenario, runs a
steps file against it through browser-harness, and stops the host when the steps end (or fail).

    python qa.py <scenario> <steps.py>

A steps file is plain Python run here with: host (send(cmd) -> reply line), bh(code) -> stdout, KEY, BASE, OUT.

To run it: build host/ (dotnet build), build both web apps (npm run build for @devthrottle/cockpit and
@devthrottle/mobile) and copy apps/cockpit/dist to host/bin/Debug/net10.0/wwwroot/c and apps/mobile/dist to
.../wwwroot/mobile, start a Director browser profile with no account (cc-devthrottle browser start agent-browser), then
for example:  QA_APP=phone python -u qa.py mixed steps_mixed.py"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "host", "bin", "Debug", "net10.0", "AlertsQaHost.exe")
OUT = os.path.dirname(HERE)  # the qa folder this tools folder sits in
PORT = 7993
BASE = f"http://127.0.0.1:{PORT}"
ENV = dict(os.environ, BU_NAME="agent-browser", BU_CDP_URL="http://127.0.0.1:9313", BH_TAB_MARKER="0")

PRELUDE = r'''
import json, time
def phone():
    cdp("Emulation.setDeviceMetricsOverride", width=390, height=844, deviceScaleFactor=2, mobile=True)
def phone360():
    cdp("Emulation.setDeviceMetricsOverride", width=360, height=780, deviceScaleFactor=2, mobile=True)
def desktop():
    cdp("Emulation.setDeviceMetricsOverride", width=1440, height=900, deviceScaleFactor=1, mobile=False)
def enrol(url, key):
    # The first load may still carry the previous run's key and bounce through a 401 to sign-in; let it settle, then
    # replace the key and load again.
    cdp("Network.clearBrowserCookies")
    goto_url(url); wait_for_load(); time.sleep(2.5)
    js("localStorage.clear(); sessionStorage.clear(); localStorage.setItem('cc.deviceKey', %s); 'ok'" % json.dumps(key))
    for _ in range(3):
        goto_url(url); wait_for_load(); time.sleep(1.5)
        if "/signin" not in js("location.pathname"):
            break
    if "/signin" in js("location.pathname"):
        raise RuntimeError("landed on sign-in after enrolling the QA key")
def wait_js(expr, timeout=15):
    end = time.time() + timeout
    while time.time() < end:
        v = js("!!(" + expr + ")")
        if v: return v
        time.sleep(0.25)
    raise RuntimeError("timed out waiting for: " + expr)
def center(sel):
    r = js("(()=>{const e=document.querySelector(%s); if(!e) return null; const b=e.getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" % json.dumps(sel))
    if not r: raise RuntimeError("no element " + sel)
    return r
def click(sel):
    x, y = center(sel); click_at_xy(x, y); time.sleep(0.4)
def click_text(sel, text):
    r = js("(()=>{const e=[...document.querySelectorAll(%s)].find(n=>n.textContent.trim()===%s); if(!e) return null; const b=e.getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" % (json.dumps(sel), json.dumps(text)))
    if not r: raise RuntimeError("no %s with text %s" % (sel, text))
    click_at_xy(r[0], r[1]); time.sleep(0.4)
def shot(name):
    p = r"%OUT%\\" + name + ".png"
    capture_screenshot(p)
    print("SHOT", name)
def overflow():
    return js("(()=>{const w=document.documentElement.clientWidth; const bad=[...document.querySelectorAll('.alerts-tray, .alerts-tray *')].filter(e=>{const b=e.getBoundingClientRect(); return b.width>0 && (b.right>w+0.5 || b.left< -0.5 || e.scrollWidth>e.clientWidth+1 && getComputedStyle(e).overflowX!=='auto' && getComputedStyle(e).overflowX!=='scroll' && getComputedStyle(e).overflowX!=='hidden');}).map(e=>e.className+':'+Math.round(e.getBoundingClientRect().right)); return JSON.stringify({width:w, bad});})()")
'''.replace("%OUT%", OUT)


class Host:
    def __init__(self, scenario):
        self.p = subprocess.Popen([EXE, scenario, str(PORT)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", cwd=os.path.dirname(EXE), bufsize=1)
        self.key = None
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("QA host exited before READY")
            if line.startswith("READY"):
                print("[qa] " + line.strip().split(" key=")[0])
                self.key = line.split("key=")[1].split()[0]
                break

    def send(self, cmd):
        self.p.stdin.write(cmd + "\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line:
                return "EXITED"
            if line.split(" ")[0] in ("REOPENED", "DOWN", "UNKNOWN", "BYE"):
                print("[qa] host: " + line.strip())
                return line.strip()

    def stop(self):
        if self.p.poll() is None:
            try:
                self.p.stdin.write("quit\n")
                self.p.stdin.flush()
                self.p.wait(timeout=30)
            except Exception as e:  # noqa: BLE001 - report and fall through to a plain wait
                print("[qa] host stop: " + str(e))
                self.p.wait(timeout=30)


def bh(code):
    r = subprocess.run(["browser-harness"], input=PRELUDE + code, text=True, encoding="utf-8", errors="replace", capture_output=True, env=ENV, timeout=300)
    out = (r.stdout or "") + (r.stderr or "")
    print(out.strip().encode("ascii", "replace").decode("ascii"))
    if r.returncode != 0:
        raise RuntimeError("browser-harness failed")
    return out


def main():
    scenario, steps = sys.argv[1], sys.argv[2]
    host = Host(scenario)
    try:
        code = open(steps, encoding="utf8").read()
        exec(code, {"host": host, "bh": bh, "KEY": host.key, "BASE": BASE, "OUT": OUT, "time": time})
    finally:
        host.stop()
        print("[qa] host stopped")


if __name__ == "__main__":
    main()
