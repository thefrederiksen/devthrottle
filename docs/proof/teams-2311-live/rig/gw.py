"""Small HTTP helper for the #2311 live proof. Every call is appended to transcript.txt with keys and
tokens shown only as their last four characters."""
import json
import os
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
GW = "http://127.0.0.1:7951"
STUB = "http://127.0.0.1:7952"
KEYS = os.path.join(HERE, "keys.json")  # local only, never committed


def load_keys():
    return json.load(open(KEYS)) if os.path.exists(KEYS) else {}


def save_key(name, value):
    k = load_keys(); k[name] = value; json.dump(k, open(KEYS, "w"), indent=2)


def mint(person):
    return json.load(urllib.request.urlopen(f"{STUB}/mint?name={person}"))["token"]


def redact(s):
    s = str(s)
    for name, value in load_keys().items():
        if value and value in s:
            s = s.replace(value, f"<{name} ...{value[-4:]}>")
    return s


def call(method, path, bearer=None, body=None, label=None, bearer_name=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(GW + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if bearer:
        req.add_header("Authorization", "Bearer " + bearer)
    try:
        resp = urllib.request.urlopen(req, timeout=60)
        status, text = resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        status, text = e.code, e.read().decode("utf-8", "replace")
    who = bearer_name or ("account token" if bearer and bearer.count(".") == 2 else "device key" if bearer else "none")
    line = f"{time.strftime('%H:%M:%S')} {method} {path}  [bearer: {who}]"
    if body is not None:
        line += "\n  body: " + redact(json.dumps(body))
    line += f"\n  -> {status} {redact(text)[:3000]}"
    with open(os.path.join(HERE, "transcript.txt"), "a", encoding="utf-8") as f:
        if label:
            f.write(f"\n## {label}\n")
        f.write(line + "\n")
    print(line)
    try:
        return status, json.loads(text)
    except ValueError:
        return status, text
