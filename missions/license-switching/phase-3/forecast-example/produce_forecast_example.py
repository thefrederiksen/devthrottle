# PRODUCES THE TWO FORECAST EXAMPLES OF SECTION 3 OF missions/license-switching/cockpit-api.md AGAIN (License Switching).
#
#   python missions\license-switching\phase-3\forecast-example\produce_forecast_example.py [--accept]
#
# It copies ForecastExample.cs into the Gateway's unit test project, runs its two methods (the forecast's own route
# handler over the real store on a throwaway SQLite file), takes the copy away again whatever happened, and writes the
# answers over the examples of section 3: the forecast for the picture of section 2, and, under the heading
# "### Example: the next seven days with a gap", the forecast with a gap and the overview of that same picture (which
# the layout check draws the cards from). Then run ..\copy_fixtures.py.
#
# ONE THING IN EACH ANSWER IS CHANGED, AND ONLY THAT. The store gives each machine a new random identifier on every
# run, so the identifiers are replaced by fixed ones, by machine name - for the first example the ones the other
# examples of the document name the two machines by. Every other character is the route handler's.
#
# It stops, and writes nothing, when an answer differs from the example in the document in anything but fields the
# document does not have yet. Pass --accept to write answers that differ, after reading the differences it prints.
import io, json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")
PROJECT = os.path.join(ROOT, "src", "CcDirector.Gateway.UnitTests")
COPY = os.path.join(PROJECT, "Licenses", "ForecastExample.cs")
OUTS = {
    "LICENSE_FORECAST_EXAMPLE_OUT": os.path.join(HERE, "answer-forecast.json"),
    "LICENSE_OUTLOOK_EXAMPLE_OUT": os.path.join(HERE, "answer-outlook.json"),
    "LICENSE_OUTLOOK_OVERVIEW_EXAMPLE_OUT": os.path.join(HERE, "answer-outlook-overview.json"),
}
IDENTIFIERS = {"the-mac": "94ebcb15a4bf40438dca738dd31189d9", "the-windows-machine": "f26a799d85a244129a27ba10b8cb3427"}
GAP_HEADING = "### Example: the next seven days with a gap"

if os.path.exists(COPY):
    sys.exit(f"{COPY} is already there: a run before this one did not finish. Look at it, then delete it.")
for out in OUTS.values():
    if os.path.exists(out):
        os.remove(out)
source = io.open(os.path.join(HERE, "ForecastExample.cs"), encoding="utf-8").read()
try:
    io.open(COPY, "w", encoding="utf-8", newline="\n").write(source)
    env = dict(os.environ, **OUTS)
    run = subprocess.run(["dotnet", "test", PROJECT, "--filter", "FullyQualifiedName~ForecastExample.Produce"], env=env)
finally:
    if os.path.exists(COPY):
        os.remove(COPY)
if run.returncode != 0 or not all(os.path.exists(out) for out in OUTS.values()):
    sys.exit("the examples were not produced - nothing was written")


def read_answer(path):
    produced = io.open(path, encoding="utf-8").read()
    os.remove(path)
    for machine in json.loads(produced)["machines"]:
        produced = produced.replace(machine["machineId"], IDENTIFIERS[machine["machineName"]])
    return json.loads(produced)


forecast = read_answer(OUTS["LICENSE_FORECAST_EXAMPLE_OUT"])
outlook = read_answer(OUTS["LICENSE_OUTLOOK_EXAMPLE_OUT"])
outlook_overview = read_answer(OUTS["LICENSE_OUTLOOK_OVERVIEW_EXAMPLE_OUT"])

text = io.open(DOC, encoding="utf-8", newline="").read()
newline = "\r\n" if "\r\n" in text else "\n"
plain = text.replace("\r\n", "\n")


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


blocks = list(re.finditer(r"```json\n(.*?)\n```", plain, flags=re.S))
first = blocks[2]
if "nextSwitchText" not in json.loads(first.group(1)):
    sys.exit("the third example of the document is not the forecast - nothing was written")
gap_at = plain.find(GAP_HEADING)
if gap_at < 0:
    sys.exit(f"the document has no heading '{GAP_HEADING}' - nothing was written")
second = next((b for b in blocks if b.start() > gap_at), None)
if second is None or "nextSwitchText" not in json.loads(second.group(1)):
    sys.exit("the example under the gap heading is not a forecast - nothing was written")
third = next((b for b in blocks if b.start() > second.start()), None)
if third is None or "accounts" not in json.loads(third.group(1)) or "nextSwitchText" in json.loads(third.group(1)):
    sys.exit("the second example under the gap heading is not an overview - nothing was written")

different = []
for name, block, answer in (("forecast", first, forecast), ("with a gap", second, outlook), ("its overview", third, outlook_overview)):
    for line in differences(json.loads(block.group(1)), answer):
        different.append(f"{name}: {line}")
for line in different:
    print("DIFFERS", line)
if different and "--accept" not in sys.argv:
    sys.exit(f"{len(different)} differences from the examples in the document - nothing was written")

# The later block first, so the earlier one's offsets still hold.
for block, answer in ((third, outlook_overview), (second, outlook), (first, forecast)):
    plain = plain[:block.start(1)] + json.dumps(answer, indent=2, ensure_ascii=True) + plain[block.end(1):]
io.open(DOC, "w", encoding="utf-8", newline="").write(plain.replace("\n", newline))
print(f"wrote the three examples of {os.path.relpath(DOC, ROOT)}: {len(different)} differences")
