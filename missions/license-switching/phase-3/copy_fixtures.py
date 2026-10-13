# COPIES THE GATEWAY'S OWN EXAMPLE ANSWERS INTO THE PAGE'S TEST FIXTURES (License Switching, phase 3).
#
# missions/license-switching/cockpit-api.md holds a real answer of every Licenses route, produced by the Gateway and
# not typed by hand. This script reads them out of that file, in order, and writes
# apps/cockpit/src/licenses/fixtures.ts, so the page's tests draw what the Gateway sends. Run it again whenever the
# document's examples move:
#
#   python missions\license-switching\phase-3\copy_fixtures.py
#
# It stops, and writes nothing, when the document does not hold exactly the examples it expects in the order it
# expects them.
import io, json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")
OUT = os.path.join(ROOT, "apps", "cockpit", "src", "licenses", "fixtures.ts")

text = io.open(DOC, encoding="utf-8").read()
blocks = re.findall(r"```json\n(.*?)\n```", text, flags=re.S)

# (constant, its type, the comment above it, the key that proves it is the block expected, a fragment to wrap)
WANTED = [
    ("OVERVIEW", "LicenseOverview", "GET /gateway/licenses: three accounts (in use, sign-in expires soon, at limit) on two machines.", "machines", False),
    ("BARRED_FIELDS", 'Pick<LicenseAccount, "barredUntilUtc" | "barredText">', "The last two fields of an account that is barred from the automatic choice of a target.", "barredText", True),
    ("FORECAST", "LicenseForecast", "GET /gateway/licenses/forecast for the same picture.", "nextSwitchText", False),
    ("LOG_PAGE", "LicenseLogPage", "GET /gateway/licenses/log?limit=4: a spare added, a switch that FAILED, a spare removed, a switch that worked; older rows exist.", "entries", False),
    ("EMPTY_OVERVIEW", "LicenseOverview", "GET /gateway/licenses for an account whose machines have never reported.", "emptyText", False),
    (None, None, None, "code", False),  # the refusal of a log limit
    (None, None, None, "renewsOn", False),  # the body of the renewal write
    ("RENEWAL_SET", "LicenseRenewalResult", "PUT /gateway/licenses/accounts/renewal, a date stored.", "renewsText", False),
    ("RENEWAL_CLEARED", "LicenseRenewalResult", "PUT /gateway/licenses/accounts/renewal, the date cleared.", "renewsText", False),
    (None, None, None, "code", False),  # the refusal of a date
    ("RENEWAL_FAILED_FIELDS", 'Pick<LicenseHolding, "renewalFailedAtUtc" | "renewalFailedText" | "renewalFailedTone">', "The three fields of a holding whose last sign-in renewal failed.", "renewalFailedText", True),
    (None, None, None, "accountEmail", False),  # the body of Switch now
    ("SWITCH_NOW_ACCEPTED", "LicenseSwitchNowResult", "POST /gateway/licenses/switch-now, accepted: one machine, whose switch waits for its Director.", "switches", False),
    ("SWITCH_NOW_REFUSED", "{ code: string; error: string }", "POST /gateway/licenses/switch-now, refused 409.", "error", False),
    ("OWNER_ONLY", "{ code: string; error: string }", "Either write, asked by a caller that is not the owner's own signed-in phone or browser: 403.", "error", False),
]

if len(blocks) != len(WANTED):
    sys.exit(f"the document holds {len(blocks)} examples, {len(WANTED)} were expected - nothing was written")

commit = subprocess.run(["git", "log", "-1", "--format=%h", "--", DOC], cwd=ROOT, capture_output=True, text=True).stdout.strip()
out = [
    "import type {",
    "  LicenseAccount,",
    "  LicenseForecast,",
    "  LicenseHolding,",
    "  LicenseLogPage,",
    "  LicenseOverview,",
    "  LicenseRenewalResult,",
    "  LicenseSwitchNowResult,",
    '} from "@devthrottle/client-core/licenses/licensesClient";',
    "",
    "// THE GATEWAY'S OWN ANSWERS, NOT HAND-WRITTEN ONES. Each constant below is the real example of its route from",
    f"// missions/license-switching/cockpit-api.md (as that file stood at commit {commit}), which the Gateway",
    "// produced by running its route handlers over a store filled with the reports of its machines and accounts.",
    "// A test that draws these draws what the Gateway sends. This file is written by",
    "// missions/license-switching/phase-3/copy_fixtures.py; when the shapes move, run it again and do not edit here.",
    "",
]
for block, (name, kind, comment, key, fragment) in zip(blocks, WANTED):
    value = json.loads("{" + block + "}" if fragment else block)
    if key not in value:
        sys.exit(f"an example is not the one expected: it has no '{key}' ({block[:60]}...) - nothing was written")
    if name is None:
        continue
    out.append(f"/** {comment} */")
    out.append(f"export const {name}: {kind} = {json.dumps(value, indent=2, ensure_ascii=True)};")
    out.append("")
out += [
    "/** A fresh copy of a fixture, so a test may change it without changing it for the next test. */",
    "export function copyOf<T>(value: T): T {",
    "  return JSON.parse(JSON.stringify(value)) as T;",
    "}",
    "",
]
io.open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print(f"wrote {os.path.relpath(OUT, ROOT)} from {len([w for w in WANTED if w[0]])} examples of {os.path.relpath(DOC, ROOT)} at {commit}")
