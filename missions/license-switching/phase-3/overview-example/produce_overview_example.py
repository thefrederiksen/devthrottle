# PRODUCES THE EXAMPLE OF SECTION 2 OF missions/license-switching/cockpit-api.md AGAIN (License Switching, phase 3).
#
#   python missions\license-switching\phase-3\overview-example\produce_overview_example.py
#
# It copies OverviewExample.cs into the Gateway's unit test project, runs that one method (the overview's own route
# handler over the real store on a throwaway SQLite file, the clock at 2026-10-11 03:12:00 UTC), takes the copy away
# again whatever happened, and writes the answer over the example in the document. Then run copy_fixtures.py.
#
# ONE THING IN THE ANSWER IS CHANGED, AND ONLY THAT. The store gives each machine a new random identifier on every
# run. The forecast and the log examples of the same document name the two machines by the identifiers of the run
# they were produced in, so the two new identifiers are replaced by those two, by machine name. Every other
# character is the route handler's.
#
# It stops, and writes nothing, when the answer differs from the example in the document in anything but fields the
# document does not have yet - so a picture that drifted is not written over the document unseen. Pass --accept to
# write an answer that differs, after reading the difference it prints.
import io, json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")
PROJECT = os.path.join(ROOT, "src", "CcDirector.Gateway.UnitTests")
COPY = os.path.join(PROJECT, "Licenses", "OverviewExample.cs")
OUT = os.path.join(HERE, "answer.json")
# The identifiers the document's other examples name the two machines by.
IDENTIFIERS = {"the-mac": "94ebcb15a4bf40438dca738dd31189d9", "the-windows-machine": "f26a799d85a244129a27ba10b8cb3427"}

if os.path.exists(COPY):
    sys.exit(f"{COPY} is already there: a run before this one did not finish. Look at it, then delete it.")
if os.path.exists(OUT):
    os.remove(OUT)
source = io.open(os.path.join(HERE, "OverviewExample.cs"), encoding="utf-8").read()
try:
    io.open(COPY, "w", encoding="utf-8", newline="\n").write(source)
    env = dict(os.environ, LICENSE_OVERVIEW_EXAMPLE_OUT=OUT)
    run = subprocess.run(["dotnet", "test", PROJECT, "--filter", "FullyQualifiedName~OverviewExample.Produce"], env=env)
finally:
    if os.path.exists(COPY):
        os.remove(COPY)
if run.returncode != 0 or not os.path.exists(OUT):
    sys.exit("the example was not produced - nothing was written")

produced = io.open(OUT, encoding="utf-8").read()
os.remove(OUT)
answer = json.loads(produced)
for machine in answer["machines"]:
    produced = produced.replace(machine["machineId"], IDENTIFIERS[machine["machineName"]])
answer = json.loads(produced)

text = io.open(DOC, encoding="utf-8", newline="").read()
newline = "\r\n" if "\r\n" in text else "\n"
plain = text.replace("\r\n", "\n")
found = list(re.finditer(r"```json\n(.*?)\n```", plain, flags=re.S))
first = found[0]
old = json.loads(first.group(1))
if "machines" not in old or "accounts" not in old:
    sys.exit("the first example of the document is not the overview - nothing was written")


def differences(was, now, where=""):
    """Every place the new answer differs from the old one, other than a field the old one does not have."""
    if isinstance(was, dict) and isinstance(now, dict):
        out = [f"{where}.{key}: gone" for key in was if key not in now]
        for key in now:
            if key in was:
                out += differences(was[key], now[key], f"{where}.{key}")
        return out
    if isinstance(was, list) and isinstance(now, list) and len(was) == len(now):
        return [d for index, (a, b) in enumerate(zip(was, now)) for d in differences(a, b, f"{where}[{index}]")]
    return [] if was == now else [f"{where}: {json.dumps(was)} -> {json.dumps(now)}"]


def added(was, now, where=""):
    if isinstance(was, dict) and isinstance(now, dict):
        return [f"{where}.{key}" for key in now if key not in was] + [a for key in now if key in was for a in added(was[key], now[key], f"{where}.{key}")]
    if isinstance(was, list) and isinstance(now, list):
        return [a for index, (x, y) in enumerate(zip(was, now)) for a in added(x, y, f"{where}[{index}]")]
    return []


different = differences(old, answer)
for line in different:
    print("DIFFERS", line)
if different and "--accept" not in sys.argv:
    sys.exit(f"{len(different)} differences from the example in the document - nothing was written")
new_fields = added(old, answer)
print(f"{len(new_fields)} fields the document did not have:", ", ".join(sorted({f.split('.')[-1] for f in new_fields})))

body = json.dumps(answer, indent=2, ensure_ascii=True)
plain = plain[:first.start(1)] + body + plain[first.end(1):]
io.open(DOC, "w", encoding="utf-8", newline="").write(plain.replace("\n", newline))
print(f"wrote the overview example of {os.path.relpath(DOC, ROOT)}: {len(different)} differences, {len(new_fields)} fields added")
