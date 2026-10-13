import io, subprocess, sys, re, json, os
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
V = "apps/cockpit/src/licenses/LicensesView.tsx"
P = "apps/cockpit/src/licenses/licenseParts.tsx"
C = "apps/cockpit/src/licenses/LicenseAccountCard.tsx"
K = "packages/client-core/src/licenses/licensesClient.ts"
D = "apps/cockpit/src/components/ConfirmDialog.tsx"
# One wrong change is (name, file, old, new, suite). `old` and `new` may each be a list, for a change that takes two
# edits of the same file to make (the two guards that keep Switch now from being sent without a target).
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
 ("R1 every pill is drawn in the good colour, whatever tone the Gateway sent", P, '<span className={`lic-pill ${toneClass(tone)}`}', '<span className={`lic-pill lic-tone-good`}', "cockpit"),
 ("R2 every schedule row shows the first machine's forecast", V, 'forecast?.machines.find((m) => m.machineId === machine.machineId)', 'forecast?.machines[0]', "cockpit"),
 ("R3 every card shows the first account's forecast sentence", V, 'forecast={forecast.data?.accounts.find((a) => a.accountEmail === account.accountEmail)}', 'forecast={forecast.data?.accounts[0]}', "cockpit"),
 ("R4a a read error is not drawn on the card", C, '{account.readErrorText !== null && (', '{false && (', "cockpit"),
 ("R4b a read error is not drawn on the machine's line", C, '{holding.readErrorText !== null && (', '{false && (', "cockpit"),
 ("R5a a machine is called by its machine name on the cards", C, '{machine.label}</span>', '{machine.machineName}</span>', "cockpit"),
 ("R5b a machine is called by its machine name in the schedule", V, '<span className="lic-strong">{machine.label}</span>', '<span className="lic-strong">{machine.machineName}</span>', "cockpit"),
 ("R5c a machine is called by its machine name in the log", V, '<td className="lic-nowrap">{entry.label}</td>', '<td className="lic-nowrap">{entry.machineName}</td>', "cockpit"),
 ("R6 a failed read of the switch log shows nothing at all", V, '{log.error !== null && <ErrorBanner message={log.error} onRetry={() => void log.refresh()} />}', '', "cockpit"),
 ("R7a the page never shows Loading for the accounts", V, 'overview.error === null && <LoadingState />', 'null', "cockpit"),
 ("R7b the page never shows Loading for the switch log", V, 'log.error === null && <LoadingState />', 'null', "cockpit"),
 ("R8 how old the numbers are is not drawn", C, '{account.readAgeText}', '', "cockpit"),
 ("R9 the page colours a read error red by itself", C, '<span className="lic-holding-error" data-testid="lic-holding-read-error">', '<span className="lic-holding-error lic-tone-bad" data-testid="lic-holding-read-error">', "cockpit"),
 ("13 a failed overview read is drawn as an empty page", V, 'overview.error === null && <LoadingState />', '<EmptyState message="No machine has reported its Claude accounts yet. They appear here once a Director with license switching turned on has connected." />', "cockpit"),
 ("14 the client accepts an answer that lost its list", K, 'if (!Array.isArray(body?.[list])) {', 'if (false) {', "core"),
 ("15 the client drops the log cursor", K, 'const query = cursor ? `?cursor=${encodeURIComponent(cursor)}` : "";', 'const query = "";', "core"),
 ("16 the client sends Switch now without its target", K, 'body: JSON.stringify({ accountEmail, targetAccountEmail }),', 'body: JSON.stringify({ accountEmail }),', "core"),
 # ---- the two writes (the second pull request of phase 3) ----
 ("W1 Switch now can be confirmed where the Gateway chose no target and none is picked", V, 'confirmDisabled={target === null}', 'confirmDisabled={false}', "cockpit"),
 ("W2 the shared dialog ignores that the confirmation cannot be given yet", D, 'disabled={busy || confirmDisabled}', 'disabled={busy}', "cockpit"),
 ("W3 Switch now is sent with no target where the Gateway chose none (both guards gone)", V,
  ['confirmDisabled={target === null}', 'if (confirming === null || target === null) return;'], ['confirmDisabled={false}', 'if (confirming === null) return;'], "cockpit"),
 ("W4 the saved renewal date is not shown from the Gateway's answer", V, '? { ...account, renewsOn: stored.renewsOn, renewsText: stored.renewsText }', '? account', "cockpit"),
 ("W5 the page words the saved renewal date itself", V, 'renewsText: stored.renewsText }', 'renewsText: `Renews ${renewsOn}` }', "cockpit"),
 ("W6 a saved renewal date is drawn on every account", V, 'account.accountEmail === accountEmail\n                ? { ...account, renewsOn', 'true\n                ? { ...account, renewsOn', "cockpit"),
 ("W7 Clear date sends an empty date in place of null", C, 'void store(null)', 'void store("")', "cockpit"),
 ("W8 a renewal date can be saved before one is given", C, 'disabled={busy || value === ""}', 'disabled={busy}', "cockpit"),
 ("W9 a refused renewal date is shown in the page's own words", C, 'setError(describeAndReport(LICENSES_SURFACE, "set the renewal date", err));', 'setError("The renewal date could not be saved.");', "cockpit"),
 ("W10 a refused renewal date closes the editor as if it were stored", C, '      setError(describeAndReport(LICENSES_SURFACE, "set the renewal date", err));\n      setBusy(false);', '      onClose();', "cockpit"),
 ("W11 the barred sentence is not drawn", C, '{account.barredText !== null && (', '{false && (', "cockpit"),
 ("W12 the time an account is barred until is not drawn", C, '(<LocalTime iso={account.barredUntilUtc} />)', '', "cockpit"),
 ("W13 the page colours the barred sentence by itself", C, '<p className="lic-card-line" data-testid="lic-account-barred">', '<p className="lic-card-line lic-tone-warning" data-testid="lic-account-barred">', "cockpit"),
 ("W14 the Gateway's sentence for the outcome of Switch now is not drawn", V, '          {result.text}', '', "cockpit"),
 ("W15 the page puts a name of its own before a left-out machine's sentence", V, '              {machine.why}', '              {machine.machineName}: {machine.why}', "cockpit"),
 ("W16 how a switch ended is never read from the switch log", V, 'log.find((entry) => entry.entryId === machine.entryId) ?? machine', 'machine', "cockpit"),
 ("W17 a failed renewal is drawn on every sign-in of the card", C, '{holding.renewalFailedText !== null && (', '{true && (', "cockpit"),
 ("K1 the writes lose their content type", K, '"Content-Type": "application/json", ...authHeaders()', '...authHeaders()', "core"),
 ("K1b the writes lose their content type (seen from the page)", K, '"Content-Type": "application/json", ...authHeaders()', '...authHeaders()', "cockpit"),
 ("K2 clearing the renewal date leaves renewsOn out of the body", K, 'body: JSON.stringify({ accountEmail, renewsOn }),', 'body: JSON.stringify(renewsOn === null ? { accountEmail } : { accountEmail, renewsOn }),', "core"),
 ("K2b clearing the renewal date leaves renewsOn out of the body (seen from the page)", K, 'body: JSON.stringify({ accountEmail, renewsOn }),', 'body: JSON.stringify(renewsOn === null ? { accountEmail } : { accountEmail, renewsOn }),', "cockpit"),
 ("K3 the renewal date is sent by POST", K, 'method: "PUT",', 'method: "POST",', "core"),
 ("K4 the client accepts a stored answer that lost its sentence", K, 'if (typeof body?.renewsText !== "string") {', 'if (false) {', "core"),
 ("K5 the client gives up on Switch now before the Gateway's own wait ends", K, 'SWITCH_NOW_TIMEOUT_MS = 90000', 'SWITCH_NOW_TIMEOUT_MS = 10000', "core"),
 ("K6 a refused renewal date loses the Gateway's sentence", K, 'if (!res.ok) throw await GatewayError.from(res, "set the renewal date");', 'if (!res.ok) throw new GatewayError(res.status, "The renewal date could not be set.");', "core"),
 ("K7 a refused Switch now loses the Gateway's sentence (seen from the page)", K, 'if (!res.ok) throw await GatewayError.from(res, "switch the account");', 'if (!res.ok) throw new GatewayError(res.status, "The account could not be switched.");', "cockpit"),
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
    # The file may be checked out with either line ending; a change is written in the file's own.
    file_nl = "\r\n" if "\r\n" in src else "\n"
    own = lambda t: t.replace("\r\n", "\n").replace("\n", file_nl)
    changed = src
    for one_old, one_new in zip([old] if isinstance(old, str) else old, [new] if isinstance(new, str) else new):
        if changed.count(own(one_old)) != 1:
            print("NOT APPLIED", name, changed.count(own(one_old))); sys.exit(1)
        changed = changed.replace(own(one_old), own(one_new))
    try:
        io.open(full, "w", encoding="utf-8", newline="").write(changed)
        total, failed = run(kind)
    finally:
        io.open(full, "w", encoding="utf-8", newline="").write(src)
    rows.append((name, total, failed))
    print(name, "| ran", total, "| red", len(failed)); [print("    -", f) for f in failed]
    sys.stdout.flush()
print("RESTORED; baseline:")
for kind in ("cockpit", "core"):
    total, failed = run(kind); print(kind, total, "red", len(failed))
survived = [name for name, _, failed in rows if not failed]
print(len(rows), "wrong changes;", len(survived), "left every test green")
[print("    SURVIVED:", name) for name in survived]
