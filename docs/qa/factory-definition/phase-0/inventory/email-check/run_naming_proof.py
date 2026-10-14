"""Phase 0 (g): prove how `cc-secrets run` names the variables a check receives.

The mission (5.5a) sets ONE rule: the entry name upper-cased, dashes and dots turned to underscores, with
`--env-name` passed for exactly one secret. This runs each form against entries this machine already holds
and prints the command, its exit code and its output. The probe prints variable NAMES only - never a value
or a length - and cc-secrets itself removes any secret from what it prints.

Entries used, and why (none is read for its value; only where it lands is shown):
  posthog-personal-api-key  secret, own variable equals the rule
  posthog-api-host          setting, own variable equals the rule
  cloudflare-account-id     setting, NO variable of its own
  laptop-2-rustdesk         secret, own variable LAPTOP2_RUSTDESK_PASSWORD - differs from the rule
  outreach-mail-client-secret  NOT held on this machine (the entry the email check will need)

Usage:  python run_naming_proof.py
"""

import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROBE = os.path.join(HERE, "env_names_probe.py")
SECRETS = shutil.which("cc-secrets")
if SECRETS is None:
    sys.exit("ERROR: cc-secrets is not on the PATH; it is installed with DevThrottle's tools.")

CASES = [
    ("1. One secret, --env-name by the rule; the entry's own variable equals the rule",
     ["posthog-personal-api-key", "--env-name", "POSTHOG_PERSONAL_API_KEY"],
     ["POSTHOG_PERSONAL_API_KEY", "CC_SECRET"]),
    ("2. One entry with NO variable of its own, --env-name by the rule, no --via",
     ["cloudflare-account-id", "--env-name", "CLOUDFLARE_ACCOUNT_ID"],
     ["CLOUDFLARE_ACCOUNT_ID", "CC_SECRET"]),
    ("3. The same with --via env",
     ["cloudflare-account-id", "--via", "env", "--env-name", "CLOUDFLARE_ACCOUNT_ID"],
     ["CLOUDFLARE_ACCOUNT_ID", "CC_SECRET"]),
    ("4. One secret whose own variable differs from the rule, --env-name by the rule",
     ["laptop-2-rustdesk", "--env-name", "LAPTOP_2_RUSTDESK"],
     ["LAPTOP_2_RUSTDESK", "LAPTOP2_RUSTDESK_PASSWORD", "CC_SECRET"]),
    ("5. Two entries (--with), both with own variables equal to the rule",
     ["posthog-personal-api-key", "--with", "posthog-api-host"],
     ["POSTHOG_PERSONAL_API_KEY", "POSTHOG_API_HOST", "CC_SECRET"]),
    ("6. Two entries, one whose own variable differs from the rule and one with none",
     ["laptop-2-rustdesk", "--with", "cloudflare-account-id"],
     ["LAPTOP_2_RUSTDESK", "LAPTOP2_RUSTDESK_PASSWORD", "CLOUDFLARE_ACCOUNT_ID", "CC_SECRET"]),
    ("7. A declared name this machine does not hold, alone",
     ["outreach-mail-client-secret", "--via", "env", "--env-name", "OUTREACH_MAIL_CLIENT_SECRET"],
     ["OUTREACH_MAIL_CLIENT_SECRET"]),
    ("8. A declared name this machine does not hold, beside one it does",
     ["posthog-api-host", "--with", "outreach-mail-client-secret"],
     ["POSTHOG_API_HOST", "OUTREACH_MAIL_CLIENT_SECRET"]),
]


def main():
    for title, args, names in CASES:
        cmd = [SECRETS, "run", *args, "--", sys.executable, PROBE, *names]
        shown = "cc-secrets run " + " ".join(args) + " -- python env_names_probe.py " + " ".join(names)
        p = subprocess.run(cmd, capture_output=True, text=True, stdin=subprocess.DEVNULL)
        print(f"=== {title}")
        print(f"$ {shown}")
        for line in (p.stdout + p.stderr).splitlines():
            if line.strip():  # cc-secrets relays the child's lines with an extra carriage return each
                print(f"    {line.rstrip()}")
        print(f"    (exit {p.returncode})")
        print()
    print("=== 9. An entry allowed for browser login only")
    print("    None exists on this machine: every one of its entries allows 'run' (cc-secrets list --json, 'uses').")
    print("    The refusal cannot be shown without an owner-made entry; entries are owner-only to add.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
