"""A stand-in for `claude auth login --claudeai --email <email>`, for the Director proof only.

It does what the real command does on its two pipes and in its config folder, and nothing else: it prints an
authorize URL that names the account, reads one line, and - only when that line is the answer this proof's
approving side hands out - writes a made-up sign-in for the account into CLAUDE_CONFIG_DIR. It never contacts
anything. The "key" it writes is a random string that no provider would accept.

    FAKE_CLAUDE_ANSWER   the one line that completes the login (the proof's stand-in for the authorize answer)
    FAKE_CLAUDE_DAYS     how many days the sign-in it writes lasts (default 29)
"""

import json
import os
import secrets
import sys
import time
import urllib.parse
from pathlib import Path


def main() -> int:
    args = sys.argv[1:]
    if args[:3] != ["auth", "login", "--claudeai"] or "--email" not in args:
        print("fake claude: only 'auth login --claudeai --email <email>' is played", flush=True)
        return 2
    email = args[args.index("--email") + 1]
    folder = os.environ.get("CLAUDE_CONFIG_DIR")
    if not folder:
        print("fake claude: CLAUDE_CONFIG_DIR is not set; the proof never touches a real default login", flush=True)
        return 2

    print("Opening browser to sign in...", flush=True)
    print("https://claude.ai/oauth/authorize?code=true&client_id=fake&state=proof&login_hint="
          + urllib.parse.quote(email, safe=""), flush=True)
    print("Paste code here if prompted >", flush=True)

    line = sys.stdin.readline().strip()
    if not line:
        return 1  # the input ended with nothing typed: the login was abandoned
    if line != os.environ.get("FAKE_CLAUDE_ANSWER", ""):
        print("Login failed: the code was not accepted", flush=True)
        return 1

    now_ms = int(time.time() * 1000)
    days = float(os.environ.get("FAKE_CLAUDE_DAYS", "29"))
    serial = secrets.token_hex(8)
    target = Path(folder)
    target.mkdir(parents=True, exist_ok=True)
    account_file = target / ".claude.json"
    account = json.loads(account_file.read_text(encoding="utf-8")) if account_file.exists() else {}
    account["oauthAccount"] = {"emailAddress": email, "organizationName": "Proof organization"}
    account_file.write_text(json.dumps(account), encoding="utf-8")
    (target / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
        "accessToken": f"fake-access-{serial}", "refreshToken": f"fake-refresh-{serial}",
        "expiresAt": now_ms + 8 * 3600 * 1000, "refreshTokenExpiresAt": now_ms + int(days * 86400 * 1000),
        "scopes": ["user:inference"], "subscriptionType": "max"}}), encoding="utf-8")
    print("Login successful.", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
