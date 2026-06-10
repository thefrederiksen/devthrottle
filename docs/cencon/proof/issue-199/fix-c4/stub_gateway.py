"""Minimal stub Gateway for issue #199 criterion-4 proof.

Serves just enough of the Gateway REST surface for the Cockpit's poll loop:
  GET /sessions?envelope=true  -> two sessions, one with an EMPTY tailnetEndpoint
  GET /directors               -> one director
  GET /healthz                 -> ok
Everything else returns an empty 200 so the poll loop never errors.

The endpoint-less session ("blank-terminal failure mode") is the whole point:
selecting it must drive the Cockpit's OnSelect WARNING into the persisted log.
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 7899

REAL_ENDPOINT = "http://qa-test-box.example.ts.net:7887"

SESSIONS = [
    {
        "sessionId": "i199-real-0001",
        "directorId": "dir-A",
        "agent": "ClaudeCode",
        "type": "Implement",
        "repoPath": "C:/repo/alpha",
        "status": "Running",
        "activityState": "Idle",
        "assessedState": None,
        "createdAt": "2026-06-10T18:00:00Z",
        "name": "alpha (has endpoint)",
        "sortOrder": 0,
        "statusColor": "blue",
        "lastStatusReason": "working",
        "briefingState": "None",
        "backendType": "ConPty",
        "machineName": "BOX-A",
        "tailnetEndpoint": REAL_ENDPOINT,
        "viewUrl": REAL_ENDPOINT + "/sessions/i199-real-0001/view",
        "wingmanEnabled": False,
    },
    {
        "sessionId": "i199-nodir-0002",
        "directorId": "dir-B",
        "agent": "ClaudeCode",
        "type": "Implement",
        "repoPath": "C:/repo/beta",
        "status": "Running",
        "activityState": "WaitingForInput",
        "assessedState": None,
        "createdAt": "2026-06-10T18:05:00Z",
        "name": "beta (NO endpoint)",
        "sortOrder": 1,
        "statusColor": "red",
        "lastStatusReason": "waiting for input",
        "briefingState": "None",
        "backendType": "ConPty",
        "machineName": "BOX-B",
        "tailnetEndpoint": "",   # <-- blank-terminal failure mode
        "viewUrl": "",
        "wingmanEnabled": False,
    },
]

DIRECTORS = [
    {"directorId": "dir-A", "machineName": "BOX-A", "tailnetEndpoint": REAL_ENDPOINT,
     "version": "test", "user": "tester"},
    {"directorId": "dir-B", "machineName": "BOX-B", "tailnetEndpoint": "",
     "version": "test", "user": "tester"},
]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # quiet
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/sessions":
            self._json({"sessions": SESSIONS, "machineErrors": []})
        elif path == "/directors":
            self._json(DIRECTORS)
        elif path == "/healthz":
            self._json({"version": "stub", "serverTimeUtc": "2026-06-10T18:00:00Z"})
        elif path == "/interrupted":
            self._json([])
        else:
            self._json({})

    def do_POST(self):
        self._json({})

    def do_DELETE(self):
        self._json({})

    def do_PATCH(self):
        self._json({})


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"stub gateway on http://127.0.0.1:{PORT}", flush=True)
    srv.serve_forever()
