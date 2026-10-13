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
#
# ONE THING HERE IS NOT A COPY, AND IT IS SAID IN THE FILE IT WRITES. The document's full overview (section 2) was
# produced before the sign-in routes existed, so it lacks the nine fields the Gateway now sends on every account,
# machine and sign-in (section 7.6). Section 7.6 shows those fields only, for another picture. `complete_overview`
# below fills them in for section 2's picture with the sentences of section 7.6 itself, as the Gateway words them
# for a picture in which no Director is connected - which is how section 2's answer was produced (route handlers run
# over a store; no tunnel). The page's real answers, with every field, are in the proof run (qa-sign-ins/).
import io, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")
OUT = os.path.join(ROOT, "apps", "cockpit", "src", "licenses", "fixtures.ts")

text = io.open(DOC, encoding="utf-8").read()
blocks = re.findall(r"```json\n(.*?)\n```", text, flags=re.S)

REFUSAL = "{ code: string; error: string }"
SIGN_IN_FIELDS = "LicenseOverviewSignInFields"

# (constant, its type, the comment above it, the key that proves it is the block expected, a fragment to wrap)
WANTED = [
    ("OVERVIEW", "LicenseOverview", "GET /gateway/licenses: three accounts (in use, sign-in expires soon, at limit) on two machines. Its sign-in fields are filled in by copy_fixtures.py (see the note above).", "machines", False),
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
    ("SWITCH_NOW_REFUSED", REFUSAL, "POST /gateway/licenses/switch-now, refused 409.", "error", False),
    ("RENEWAL_OWNER_ONLY", REFUSAL, "The renewal date, asked by a caller that is not the owner's own signed-in phone or browser: 403.", "error", False),
    ("SWITCH_NOW_OWNER_ONLY", REFUSAL, "Switch now, asked by the same caller: 403, with its own sentence.", "error", False),
    # ---- section 7: sign-ins ----
    ("APPROVERS_EMPTY", "LicenseApprovers", "GET /gateway/licenses/approvers with no approver set for any account.", "approvers", False),
    ("APPROVERS", "LicenseApprovers", "GET /gateway/licenses/approvers with two set: one whose Director is connected, one on a machine that never reported.", "approvers", False),
    ("APPROVER_SET", "LicenseApprover", "PUT /gateway/licenses/approvers, the approver as stored.", "browserProfile", False),
    ("APPROVER_REFUSED", REFUSAL, "PUT /gateway/licenses/approvers, refused 400: the browser profile is not a name.", "error", False),
    ("APPROVER_REMOVED", "LicenseApproverRemoved", "DELETE /gateway/licenses/approvers, one removed.", "removed", False),
    ("SIGN_IN_STARTED", "LicenseSignInStarted", "POST /gateway/licenses/sign-ins/renew, answered 202: the sign-in's row of the log as it stands.", "entry", False),
    ("SIGN_IN_RUNNING", REFUSAL, "A sign-in refused 409: one for that machine and account has not ended.", "error", False),
    ("SIGN_IN_DIRECTOR_OFFLINE", REFUSAL, "A sign-in refused 409: the machine's Director is not connected.", "error", False),
    ("SIGN_IN_ALREADY_HELD", REFUSAL, "Add a spare refused 409: the machine holds the account.", "error", False),
    ("SIGN_IN_UNKNOWN_MACHINE", REFUSAL, "A sign-in refused 404: no machine with that identifier has reported.", "error", False),
    ("SIGN_IN_LOG", "LicenseLogPage", "GET /gateway/licenses/log with three sign-ins: one running, one that signed in, one that failed.", "entries", False),
    ("OVERVIEW_SIGN_IN_FIELDS", SIGN_IN_FIELDS, "The sign-in fields of GET /gateway/licenses, and only those, while a renewal was running on the-windows-machine and the Director on the-mac was not connected. It is another picture than OVERVIEW: there the-mac is on account C.", "machines", False),
]

if len(blocks) != len(WANTED):
    sys.exit(f"the document holds {len(blocks)} examples, {len(WANTED)} were expected - nothing was written")

values = {}
for block, (name, kind, comment, key, fragment) in zip(blocks, WANTED):
    value = json.loads("{" + block + "}" if fragment else block)
    if key not in value:
        sys.exit(f"an example is not the one expected: it has no '{key}' ({block[:60]}...) - nothing was written")
    if name is not None:
        values[name] = value


