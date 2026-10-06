"""Local stand-in for the DevThrottle sign-in page and website API, for the #2311 live proof.

It mints ES256 account tokens with a throwaway key made for this run, for three made-up test
people, so the local hosted Gateway and the two test Directors never reach production sign-in.
Every other request (the website API: seat sync, invitation mail, refresh) is logged and answered
503, so nothing is ever forwarded anywhere.

  GET  /signin?redirect_uri=<loopback>   302 to the loopback with the current person's tokens in the fragment
  POST /control/identity  {"name": "alice"}  choose who the next browser sign-in is
  GET  /mint?name=alice                   a token for that person (for the proof's own HTTP calls)
  anything else                           logged, 503
"""
import base64
import http.server
import json
import os
import sys
import time
import urllib.parse
import uuid

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = int(sys.argv[1])
ISSUER = f"http://127.0.0.1:{PORT}/auth/v1"
KID = "teams-2311-liveproof"
LOG = os.path.join(HERE, "stub-requests.log")

PEOPLE = json.load(open(os.path.join(HERE, "people.json"), encoding="utf-8"))
key_path = os.path.join(HERE, "signing-key.pem")
if os.path.exists(key_path):
    KEY = serialization.load_pem_private_key(open(key_path, "rb").read(), password=None)
else:
    KEY = ec.generate_private_key(ec.SECP256R1())
    open(key_path, "wb").write(KEY.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                                 serialization.NoEncryption()))


def b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def jwks() -> str:
    nums = KEY.public_key().public_numbers()
    return json.dumps({"keys": [{"kty": "EC", "crv": "P-256", "alg": "ES256", "use": "sig", "kid": KID,
                                 "x": b64(nums.x.to_bytes(32, "big")), "y": b64(nums.y.to_bytes(32, "big"))}]})


def mint(name: str) -> str:
    person = PEOPLE[name]
    now = int(time.time())
    header = {"alg": "ES256", "typ": "JWT", "kid": KID}
    payload = {"sub": person["sub"], "email": person["email"], "aud": "authenticated", "iss": ISSUER,
               "role": "authenticated", "iat": now, "exp": now + 8 * 3600,
               "app_metadata": {"provider": "email"}}
    signing_input = b64(json.dumps(header).encode()) + "." + b64(json.dumps(payload).encode())
    der = KEY.sign(signing_input.encode("ascii"), ec.ECDSA(hashes.SHA256()))
    r, s = decode_dss_signature(der)
    return signing_input + "." + b64(r.to_bytes(32, "big") + s.to_bytes(32, "big"))


current = {"name": None}


def log(line: str) -> None:
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + line + "\n")


class Handler(http.server.BaseHTTPRequestHandler):
    def _send(self, status, body, ctype="application/json"):
        data = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(url.query)
        if url.path == "/signin":
            name = current["name"]
            if not name:
                log("GET /signin REFUSED - no person chosen")
                return self._send(409, json.dumps({"error": "choose a person first: POST /control/identity"}))
            target = query["redirect_uri"][0]
            parsed = urllib.parse.urlparse(target)
            if parsed.hostname not in ("127.0.0.1", "localhost"):
                log(f"GET /signin REFUSED - redirect is not loopback: {parsed.hostname}")
                return self._send(400, json.dumps({"error": "loopback redirects only"}))
            fragment = urllib.parse.urlencode({"access_token": mint(name), "refresh_token": "stub-" + uuid.uuid4().hex})
            log(f"GET /signin as {name} -> {parsed.scheme}://{parsed.netloc}{parsed.path}")
            self.send_response(302)
            self.send_header("Location", target + "#" + fragment)
            self.end_headers()
            return
        if url.path == "/mint":
            return self._send(200, json.dumps({"token": mint(query["name"][0])}))
        if url.path == "/jwks":
            return self._send(200, jwks())
        log(f"GET {self.path} -> 503 (website stub)")
        return self._send(503, json.dumps({"error": "local stub - nothing is forwarded"}))

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode("utf-8") if length else ""
        if self.path == "/control/identity":
            current["name"] = json.loads(body)["name"]
            log(f"POST /control/identity -> {current['name']}")
            return self._send(200, json.dumps({"next": current["name"]}))
        log(f"POST {self.path} -> 503 (website stub)")
        return self._send(503, json.dumps({"error": "local stub - nothing is forwarded"}))

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    open(os.path.join(HERE, "jwks.json"), "w", encoding="utf-8").write(jwks())
    if "--jwks-only" in sys.argv:
        sys.exit(0)
    log(f"stub listening on 127.0.0.1:{PORT}, issuer {ISSUER}")
    print(f"auth stub on 127.0.0.1:{PORT}", flush=True)
    http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
