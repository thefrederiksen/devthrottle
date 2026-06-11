# Proof harness for issue #325: a minimal synthetic Director endpoint.
# Answers GET /healthz identifying as the given directorId (the thing the Gateway's
# advertised-endpoint probe verifies) and GET /sessions with an empty list so the
# Gateway's fleet fan-out sees a healthy control plane. With "sessions500" the
# /sessions route answers HTTP 500 (healthz fine) to demonstrate the EXISTING amber
# UNREACHABLE (fan-out failure) state, distinct from the new unreachable-by-name.
#
# Usage: python listener.py <port> <directorId> [sessions500]
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1])
DIRECTOR_ID = sys.argv[2]
SESSIONS_BROKEN = len(sys.argv) > 3 and sys.argv[3] == "sessions500"


class Handler(BaseHTTPRequestHandler):
    def _json(self, payload, status=200):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/healthz"):
            self._json({
                "status": "ok",
                "directors": 1,
                "sessions": 0,
                "version": "proof-325",
                "serverTime": "2026-06-11T00:00:00Z",
                "directorId": DIRECTOR_ID,
                "machineName": "PROOF-325",
            })
        elif self.path.startswith("/sessions"):
            if SESSIONS_BROKEN:
                self._json({"error": "synthetic control-plane failure (proof)"}, status=500)
            else:
                self._json([])
        else:
            self._json({"error": "not found"}, status=404)

    def log_message(self, *args):
        pass  # keep the proof log quiet


print(f"[listener] port={PORT} directorId={DIRECTOR_ID} sessionsBroken={SESSIONS_BROKEN}", flush=True)
ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
