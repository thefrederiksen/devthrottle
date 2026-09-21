import json, os, subprocess, sys, time
EXE = r"C:\Users\soren\AppData\Local\Temp\wbf-live\venv\Scripts\cc-devthrottle.exe"
ENV = dict(os.environ, CC_GATEWAY_URL="http://127.0.0.1:7898", CC_GATEWAY_SESSION_KEY="wbf-live-no-auth")
def dt(*a):
    return subprocess.run([EXE, *a], capture_output=True, text=True, env=ENV).stdout
secs = int(sys.argv[1])
end = time.time() + secs
while True:
    for t in json.loads(dt("trigger", "list", "--json")):
        print(time.strftime("%H:%M:%S"), t["name"], "|", t["status"], "|", t["statusText"], "| last:", t["lastOutcome"], t["lastCheckUtc"], "| paused:", t["paused"], "| session:", t["lastSessionId"])
    print("-", flush=True)
    if time.time() > end:
        break
    time.sleep(60)
