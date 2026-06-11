# QA Agent independent harness for issue #325: minimal synthetic Director endpoint.
# Answers GET /healthz identifying as the given directorId; GET /sessions returns []
# (or HTTP 500 with "sessions500" to show the EXISTING amber fan-out UNREACHABLE state).
# Usage: python qa_listener.py <port> <directorId> [sessions500|wrongid]
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1])
DIRECTOR_ID = sys.argv[2]
MODE = sys.argv[3] if len(sys.argv) > 3 else ""


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
            reported_id = "qa-impostor-process" if MODE == "wrongid" else DIRECTOR_ID
            self._json({
                "status": "ok",
                "directors": 1,
                "sessions": 0,
                "version": "qa-proof-325",
                "serverTime": "2026-06-11T00:00:00Z",
                "directorId": reported_id,
                "machineName": "QA-PROOF-325",
            })
        elif self.path.startswith("/sessions"):
            if MODE == "sessions500":
                self._json({"error": "synthetic control-plane failure (QA proof)"}, status=500)
            else:
                self._json([])
        else:
            self._json({"error": "not found"}, status=404)

    def log_message(self, *args):
        pass


print(f"[qa_listener] port={PORT} directorId={DIRECTOR_ID} mode={MODE}", flush=True)
ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
