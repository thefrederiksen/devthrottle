"""Issue #622 proof: a minimal webhook listener that captures every POST it receives.

Records each request body to captured_webhooks.jsonl (one JSON line per POST) and prints it, so
the proof can show the per-job outbound webhook received the run-complete payload (AC5).

Usage: python webhook_listener.py <port> <out_file>
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

OUT_FILE = sys.argv[2] if len(sys.argv) > 2 else "captured_webhooks.jsonl"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        # Handle both Content-Length and chunked Transfer-Encoding (HttpClient.PostAsJsonAsync
        # streams chunked, so there is no Content-Length header to read).
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            body = self._read_chunked()
        else:
            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length).decode("utf-8") if length else ""
        record = {"path": self.path, "body": body}
        with open(OUT_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(record) + "\n")
        print(f"[webhook] received POST {self.path}: {body}", flush=True)
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'{"ok":true}')

    def _read_chunked(self):
        chunks = []
        while True:
            size_line = self.rfile.readline().strip()
            if not size_line:
                continue
            size = int(size_line, 16)
            if size == 0:
                self.rfile.readline()  # consume trailing CRLF
                break
            chunks.append(self.rfile.read(size))
            self.rfile.readline()  # consume CRLF after each chunk
        return b"".join(chunks).decode("utf-8")

    def log_message(self, *args):
        pass  # quiet the default access log


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8623
    print(f"[webhook] listening on http://127.0.0.1:{port}, writing {OUT_FILE}", flush=True)
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