def complete_overview(overview, fields):
    """Fill in the sign-in fields the section 2 example was produced without. See the note at the top."""
    no_approver = [a for a in fields["accounts"] if a["approver"] is None]
    offline = [m for m in fields["machines"] if not m["canAddSpare"] and m["active"] is not None
               and m["addSpareText"] == m["active"]["renewNowText"] and m["machineName"] in m["addSpareText"]]
    if len(no_approver) != 1 or len(offline) != 1:
        sys.exit("section 7.6 no longer shows one account with no approver and one machine whose Director is not connected - nothing was written")
    for account in overview["accounts"]:
        if "approver" in account:
            sys.exit("the section 2 example now carries the sign-in fields itself: remove complete_overview - nothing was written")
        account["approver"] = None
        account["approverText"] = no_approver[0]["approverText"]
        account["approverTone"] = no_approver[0]["approverTone"]
    for machine in overview["machines"]:
        sentence = offline[0]["addSpareText"].replace(offline[0]["machineName"], machine["label"])
        machine["canAddSpare"] = False
        machine["addSpareText"] = sentence
        machine["signInsInProgress"] = []
        for holding in ([machine["active"]] if machine["active"] is not None else []) + machine["spares"]:
            holding["canRenewNow"] = False
            holding["renewNowText"] = sentence


complete_overview(values["OVERVIEW"], values["OVERVIEW_SIGN_IN_FIELDS"])

out = [
    "import type {",
    "  LicenseAccount,",
    "  LicenseApprover,",
    "  LicenseApproverRemoved,",
    "  LicenseApprovers,",
    "  LicenseForecast,",
    "  LicenseHolding,",
    "  LicenseLogPage,",
    "  LicenseMachine,",
    "  LicenseOverview,",
    "  LicenseRenewalResult,",
    "  LicenseSignInStarted,",
    "  LicenseSwitchNowResult,",
    '} from "@devthrottle/client-core/licenses/licensesClient";',
    "",
    "// THE GATEWAY'S OWN ANSWERS, NOT HAND-WRITTEN ONES. Each constant below is the real example of its route from",
    "// missions/license-switching/cockpit-api.md, which the Gateway produced by running its route handlers over a",
    "// store filled with the reports of its machines and accounts. A test that draws these draws what the Gateway",
    "// sends. This file is written by missions/license-switching/phase-3/copy_fixtures.py and is not edited by hand:",
    "// fixtures.test.ts fails when a constant here differs from its example in the document. Run the script again.",
    "//",
    "// ONE PART OF OVERVIEW IS NOT A COPY. The document's full overview was produced before the sign-in routes existed,",
    "// so it lacks the fields the Gateway now sends on every account (approver, approverText, approverTone), machine",
    "// (canAddSpare, addSpareText, signInsInProgress) and sign-in (canRenewNow, renewNowText). The script fills them in",
    "// with the document's own sentences for a picture in which no approver is set and no Director is connected: no",
    "// sign-in is offered anywhere, and each sentence says why. A test of a sign-in lays OVERVIEW_SIGN_IN_FIELDS, the",
    "// Gateway's real example of those fields, over it. fixtures.test.ts holds both halves.",
    "",
    "/** The sign-in fields of an overview, as section 7.6 of the document shows them: these fields and no others. */",
    "export interface LicenseOverviewSignInFields {",
    '  accounts: Array<Pick<LicenseAccount, "accountEmail" | "approver" | "approverText" | "approverTone">>;',
    "  machines: Array<",
    '    Pick<LicenseMachine, "machineName" | "canAddSpare" | "addSpareText" | "signInsInProgress"> & {',
    '      active: Pick<LicenseHolding, "accountEmail" | "canRenewNow" | "renewNowText"> | null;',
    '      spares: Array<Pick<LicenseHolding, "accountEmail" | "canRenewNow" | "renewNowText">>;',
    "    }",
    "  >;",
    "}",
    "",
]
for name, kind, comment, key, fragment in WANTED:
    if name is None:
        continue
    out.append(f"/** {comment} */")
    out.append(f"export const {name}: {kind} = {json.dumps(values[name], indent=2, ensure_ascii=True)};")
    out.append("")
out += [
    "/** A fresh copy of a fixture, so a test may change it without changing it for the next test. */",
    "export function copyOf<T>(value: T): T {",
    "  return JSON.parse(JSON.stringify(value)) as T;",
    "}",
    "",
]
io.open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print(f"wrote {os.path.relpath(OUT, ROOT)} from {len([w for w in WANTED if w[0]])} examples of {os.path.relpath(DOC, ROOT)}")
