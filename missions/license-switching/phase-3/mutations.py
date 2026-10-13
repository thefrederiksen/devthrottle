import io, subprocess, sys, re, json, os
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
V = "apps/cockpit/src/licenses/LicensesView.tsx"
P = "apps/cockpit/src/licenses/licenseParts.tsx"
C = "apps/cockpit/src/licenses/LicenseAccountCard.tsx"
K = "packages/client-core/src/licenses/licensesClient.ts"
D = "apps/cockpit/src/components/ConfirmDialog.tsx"
S = "apps/cockpit/src/licenses/LicenseSignIns.tsx"
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
 ("V1 the page puts a word of its own before the barred sentence (the review of 3944)", C, '          {account.barredText}', '          Barred: {account.barredText}', "cockpit"),
 ("V2 the page puts the machine's label before the Gateway's row sentence in the Switch now answer (the review of 3944)", V, '/> {row.summary}', '/> {machine.label}: {row.summary}', "cockpit"),
 ("V3 the confirmation dialog can be confirmed while it is shut (its own test)", D, 'disabled={busy || confirmDisabled}', 'disabled={busy}', "dialog"),
 ("V4 a fixture is edited by hand and no longer the document's example", "apps/cockpit/src/licenses/fixtures.ts", '"renewsText": "Renews 17 Oct"', '"renewsText": "Renews on 17 Oct"', "fixtures"),
 ("K1 the writes lose their content type", K, '"Content-Type": "application/json", ...authHeaders()', '...authHeaders()', "core"),
 ("K1b the writes lose their content type (seen from the page)", K, '"Content-Type": "application/json", ...authHeaders()', '...authHeaders()', "cockpit"),
 ("K2 clearing the renewal date leaves renewsOn out of the body", K, 'body: JSON.stringify({ accountEmail, renewsOn }),', 'body: JSON.stringify(renewsOn === null ? { accountEmail } : { accountEmail, renewsOn }),', "core"),
 ("K2b clearing the renewal date leaves renewsOn out of the body (seen from the page)", K, 'body: JSON.stringify({ accountEmail, renewsOn }),', 'body: JSON.stringify(renewsOn === null ? { accountEmail } : { accountEmail, renewsOn }),', "cockpit"),
 ("K3 the renewal date is sent by POST", K, '"/gateway/licenses/accounts/renewal", {\n    method: "PUT",', '"/gateway/licenses/accounts/renewal", {\n    method: "POST",', "core"),
 ("K4 the client accepts a stored answer that lost its sentence", K, 'if (typeof body?.renewsText !== "string") {', 'if (false) {', "core"),
 ("K5 the client gives up on Switch now before the Gateway's own wait ends", K, 'SWITCH_NOW_TIMEOUT_MS = 90000', 'SWITCH_NOW_TIMEOUT_MS = 10000', "core"),
 ("K6 a refused renewal date loses the Gateway's sentence", K, 'if (!res.ok) throw await GatewayError.from(res, "set the renewal date");', 'if (!res.ok) throw new GatewayError(res.status, "The renewal date could not be set.");', "core"),
 ("K7 a refused Switch now loses the Gateway's sentence (seen from the page)", K, 'if (!res.ok) throw await GatewayError.from(res, "switch the account");', 'if (!res.ok) throw new GatewayError(res.status, "The account could not be switched.");', "cockpit"),
 # ---- the sign-ins (the third pull request of phase 3): who approves an account, Add a spare, Renew now ----
 ("S1 Add a spare is offered on every machine", S, '{machine.canAddSpare && (', '{true && (', "cockpit"),
 ("S2 Renew now is offered on every sign-in", C, '{holding.canRenewNow && (', '{true && (', "cockpit"),
 ("S3 the Gateway's Add a spare sentence is not drawn on the machine's row", S, '                      {machine.addSpareText}', '', "cockpit"),
 ("S4 the Gateway's Renew now sentence is not drawn on the sign-in", C, '<span data-testid="lic-holding-renew-text">{holding.renewNowText}</span>', '<span data-testid="lic-holding-renew-text"></span>', "cockpit"),
 ("S5 Renew now sends without asking", V, 'onRenewNow={(machine, holding) => setRenewing({ machine, holding })}', 'onRenewNow={(machine, holding) => void renewLicenseSignIn(holding.accountEmail, machine.machineId)}', "cockpit"),
 ("S6 Add a spare can be confirmed with no account typed", V, 'confirmDisabled={spareAccount.trim() === ""}', 'confirmDisabled={false}', "cockpit"),
 ("S7 Add a spare is sent with no account (both guards gone)", V,
  ['confirmDisabled={spareAccount.trim() === ""}', 'if (addingSpare === null || accountEmail === "") return;'], ['confirmDisabled={false}', 'if (addingSpare === null) return;'], "cockpit"),
 ("S8 Add a spare sends the account with the space around it", V, 'const accountEmail = spareAccount.trim();', 'const accountEmail = spareAccount;', "cockpit"),
 ("S9 Add a spare is sent to the route of Renew now", V, 'setLastSignIn(await addLicenseSpare(accountEmail, addingSpare.machineId));', 'setLastSignIn(await renewLicenseSignIn(accountEmail, addingSpare.machineId));', "cockpit"),
 ("S10 Renew now names the machine by its name, not its identifier", V, 'renewLicenseSignIn(renewing.holding.accountEmail, renewing.machine.machineId)', 'renewLicenseSignIn(renewing.holding.accountEmail, renewing.machine.machineName)', "cockpit"),
 ("S11 the Gateway's sentence for a started sign-in is not drawn", S, '          {started.text}', '', "cockpit"),
 ("S12 the page puts the machine's label before the Gateway's row sentence of a sign-in", S, '/> {row.summary}', '/> {started.machine.label}: {row.summary}', "cockpit"),
 ("S13 how a sign-in ended is never read from the log", S, 'log.find((entry) => entry.entryId === started.entry.entryId) ?? started.entry', 'started.entry', "cockpit"),
 ("S14 the reason of a failed sign-in is not drawn", S, '{row.reasonText !== null && (', '{false && (', "cockpit"),
 ("S15 the approver sentence on a card loses the Gateway's tone", C, '<p className={`lic-card-line ${toneClass(account.approverTone)}`} data-testid="lic-account-approver">', '<p className="lic-card-line" data-testid="lic-account-approver">', "cockpit"),
 ("S16 the approver sentence on a card is not drawn", C, '        {account.approverText}', '', "cockpit"),
 ("S17 the page words 'no approver is set' itself", S, '          {approvers.emptyText}', '          No approver is set.', "cockpit"),
 ("S18 an approver's status word is shown in place of its label", S, 'text={approver.statusText}', 'text={approver.status}', "cockpit"),
 ("S19 an approving machine is called by its machine name", S, '<td className="lic-nowrap">{approver.label}</td>', '<td className="lic-nowrap">{approver.machineName}</td>', "cockpit"),
 ("S20 an approver's sentence is not drawn", S, '<div data-testid="lic-approver-text">{approver.text}</div>', '<div data-testid="lic-approver-text"></div>', "cockpit"),
 ("S21 the approvers are not read again after one is set", V, 'setApproverAnswer(stored.text);\n    void approvers.refresh();', 'setApproverAnswer(stored.text);', "cockpit"),
 ("S22 the approvers are not read again after one is removed", V, 'setApproverAnswer(removed.text);\n    void approvers.refresh();', 'setApproverAnswer(removed.text);', "cockpit"),
 ("S23 the page words the stored approver itself", V, 'setApproverAnswer(stored.text);', 'setApproverAnswer("Saved.");', "cockpit"),
 ("S24 the page words the removal itself", V, 'setApproverAnswer(removed.text);', 'setApproverAnswer("Removed.");', "cockpit"),
 ("S25 a refused approver is shown in the page's own words", S, 'setError(describeAndReport(LICENSES_SURFACE, "set who approves the sign-ins", err));', 'setError("The approver could not be saved.");', "cockpit"),
 ("S26 a refused approver closes the editor as if it were stored", S, '      setError(describeAndReport(LICENSES_SURFACE, "set who approves the sign-ins", err));\n      setBusy(false);', '      onClose();', "cockpit"),
 ("S27 an approver can be saved with a field left empty", S, 'disabled={busy || !complete}', 'disabled={busy}', "cockpit"),
 ("S28 the approver's account is sent with the space around it", S, 'accountEmail: accountEmail.trim(),', 'accountEmail,', "cockpit"),
 ("S29 changing an approver lets its account be changed", S, 'disabled={busy || editing !== null}', 'disabled={busy}', "cockpit"),
 ("S30 Remove sends without asking", V, 'onRemoveApprover={setRemovingApprover}', 'onRemoveApprover={(approver) => void removeLicenseApprover(approver.accountEmail)}', "cockpit"),
 ("S31 the account typed for one machine is carried to the next", V, '    setSpareAccount("");\n    setAddingSpare(machine);', '    setAddingSpare(machine);', "cockpit"),
 ("S32 the sentence of the answer before stays beside a refused removal", V, 'setApproverAnswer(null);\n    const removed', 'const removed', "cockpit"),
 ("S33 a failed read of the approvers shows nothing at all", S, '{approversError !== null && <ErrorBanner message={approversError} onRetry={onRetryApprovers} />}', '', "cockpit"),
 ("S34 a sign-in that has not ended is not drawn on its machine's row", S, '                      {signIn.text}', '', "cockpit"),
 ("S35 a machine is called by its machine name in the Sign-ins table", S, '<span className="lic-strong">{machine.label}</span>', '<span className="lic-strong">{machine.machineName}</span>', "cockpit"),
 ("S36 a machine is called by its machine name in the Add a spare confirmation", V, '`Add a spare on ${addingSpare.label}?`', '`Add a spare on ${addingSpare.machineName}?`', "cockpit"),
 ("S37 the Renew now confirmation says what it does in the page's own words", V, '{renewing.holding.renewNowText}</p>', 'This renews the sign-in.</p>', "cockpit"),
 ("S38 the removal confirmation says what is removed in the page's own words", V, '{removingApprover.text}</p>', 'This removes the approver.</p>', "cockpit"),
 ("S39 the Add a spare confirmation drops the Gateway's sentence for the machine", S, '<p className="lic-choice-text">{machine.addSpareText}</p>', '', "cockpit"),
 ("S40 a sign-in field of the overview fixture is edited by hand", "apps/cockpit/src/licenses/fixtures.ts", '"addSpareText": "The Director on the-windows-machine is not connected, so a sign-in cannot be started there."', '"addSpareText": "The Director on the-windows-machine is not connected."', "fixtures"),
 ("S41 a sign-in fixture is edited by hand and no longer the document's example", "apps/cockpit/src/licenses/fixtures.ts", '"text": "No machine is set to approve sign-ins for account-b@example.com any more."', '"text": "No machine approves account-b@example.com any more."', "fixtures"),
 ("K8 the approver is sent without the machine's user", K, 'body: JSON.stringify({ accountEmail, machineName, osUser, browserProfile }),', 'body: JSON.stringify({ accountEmail, machineName, browserProfile }),', "core"),
 ("K8b the approver is sent without the machine's user (seen from the page)", K, 'body: JSON.stringify({ accountEmail, machineName, osUser, browserProfile }),', 'body: JSON.stringify({ accountEmail, machineName, browserProfile }),', "cockpit"),
 ("K9 the removal names its account without escaping it", K, '?accountEmail=${encodeURIComponent(accountEmail)}', '?accountEmail=${accountEmail}', "core"),
 ("K9b the removal names its account without escaping it (seen from the page)", K, '?accountEmail=${encodeURIComponent(accountEmail)}', '?accountEmail=${accountEmail}', "cockpit"),
 ("K10 the removal is sent by POST", K, 'method: "DELETE",', 'method: "POST",', "core"),
 ("K10b the removal is sent by POST (seen from the page)", K, 'method: "DELETE",', 'method: "POST",', "cockpit"),
 ("K11 Add a spare goes to the route of Renew now", K, '"/gateway/licenses/sign-ins/add-spare", "add a spare"', '"/gateway/licenses/sign-ins/renew", "add a spare"', "core"),
 ("K11b Add a spare goes to the route of Renew now (seen from the page)", K, '"/gateway/licenses/sign-ins/add-spare", "add a spare"', '"/gateway/licenses/sign-ins/renew", "add a spare"', "cockpit"),
 ("K12 a sign-in is started without naming its machine", K, 'body: JSON.stringify({ accountEmail, machineId }),', 'body: JSON.stringify({ accountEmail }),', "core"),
 ("K12b a sign-in is started without naming its machine (seen from the page)", K, 'body: JSON.stringify({ accountEmail, machineId }),', 'body: JSON.stringify({ accountEmail }),', "cockpit"),
 ("K13 a refused sign-in loses the Gateway's sentence", K, 'if (!res.ok) throw await GatewayError.from(res, what);\n  const body = (await res.json()) as LicenseSignInStarted | null;', 'if (!res.ok) throw new GatewayError(res.status, "The sign-in could not be started.");\n  const body = (await res.json()) as LicenseSignInStarted | null;', "core"),
 ("K13b a refused sign-in loses the Gateway's sentence (seen from the page)", K, 'if (!res.ok) throw await GatewayError.from(res, what);\n  const body = (await res.json()) as LicenseSignInStarted | null;', 'if (!res.ok) throw new GatewayError(res.status, "The sign-in could not be started.");\n  const body = (await res.json()) as LicenseSignInStarted | null;', "cockpit"),
 ("K14 the client accepts a started sign-in that lost its sentence and its row", K, 'if (typeof body?.text !== "string" || typeof body?.entry?.entryId !== "string") {', 'if (false) {', "core"),
 ("K15 the client accepts a stored approver that lost its sentence", K, 'if (typeof body?.text !== "string") {\n    throw new GatewayError(res.status, "The Gateway answered the approver without', 'if (false) {\n    throw new GatewayError(res.status, "The Gateway answered the approver without', "core"),
 ("K16 the client accepts a removal that lost its sentence", K, 'if (typeof body?.text !== "string") {\n    throw new GatewayError(res.status, "The Gateway answered the removal', 'if (false) {\n    throw new GatewayError(res.status, "The Gateway answered the removal', "core"),
 ("K17 a refused approver loses the Gateway's sentence", K, 'if (!res.ok) throw await GatewayError.from(res, "set who approves the sign-ins");', 'if (!res.ok) throw new GatewayError(res.status, "The approver could not be set.");', "core"),
 ("K17b a refused approver loses the Gateway's sentence (seen from the page)", K, 'if (!res.ok) throw await GatewayError.from(res, "set who approves the sign-ins");', 'if (!res.ok) throw new GatewayError(res.status, "The approver could not be set.");', "cockpit"),
 ("K18 a refused removal loses the Gateway's sentence", K, 'if (!res.ok) throw await GatewayError.from(res, "remove who approves the sign-ins");', 'if (!res.ok) throw new GatewayError(res.status, "The approver could not be removed.");', "core"),
 ("K18b a refused removal loses the Gateway's sentence (seen from the page)", K, 'if (!res.ok) throw await GatewayError.from(res, "remove who approves the sign-ins");', 'if (!res.ok) throw new GatewayError(res.status, "The approver could not be removed.");', "cockpit"),
 # ---- the review of pull request 3954: its two wrong changes that survived, and the field that did not lock ----
 ("S42 whether the approvers list is empty is worked out by counting its rows", S, ') : approvers.isEmpty ? (', ') : approvers.approvers.length === 0 ? (', "cockpit"),
 ("S43 Dismiss on the answer to a started sign-in does nothing", V, 'onDismiss={() => setLastSignIn(null)}', 'onDismiss={() => undefined}', "cockpit"),
 ("S44 the Add a spare account field stays open while the call is out", S, '          disabled={busy}\n          onChange={(event) => onType(event.target.value)}', '          onChange={(event) => onType(event.target.value)}', "cockpit"),
]
def run(kind):
    cwd = os.path.join(ROOT, "packages", "client-core") if kind == "core" else os.path.join(ROOT, "apps", "cockpit")
    what = {"dialog": "src/components/ConfirmDialog.test.tsx", "fixtures": "src/licenses/fixtures.test.ts"}.get(kind, "src/licenses")
    r = subprocess.run(f"npx vitest run {what} --reporter=json", cwd=cwd, shell=True, capture_output=True)
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
