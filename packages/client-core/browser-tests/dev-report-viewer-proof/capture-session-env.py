"""Write this session's Gateway environment to a file, so the viewer proof can run cc-dev-reports as the session.

Run by the rig's fixture session (rig.ps1 session). The file lives in the rig root, which rig.ps1 reset deletes;
the key in it is a per-session key issued by the rig Gateway, not a credential of the owner's.
"""

import json
import os
import sys

NAMES = ("CC_GATEWAY_URL", "CC_GATEWAY_SESSION_KEY", "CC_SESSION_ID")


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: capture-session-env.py <output file>")
        return 2
    missing = [n for n in NAMES if not os.environ.get(n)]
    if missing:
        print("ERROR: this environment has no " + ", ".join(missing) + " - run it inside a DevThrottle session.")
        return 1
    with open(sys.argv[1], "w", encoding="ascii") as f:
        json.dump({n: os.environ[n] for n in NAMES}, f)
    print("OK: wrote " + sys.argv[1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
