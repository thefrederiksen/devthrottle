"""
Issue #848 proof harness: a tiny static + mock server for the mobile PWA.

It serves the built mobile app (mobile/dist) under /m and stubs the three Gateway endpoints the
"+ New session" flow uses, so the machine-picker subtitle can be proven on a phone viewport with
NO real Gateway, NO Director, and NO network - on a free loopback port. The stubbed /directors
covers every acceptance-criterion case:

  D1 SOREN_NORTH  - started 1h 05m ago (today)        -> "up 1h 5m  . started <time> . seen <time>"
  D2 SOREN        - started 2d 1h ago (older)         -> "up 2d 1h  . started <Mon d> . seen <time>"
  D3 SOREN_TICK   - started 2m 50s ago (today)        -> "up 2m" then "up 3m" (live tick, minutes)
  D4 SOREN_LEGACY - startedAt MISSING                 -> degrades to "last seen <time>"
  D5 SOREN_BADTM  - startedAt = "not-a-date"          -> degrades to "last seen <time>" (no NaN)

Run:  python mock-server.py <port> <abs-path-to-mobile/dist>
ASCII only. Loopback only.
"""
import json
import os
import sys
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8848
DIST = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else "dist"

START = datetime.now(timezone.utc)


def iso(delta):
    return (START - delta).strftime("%Y-%m-%dT%H:%M:%S.000Z")


def directors():
    return [
        {
            "directorId": "d1",
            "machineName": "SOREN_NORTH",
            "version": "0.9.27",
            "startedAt": iso(timedelta(hours=1, minutes=5)),
            "lastSeen": iso(timedelta(seconds=10)),
        },
        {
            "directorId": "d2",
            "machineName": "SOREN",
            "version": "0.9.27",
            "startedAt": iso(timedelta(days=2, hours=1)),
            "lastSeen": iso(timedelta(minutes=5)),
        },
        {
            "directorId": "d3",
            "machineName": "SOREN_TICK",
            "version": "0.9.27",
            "startedAt": iso(timedelta(minutes=2, seconds=50)),
            "lastSeen": iso(timedelta(minutes=20)),
        },
        {
            "directorId": "d4",
            "machineName": "SOREN_LEGACY",
            "version": "0.9.20",
            # startedAt intentionally absent (old Director that never reported it)
            "lastSeen": iso(timedelta(hours=1)),
        },
        {
            "directorId": "d5",
            "machineName": "SOREN_BADTM",
            "version": "0.9.20",
            "startedAt": "not-a-date",
            "lastSeen": iso(timedelta(hours=2)),
        },
    ]


REPOS = [
    {"name": "devthrottle", "path": "D:\\ReposFred\\devthrottle", "lastUsed": iso(timedelta(minutes=3))},
    {"name": "cc-director", "path": "D:\\ReposFred\\cc-director", "lastUsed": iso(timedelta(hours=4))},
]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def _json(self, obj, status=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _file(self, rel):
        path = os.path.join(DIST, rel)
        if not os.path.isfile(path):
            path = os.path.join(DIST, "index.html")  # SPA fallback for client routes like /m/new
        ctype = "text/html"
        if path.endswith(".js"):
            ctype = "application/javascript"
        elif path.endswith(".css"):
            ctype = "text/css"
        elif path.endswith(".png"):
            ctype = "image/png"
        elif path.endswith(".webmanifest") or path.endswith(".json"):
            ctype = "application/json"
        with open(path, "rb") as f:
            body = f.read()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/directors":
            return self._json(directors())
        if path.startswith("/directors/") and path.endswith("/repos"):
            return self._json(REPOS)
        if path == "/" :
            self.send_response(302)
            self.send_header("Location", "/m/new")
            self.end_headers()
            return
        if path.startswith("/m/"):
            return self._file(path[len("/m/"):] or "index.html")
        if path == "/m":
            self.send_response(302)
            self.send_header("Location", "/m/new")
            self.end_headers()
            return
        return self._json({"error": "not found"}, 404)

    def do_POST(self):
        path = self.path.split("?", 1)[0]
        if path.startswith("/directors/") and path.endswith("/sessions"):
            return self._json({"sessionId": "stub-session-1", "title": "stub"}, 201)
        return self._json({"error": "not found"}, 404)


if __name__ == "__main__":
    print("MOCK_SERVER listening on http://127.0.0.1:%d  (dist=%s)" % (PORT, DIST))
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
