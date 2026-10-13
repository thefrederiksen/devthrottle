import io, subprocess, sys, re, json, os
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
V = "apps/cockpit/src/licenses/LicensesView.tsx"
P = "apps/cockpit/src/licenses/licenseParts.tsx"
C = "apps/cockpit/src/licenses/LicenseAccountCard.tsx"
K = "packages/client-core/src/licenses/licensesClient.ts"
M = [
 ("1 the page works a window's colour out of its number", P, 'lic-bar-${window.level}`', 'lic-bar-${(window.utilization as number) >= 97 ? "at-limit" : "normal"}`', "cockpit"),
 ("2 a window that was not read is drawn as a meter", P, 'const known = window.utilization !== null && (window.level === "normal" || window.level === "at-limit");', 'const known = true;', "cockpit"),
 ("3 the status word is shown in place of its label", C, 'text={account.statusText}', 'text={account.status}', "cockpit"),
 ("4 a sign-in's expiry loses the Gateway's tone", C, '${toneClass(holding.expiryTone)}', '', "cockpit"),
 ("5 Switch now is offered on every account", C, '{account.canSwitchNow && (', '{true && (', "cockpit"),
 ("6 Switch now sends without asking", V, '    setTarget(account.switchNowTargetAccountEmail);\r\n    setConfirming(account);', '    void switchLicenseNow(account.accountEmail, account.switchNowTargetAccountEmail);', "cockpit"),
 ("7 Switch now drops the picked target", V, 'switchLicenseNow(confirming.accountEmail, target)', 'switchLicenseNow(confirming.accountEmail, null)', "cockpit"),
 ("8 the empty answer is drawn as tables", V, ') : data.isEmpty ? (', ') : false ? (', "cockpit"),
 ("9 a log row loses its reason", V, '{entry.reasonText !== null && <div className="lic-dim">{entry.reasonText}</div>}', '', "cockpit"),
 ("10 older rows are read without the cursor", V, 'await getLicenseLog(olderCursor)', 'await getLicenseLog(null)', "cockpit"),
 ("11 a failed renewal is drawn without the Gateway's tone", C, '${toneClass(holding.renewalFailedTone ?? "neutral")}', '', "cockpit"),
 ("12 the log merge repeats a row already on screen", V, 'return [...first, ...rest.filter((entry) => !seen.has(entry.entryId))];', 'return [...first, ...rest];', "cockpit"),
 ("17 a new newest page throws away the older rows already read", V, 'if (current !== null && current.walked) {', 'if (false) {', "cockpit"),
 ("18 the renewal date is not drawn", C, '{account.planText} - {account.renewsText}', '{account.planText}', "cockpit"),
 ("13 a failed overview read is drawn as an empty page", V, 'overview.error === null && <LoadingState />', '<EmptyState message="No machine has reported its Claude accounts yet. They appear here once a Director with license switching turned on has connected." />', "cockpit"),
 ("14 the client accepts an answer that lost its list", K, 'if (!Array.isArray(body?.[list])) {', 'if (false) {', "core"),
 ("15 the client drops the log cursor", K, 'const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";', 'const query = "";', "core"),
 ("16 the client sends Switch now without its target", K, 'body: JSON.stringify({ accountEmail, targetAccountEmail }),', 'body: JSON.stringify({ accountEmail }),', "core"),
]
def run(kind):
    cwd = os.path.join(ROOT, "apps", "cockpit") if kind == "cockpit" else os.path.join(ROOT, "packages", "client-core")
    r = subprocess.run("npx vitest run src/licenses --reporter=json", cwd=cwd, shell=True, capture_output=True)
    out = r.stdout.decode("utf-8", "replace")
    j = json.loads(out[out.index("{"):])
    failed = [a["title"] for f in j["testResults"] for a in f["assertionResults"] if a["status"] != "passed"]
    return j["numTotalTests"], failed
rows = []
for name, path, old, new, kind in M:
    if kind == "skip": continue
    full = os.path.join(ROOT, path)
    src = io.open(full, encoding="utf-8", newline="").read()
    if src.count(old) != 1:
        old2 = old.replace("\r\n", "\n")
        if src.count(old2) == 1: old = old2
        else:
            print("NOT APPLIED", name, src.count(old)); sys.exit(1)
    try:
        io.open(full, "w", encoding="utf-8", newline="").write(src.replace(old, new))
        total, failed = run(kind)
    finally:
        io.open(full, "w", encoding="utf-8", newline="").write(src)
    rows.append((name, total, failed))
    print(name, "| ran", total, "| red", len(failed)); [print("    -", f) for f in failed]
    sys.stdout.flush()
print("RESTORED; baseline:")
for kind in ("cockpit", "core"):
    total, failed = run(kind); print(kind, total, "red", len(failed))
