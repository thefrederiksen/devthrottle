"""A stand-in for `claude auth login`, for the step 5 proof only.

It does what Claude Code's login does to a config folder, and nothing else: prints an authorize URL naming the
account, reads the code back as one line, and writes a made-up sign-in (credentials file and account block) into the
folder CLAUDE_CONFIG_DIR names. The proof puts it first on PATH, so the real `cc-devthrottle license sign-in` runs end
to end - its real subprocess, its real key store - without a browser, claude.ai or any real login.
"""

import json
import os
import re
import sys
from pathlib import Path


def main() -> int:
    args = sys.argv[1:]
    if args[:2] != ["auth", "login"] or "--email" not in args:
        print(f"fake claude: unexpected arguments {args}", file=sys.stderr)
        return 2
    email = args[args.index("--email") + 1]
    folder = os.environ.get("CLAUDE_CONFIG_DIR")
    if not folder:
        print("fake claude: CLAUDE_CONFIG_DIR is not set; refusing to touch a real login", file=sys.stderr)
        return 2
    print("Opening the browser is off. Visit:", flush=True)
    print("https://claude.ai/oauth/authorize?code=true&client_id=proof&state=s1&login_hint="
          + email.replace("@", "%40"), flush=True)
    code = sys.stdin.readline().strip()
    if not re.fullmatch(r"[A-Za-z0-9_\-]{20,}#[A-Za-z0-9_\-]{20,}", code):
        print("fake claude: that is not a code", file=sys.stderr)
        return 1
    Path(folder).mkdir(parents=True, exist_ok=True)
    # Each login makes a new sign-in, as the real one does, so a renewal can be told from the sign-in it replaced.
    serial = os.urandom(6).hex()
    (Path(folder) / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
        "accessToken": f"fake-access-{serial}", "refreshToken": f"fake-refresh-{serial}",
        "expiresAt": 1999999999000, "refreshTokenExpiresAt": 1999999999000,
        "scopes": ["user:inference"], "subscriptionType": "max"}}), encoding="utf-8")
    (Path(folder) / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": email}}),
                                               encoding="utf-8")
    print("Login successful.", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
