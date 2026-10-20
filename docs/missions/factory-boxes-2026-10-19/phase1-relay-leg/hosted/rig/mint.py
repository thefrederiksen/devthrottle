"""Throwaway signing key and account token for the hosted relay run. Nothing here reaches production sign-in.
  python3 mint.py jwks   -> the public key set (for DEVTHROTTLE_JWT_PUBLIC_KEY_SET)
  python3 mint.py token  -> an account token for the one made-up test person
"""
import base64, json, os, sys, time
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

HERE = os.path.dirname(os.path.abspath(__file__))
ISSUER = "http://127.0.0.1:7997/auth/v1"
KID = "boxes-relay-hosted-proof"
key_path = os.path.join(HERE, "signing-key.pem")
if os.path.exists(key_path):
    KEY = serialization.load_pem_private_key(open(key_path, "rb").read(), password=None)
else:
    KEY = ec.generate_private_key(ec.SECP256R1())
    open(key_path, "wb").write(KEY.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))

def b64(d): return base64.urlsafe_b64encode(d).rstrip(b"=").decode()

if sys.argv[1] == "jwks":
    n = KEY.public_key().public_numbers()
    print(json.dumps({"keys": [{"kty": "EC", "crv": "P-256", "alg": "ES256", "use": "sig", "kid": KID,
                                "x": b64(n.x.to_bytes(32, "big")), "y": b64(n.y.to_bytes(32, "big"))}]}))
else:
    now = int(time.time())
    header = {"alg": "ES256", "typ": "JWT", "kid": KID}
    payload = {"sub": "0b0c5e1e-0000-4000-8000-000000004258", "email": "boxes-relay-proof@example.invalid",
               "aud": "authenticated", "iss": ISSUER, "role": "authenticated", "iat": now, "exp": now + 4 * 3600,
               "app_metadata": {"provider": "email"}}
    si = b64(json.dumps(header).encode()) + "." + b64(json.dumps(payload).encode())
    r, s = decode_dss_signature(KEY.sign(si.encode(), ec.ECDSA(hashes.SHA256())))
    print(si + "." + b64(r.to_bytes(32, "big") + s.to_bytes(32, "big")))
