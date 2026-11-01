"""Wait in the foreground until a Gateway GET holds a word, or the time limit passes, and print the last answer.

Usage: python wait.py <path> <word> [seconds]
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(os.environ["LOCALAPPDATA"]) / "cc-director-restart-qa-rig"
TOKEN = (ROOT / "config" / "director" / "gateway-token.txt").read_text().strip()


def read(path):
    req = urllib.request.Request("http://127.0.0.1:7911" + path)
    req.add_header("Authorization", "Bearer " + TOKEN)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.read().decode()
    except urllib.error.URLError as err:
        return f"READ FAILED: {err}"


path, word = sys.argv[1], sys.argv[2]
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 300
end = time.time() + limit
text = ""
while time.time() < end:
    text = read(path)
    if word in text:
        print("FOUND", word)
        print(text[-1500:].encode("ascii", "replace").decode())
        sys.exit(0)
    time.sleep(5)
print("TIMED OUT waiting for", word)
print(text[-1500:].encode("ascii", "replace").decode())
sys.exit(1)
