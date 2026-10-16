"""QA check: says what it can see, and finds no secret it did not declare.

It declares ONE entry (the rig's tenant-id entry). It writes into its state file - never on standard output, and never
a value - the names of every variable it was given that begins with CC_, the map CC_CHECK_SECRET_VARS gave it, whether
the declared entry's variable is set, and whether the variables of entries it did NOT declare are set. Then it asks
cc-secrets itself for an entry it did not declare, and records only whether that worked. Prints count 0.
"""
import json
import os
import shutil
import subprocess
import sys

# The definition file files/probe.json names the variables of entries this start does NOT declare, and one entry
# (a setting, never a secret) to ask cc-secrets for directly.
with open(os.path.join("files", "probe.json"), encoding="utf-8") as fh:
    PROBE = json.load(fh)
UNDECLARED = PROBE["undeclaredVariables"]
path = os.environ["CC_CHECK_STATE_FILE"]
given = json.loads(os.environ.get("CC_CHECK_SECRET_VARS") or "{}")
report = {
    "variablesBeginningCC": sorted(n for n in os.environ if n.upper().startswith("CC_")),
    "CC_CHECK_SECRET_VARS": given,
    "declaredVariableSet": {v: bool(os.environ.get(v)) for v in given.values()},
    "undeclaredVariableSet": {v: bool(os.environ.get(v)) for v in UNDECLARED},
}
probe = PROBE.get("selfCallEntry")
tool = shutil.which("cc-secrets")       # cc-secrets is a .cmd on Windows, so it is found the way a shell finds it
report["cc-secrets on this check's PATH"] = tool is not None
if probe and tool:
    variable = PROBE["selfCallVariable"]
    line = [tool, "run", probe, "--", sys.executable, "-c",
            f"import os; print('set' if os.environ.get('{variable}') else 'not set')"]
    try:
        r = subprocess.run(line, capture_output=True, text=True, timeout=60)
        report["cc-secrets run of an undeclared entry, from inside the check"] = {
            "exit": r.returncode, "said": r.stdout.strip()[:200], "stderr": r.stderr.strip()[:300]}
    except (OSError, subprocess.TimeoutExpired) as e:
        report["cc-secrets run of an undeclared entry, from inside the check"] = {"could not run": str(e)}
with open(path, "w", encoding="utf-8", newline="\n") as fh:
    fh.write(json.dumps(report, indent=1, sort_keys=True))
print('{"count": 0}')
