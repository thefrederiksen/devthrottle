# THE QA RUN OF THE SIGN-INS OF THE LICENSES PAGE AGAINST A REAL LOCAL GATEWAY (License Switching, phase 3).
#
# Who approves an account (set, change, remove), Add a spare and Renew now, each sent by the real page in a real
# browser to the real routes of a Gateway built from this tree, with a screenshot of each write and of each refusal.
#
# One command, everything in the foreground, everything stopped at the end:
#   1. starts qa-gateway (the product's own GatewayHost, hosted mode, throwaway storage, seeded license data, and a
#      stand-in Director on the Gateway's real tunnel - see the top of qa-gateway/Program.cs for what it stands in for);
#   2. starts the Cockpit's own development server, forwarding Gateway routes to that Gateway;
#   3. opens the page in a browser enrolled with a seeded key - NO request is intercepted or stubbed - and does each
#      write the way a person does, recording what the browser sent and what the Gateway answered;
#   4. stops the development server and the Gateway.
#
# No Director of the product is started, no Claude command is run, no browser profile is opened for an approval and
# no Claude sign-in of this machine is read or touched: the Gateway's storage is a throwaway folder, the usage reports
# in it are made up by qa-gateway, and the accounts are names at example.com.
#
# Run from anywhere:  python missions\license-switching\phase-3\qa_sign_ins_run.py     (after `npm ci` at the repository root)
import json, os, re, subprocess, sys, time, urllib.request, urllib.error
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT = os.path.join(HERE, "qa-sign-ins")
os.makedirs(OUT, exist_ok=True)
VITE_PORT = 5199
A, B, E = "account-a@example.com", "account-b@example.com", "account-e@example.com"
WINDOWS, MAC = "the-windows-machine", "the-mac"
APPROVERS, ADD_SPARE, RENEW = "/gateway/licenses/approvers", "/gateway/licenses/sign-ins/add-spare", "/gateway/licenses/sign-ins/renew"
# How long the page may take to show something it learns from its next read: it reads every 30 seconds.
NEXT_READ = 45000
# A name in an answer that would mean something of a sign-in itself had reached the browser.
FORBIDDEN_NAME = re.compile(r"token|secret|credential|password|authorize|sealed|verifier|key$", re.I)

LOG = open(os.path.join(OUT, "qa-sign-ins-run-output.txt"), "w", encoding="utf-8", newline="\n")
failures = []
checks = 0


def say(text=""):
    print(text)
    sys.stdout.flush()
    LOG.write(text + "\n")
    LOG.flush()


def check(what, ok):
    global checks
    checks += 1
    say(("  [OK]   " if ok else "  [FAIL] ") + what)
    if not ok:
        failures.append(what)


def get(port, path, key):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", headers={"Authorization": "Bearer " + key, "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as err:
        return err.code, json.loads(err.read().decode("utf-8") or "{}")


def save(name, value):
    with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
        json.dump(value, f, indent=2)
        f.write("\n")


def names_in(value):
    """Every property name in an answer, at any depth."""
    if isinstance(value, dict):
        for name, inner in value.items():
            yield name
            yield from names_in(inner)
    elif isinstance(value, list):
        for inner in value:
            yield from names_in(inner)


gateway = subprocess.Popen(
    ["dotnet", "run", "--project", os.path.join(HERE, "qa-gateway"), "-c", "Debug"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
vite = None
try:
    line = ""
    while not line.startswith("{"):
        line = gateway.stdout.readline()
        if line == "":
            sys.exit("the QA Gateway ended before it printed its port")
    seeded = json.loads(line)
    port = seeded["port"]
    OWNER, WORKSTATION = seeded["signInsKey"], seeded["signInsWorkstationKey"]
    say(f"Gateway on loopback port {port}, storage under a throwaway folder")

    def command(word):
        gateway.stdin.write(word + "\n")
        gateway.stdin.flush()
        answer = gateway.stdout.readline().strip()
        say(f"  qa-gateway {word} -> {answer}")
        if not answer.startswith("ok"):
            sys.exit("the QA Gateway did not do: " + word)

    env = dict(os.environ, COCKPIT_PROXY_TARGET=f"http://127.0.0.1:{port}")
    vite = subprocess.Popen(f"npx vite --port {VITE_PORT} --strictPort", cwd=os.path.join(ROOT, "apps", "cockpit"), shell=True,
                            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(90):
        try:
            urllib.request.urlopen(f"http://localhost:{VITE_PORT}/", timeout=1)
            break
        except Exception:
            time.sleep(1)
    else:
        sys.exit("the Cockpit development server did not start")

    with sync_playwright() as p:
        browser = p.chromium.launch()
        seen_names = set()
        answers_seen = [0]

        def open_as(key):
            """The Licenses page in a browser enrolled with `key`. Returns the page and every sign-in write it sends."""
            context = browser.new_context(viewport={"width": 1440, "height": 1100})
            context.add_init_script(f"localStorage.setItem('cc.deviceKey', {json.dumps(key)})")
            page = context.new_page()
            sent = []

            def on_request(request):
                path = request.url.split(str(VITE_PORT))[1]
                if path.split("?")[0] in (APPROVERS, ADD_SPARE, RENEW) and request.method != "GET":
                    sent.append({"method": request.method, "path": path, "contentType": request.headers.get("content-type"), "body": request.post_data})

            def on_response(response):
                # Every answer of a Licenses route that reaches the browser: its property names are kept.
                if "/gateway/licenses" in response.url:
                    try:
                        seen_names.update(names_in(response.json()))
                        answers_seen[0] += 1
                    except Exception:
                        pass

            page.on("request", on_request)
            page.on("response", on_response)
            page.goto(f"http://localhost:{VITE_PORT}/licenses")
            page.wait_for_selector("[data-testid=lic-account]")
            page.wait_for_selector("[data-testid=lic-sign-in-machine]")
            page.wait_for_selector("[data-testid=lic-approvers], [data-testid=lic-approvers-empty]")
            return page, sent

        def card(page, email):
            return page.get_by_role("region", name=email, exact=True)

        def holding(page, email, machine):
            return card(page, email).locator("[data-testid=lic-holding]").filter(has=page.locator(".lic-holding-machine", has_text=re.compile("^" + re.escape(machine) + "$")))

        def machine_row(page, label):
            return page.locator("[data-testid=lic-sign-in-machine]").filter(has=page.locator(".lic-strong", has_text=re.compile("^" + re.escape(label) + "$")))

        def approver_row(page, email):
            return page.locator("[data-testid=lic-approver]").filter(has=page.locator("td", has_text=re.compile("^" + re.escape(email) + "$")))

        def shot(page, name, around=None):
            if around is not None:
                around.scroll_into_view_if_needed()
            page.wait_for_timeout(400)
            page.screenshot(path=os.path.join(OUT, name + ".png"))
            say(f"  screenshot qa-sign-ins/{name}.png")

        def answered(page, method, path, act):
            """Do `act` and return the Gateway's answer to the write it causes: (status, body)."""
            with page.expect_response(lambda r: r.request.method == method and r.url.split("?")[0].endswith(path), timeout=60000) as info:
                act()
            response = info.value
            return response.status, json.loads(response.text())

        def said(sent_one):
            say(f"  the browser sent: {sent_one['method']} {sent_one['path']}  Content-Type: {sent_one['contentType']}  body: {sent_one['body']}")

        def until(page, what, timeout=NEXT_READ):
            """Wait until `what()` is true, as a person waiting for the page's next read would. Returns whether it became true."""
            deadline = time.time() + timeout / 1000
            while time.time() < deadline:
                if what():
                    return True
                page.wait_for_timeout(250)
            return what()

        def dialog(page):
            page.wait_for_selector("[role=alertdialog]")
            return page.get_by_role("alertdialog")

        def refusal_in(page):
            page.wait_for_selector(".ui-confirm-error")
            return page.locator(".ui-confirm-error").text_content()

        def editor(page):
            return page.locator("[data-testid=lic-approver-editor]")

        def fill_approver(page, account, machine, user, profile):
            if account is not None:
                editor(page).get_by_label("Claude account").fill(account)
            editor(page).get_by_label("Approving machine").fill(machine)
            editor(page).get_by_label("Operating system user on that machine").fill(user)
            editor(page).get_by_label("Browser profile on that machine").fill(profile)

        def save_approver(page):
            return answered(page, "PUT", APPROVERS, lambda: editor(page).get_by_role("button", name="Save").click())

        def log_entry(entry_id):
            _, log = get(port, "/gateway/licenses/log", OWNER)
            return [e for e in log["entries"] if e["entryId"] == entry_id][0]

        def ended(entry_id):
            """The row of the log once the Gateway has settled it."""
            for _ in range(80):
                entry = log_entry(entry_id)
                if not entry["isOpen"]:
                    return entry
                time.sleep(0.25)
            sys.exit("the Gateway did not settle the sign-in")

        # ---- 1. What the Gateway says of sign-ins, and what the page shows of it ----
        say()
        say("1. What the Gateway says of sign-ins before anything is done, and what the page shows of it")
        status, overview = get(port, "/gateway/licenses", OWNER)
        save("answer-overview.json", overview)
        _, approvers = get(port, APPROVERS, OWNER)
        save("answer-approvers-none-set.json", approvers)
        machines = {m["machineName"]: m for m in overview["machines"]}
        say(f"  GET /gateway/licenses -> {status}")
        for m in overview["machines"]:
            say(f"    {m['label']}: canAddSpare={m['canAddSpare']}, addSpareText=\"{m['addSpareText']}\"")
            for h in ([m["active"]] if m["active"] else []) + m["spares"]:
                say(f"      {h['accountEmail']}: canRenewNow={h['canRenewNow']}, renewNowText=\"{h['renewNowText']}\"")
        say(f"  GET {APPROVERS} -> isEmpty={approvers['isEmpty']}, emptyText=\"{approvers['emptyText']}\"")
        check("the Gateway offers Add a spare on the machine whose Director is connected and not on the other", machines[WINDOWS]["canAddSpare"] is True and machines[MAC]["canAddSpare"] is False)
        page, sent = open_as(OWNER)
        check("the page shows the Gateway's sentence for no approver, in place of the list", page.locator("[data-testid=lic-approvers-empty]").text_content() == approvers["emptyText"] and page.locator("[data-testid=lic-approver]").count() == 0)
        for account in overview["accounts"]:
            shown = card(page, account["accountEmail"]).locator("[data-testid=lic-account-approver]")
            check(f"the card of {account['accountEmail']} shows the Gateway's approverText whole, in its tone", shown.text_content() == account["approverText"] and ("lic-tone-" + account["approverTone"]) in shown.get_attribute("class"))
        for m in overview["machines"]:
            row = machine_row(page, m["label"])
            check(f"the row of {m['label']} shows the Gateway's addSpareText whole", row.locator("[data-testid=lic-add-spare-text]").text_content() == m["addSpareText"])
            check(f"Add a spare is {'offered' if m['canAddSpare'] else 'not offered'} on {m['label']}, as the Gateway says", row.get_by_role("button", name="Add a spare").count() == (1 if m["canAddSpare"] else 0))
            for h in ([m["active"]] if m["active"] else []) + m["spares"]:
                line = holding(page, h["accountEmail"], m["label"])
                check(f"the sign-in for {h['accountEmail']} on {m['label']} shows the Gateway's renewNowText whole, and Renew now is {'offered' if h['canRenewNow'] else 'not offered'}",
                      line.locator("[data-testid=lic-holding-renew-text]").text_content() == h["renewNowText"] and line.get_by_role("button", name="Renew now").count() == (1 if h["canRenewNow"] else 0))
        check("nothing was sent to reach this", len(sent) == 0)
        shot(page, "sign-ins-before", page.locator("[data-testid=lic-approvers-empty]"))
        shot(page, "cards-renew-now-offered-and-not", card(page, A))

        # ---- 2. Who approves an account: set, refused, changed, removed ----
        say()
        say("2. Who approves an account, by the owner's signed-in browser: set, refused, changed, removed")
        page.get_by_role("button", name="Set an approver").click()
        check("nothing can be saved before the four fields are given", editor(page).get_by_role("button", name="Save").is_disabled())
        fill_approver(page, B, MAC, "owner", "work")
        status, body = save_approver(page)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("PUT /gateway/licenses/approvers answered 200", status == 200)
        check("the body was sent as application/json", sent[-1]["contentType"] == "application/json")
        check("the body is the four fields and nothing else", json.loads(sent[-1]["body"]) == {"accountEmail": B, "machineName": MAC, "osUser": "owner", "browserProfile": "work"})
        page.wait_for_selector("[data-testid=lic-approver-answer]")
        check("the page shows the Gateway's sentence for what it stored, whole", page.locator("[data-testid=lic-approver-answer]").text_content() == body["text"])
        check("the page read the approvers again and lists the one stored", until(page, lambda: approver_row(page, B).count() == 1, 10000))
        _, approvers = get(port, APPROVERS, OWNER)
        stored = [a for a in approvers["approvers"] if a["accountEmail"] == B][0]
        say(f"  GET {APPROVERS} now says of {B}: status={stored['status']}, statusText=\"{stored['statusText']}\", text=\"{stored['text']}\"")
        row = approver_row(page, B)
        check("its row is the Gateway's label, browser profile, status word in its tone, and sentence",
              [c.text_content() for c in row.locator("td").all()[:3]] == [B, stored["label"], stored["browserProfile"]]
              and row.locator("[data-testid=lic-approver-status]").text_content() == stored["statusText"]
              and ("lic-tone-" + stored["statusTone"]) in row.locator("[data-testid=lic-approver-status]").get_attribute("class")
              and row.locator("[data-testid=lic-approver-text]").text_content() == stored["text"])
        _, overview = get(port, "/gateway/licenses", OWNER)
        account = [a for a in overview["accounts"] if a["accountEmail"] == B][0]
        check("the card of account B now shows the Gateway's new approverText, in its tone",
              until(page, lambda: card(page, B).locator("[data-testid=lic-account-approver]").text_content() == account["approverText"], 10000)
              and ("lic-tone-" + account["approverTone"]) in card(page, B).locator("[data-testid=lic-account-approver]").get_attribute("class")
              and account["approver"] is not None)
        shot(page, "approver-set", page.locator("[data-testid=lic-approver-answer]"))
        shot(page, "approver-set-on-the-card", card(page, B))

        say("  a machine that has never reported:")
        page.get_by_role("button", name="Set an approver").click()
        fill_approver(page, E, "the-laptop", "owner", "personal")
        status, body = save_approver(page)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("an approver on a machine that has never reported is stored, and the Gateway says it cannot approve yet", status == 200 and body["status"] == "never-reported" and body["canApproveNow"] is False)
        check("the page lists it with the Gateway's status and sentence", until(page, lambda: approver_row(page, E).count() == 1, 10000)
              and approver_row(page, E).locator("[data-testid=lic-approver-status]").text_content() == body["statusText"]
              and approver_row(page, E).locator("[data-testid=lic-approver-text]").text_content() == body["text"])
        shot(page, "approver-set-machine-never-reported", approver_row(page, E))

        say("  refused by the Gateway (400): a browser profile that is not a name")
        writes_before = len(sent)
        page.get_by_role("button", name="Set an approver").click()
        fill_approver(page, A, MAC, "owner", "work/1")
        status, body = save_approver(page)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("the approver was refused 400 with a word and a sentence", status == 400 and body.get("code") == "invalid_browser_profile" and bool(body.get("error")))
        refusal = editor(page).locator("[data-testid=lic-approver-error]")
        refusal.wait_for()
        say(f"  the page shows: \"{refusal.text_content()}\"")
        check("the page shows the Gateway's sentence, word for word", refusal.text_content() == body["error"])
        check("the editor stays open with what was typed", editor(page).get_by_label("Browser profile on that machine").input_value() == "work/1")
        check("nothing was stored: the Gateway still lists two approvers, none for account A", [a["accountEmail"] for a in get(port, APPROVERS, OWNER)[1]["approvers"]] == [B, E])
        shot(page, "approver-refused-400", editor(page))
        editor(page).get_by_role("button", name="Cancel").click()
        check("exactly one write was sent for it", len(sent) == writes_before + 1)

        say("  changed from its own row:")
        approver_row(page, B).get_by_role("button", name="Change").click()
        check("the editor opens on what is stored, and its account cannot be changed",
              editor(page).get_by_label("Claude account").input_value() == B and editor(page).get_by_label("Claude account").is_disabled()
              and editor(page).get_by_label("Approving machine").input_value() == MAC and editor(page).get_by_label("Browser profile on that machine").input_value() == "work")
        editor(page).get_by_label("Browser profile on that machine").fill("personal")
        status, body = save_approver(page)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("the change was stored: the Gateway answers the new browser profile", status == 200 and body["browserProfile"] == "personal")
        check("the page lists the new browser profile, from its next read", until(page, lambda: approver_row(page, B).locator("td").nth(2).text_content() == "personal", 10000))
        shot(page, "approver-changed", approver_row(page, B))

        say("  a machine whose Director is connected:")
        page.get_by_role("button", name="Set an approver").click()
        fill_approver(page, A, WINDOWS, "owner", "work")
        status, body = save_approver(page)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("an approver on a machine whose Director is connected is stored, and the Gateway says it can approve now", status == 200 and body["status"] == "ready" and body["canApproveNow"] is True)
        check("the page lists it with the Gateway's status in its tone, and its sentence", until(page, lambda: approver_row(page, A).count() == 1, 10000)
              and approver_row(page, A).locator("[data-testid=lic-approver-status]").text_content() == body["statusText"]
              and ("lic-tone-" + body["statusTone"]) in approver_row(page, A).locator("[data-testid=lic-approver-status]").get_attribute("class")
              and approver_row(page, A).locator("[data-testid=lic-approver-text]").text_content() == body["text"])
        shot(page, "approver-set-ready", approver_row(page, A))
        _, approvers = get(port, APPROVERS, OWNER)
        save("answer-approvers.json", approvers)

        say("  removed:")
        writes_before = len(sent)
        approver_row(page, E).get_by_role("button", name="Remove").click()
        check("the confirmation says what is removed in the Gateway's sentence, and opening it sends nothing",
              dialog(page).locator(".ui-confirm-message").text_content() == [a for a in approvers["approvers"] if a["accountEmail"] == E][0]["text"] and len(sent) == writes_before)
        shot(page, "approver-remove-confirmation")
        status, body = answered(page, "DELETE", APPROVERS, lambda: dialog(page).get_by_role("button", name="Remove").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("DELETE /gateway/licenses/approvers answered 200 and says one was removed", status == 200 and body["removed"] is True)
        check("it names the account in the query and has no body", sent[-1]["path"] == APPROVERS + "?accountEmail=account-e%40example.com" and sent[-1]["body"] is None)
        check("the page shows the Gateway's sentence for it, whole", until(page, lambda: page.locator("[data-testid=lic-approver-answer]").text_content() == body["text"], 10000))
        check("the page read the approvers again: the row is gone and the other is still there", until(page, lambda: approver_row(page, E).count() == 0, 10000) and approver_row(page, B).count() == 1)
        check("the Gateway lists the two that are left", [a["accountEmail"] for a in get(port, APPROVERS, OWNER)[1]["approvers"]] == [A, B])
        shot(page, "approver-removed", page.locator("[data-testid=lic-approver-answer]"))

        # ---- 3. Add a spare ----
        say()
        say("3. Add a spare, by the owner's signed-in browser")
        writes_before = len(sent)
        machine_row(page, WINDOWS).get_by_role("button", name="Add a spare").click()
        add = dialog(page)
        check("the confirmation shows the Gateway's sentence for the machine", add.get_by_text(machines[WINDOWS]["addSpareText"], exact=True).count() == 1)
        check("it cannot be given before an account is typed, and opening it sends nothing", add.get_by_role("button", name="Add a spare").is_disabled() and len(sent) == writes_before)
        add.get_by_label("Claude account to sign in to").fill(E)
        shot(page, "add-spare-confirmation")
        status, body = answered(page, "POST", ADD_SPARE, lambda: add.get_by_role("button", name="Add a spare").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("POST /gateway/licenses/sign-ins/add-spare answered 202", status == 202)
        check("the body was sent as application/json", sent[-1]["contentType"] == "application/json")
        check("the body names the account and the machine's identifier and nothing else", json.loads(sent[-1]["body"]) == {"accountEmail": E, "machineId": machines[WINDOWS]["machineId"]})
        page.wait_for_selector("[data-testid=lic-sign-in-answer]")
        check("the page shows the Gateway's sentence for what was started, whole", page.locator("[data-testid=lic-sign-in-answer-text]").text_content() == body["text"])
        check(f"the whole row is the Gateway's, nothing added: \"{body['entry']['stateText']}\" - {body['entry']['summary']}",
              page.locator("[data-testid=lic-sign-in-answer-row]").text_content() == body["entry"]["stateText"] + " " + body["entry"]["summary"])
        shot(page, "add-spare-started", page.locator("[data-testid=lic-sign-in-answer]"))
        added = body["entry"]["entryId"]
        _, overview = get(port, "/gateway/licenses", OWNER)
        running = [m for m in overview["machines"] if m["machineName"] == WINDOWS][0]["signInsInProgress"]
        say(f"  GET /gateway/licenses now says the-windows-machine has in progress: {json.dumps(running)}")
        check("the Gateway says the sign-in has not ended", len(running) == 1 and running[0]["accountEmail"] == E)
        check("the page shows that sentence on the machine's row", until(page, lambda: machine_row(page, WINDOWS).locator("[data-testid=lic-sign-in-progress]").count() == 1, 10000)
              and machine_row(page, WINDOWS).locator("[data-testid=lic-sign-in-progress]").text_content() == running[0]["text"])
        check("the sign-in is a row of the log on the page", until(page, lambda: page.locator("[data-testid=lic-log-row]").count() == len(get(port, "/gateway/licenses/log", OWNER)[1]["entries"]) >= 1, 10000))
        shot(page, "sign-in-in-progress", machine_row(page, WINDOWS))

        say("  refused by the Gateway (409): the same sign-in again while it is running")
        machine_row(page, WINDOWS).get_by_role("button", name="Add a spare").click()
        dialog(page).get_by_label("Claude account to sign in to").fill(E)
        status, body = answered(page, "POST", ADD_SPARE, lambda: dialog(page).get_by_role("button", name="Add a spare").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Add a spare was refused 409 sign_in_running", status == 409 and body.get("code") == "sign_in_running")
        say(f"  the page shows: \"{refusal_in(page)}\"")
        check("the page shows the Gateway's sentence, word for word, and the confirmation stays open", refusal_in(page) == body["error"] and page.get_by_role("alertdialog").count() == 1)
        check("what was typed stays", dialog(page).get_by_label("Claude account to sign in to").input_value() == E)
        shot(page, "add-spare-refused-409-sign-in-running")
        dialog(page).get_by_role("button", name="Cancel").click()

        say("  the Director answers: signed in")
        command("finish ok")
        entry = ended(added)
        say(f"  the log row is now: \"{entry['stateText']}\" - {entry['summary']}")
        check("the Gateway settled the row from the Director's answer", entry["state"] == "added")
        check("the page reads how it ended from that row of the log, at its next read, and shows it whole",
              until(page, lambda: page.locator("[data-testid=lic-sign-in-answer-row]").text_content() == entry["stateText"] + " " + entry["summary"]))
        check("in the Gateway's tone", ("lic-tone-" + entry["stateTone"]) in page.locator("[data-testid=lic-sign-in-answer-row] .lic-pill").get_attribute("class"))
        check("the sign-in was started once: no second call was sent to find out", len([s for s in sent if s["path"] == ADD_SPARE]) == 2)
        shot(page, "add-spare-signed-in", page.locator("[data-testid=lic-sign-in-answer]"))
        command("windows-holds-e")
        check("when the machine's next report holds the new account, the page shows its card, from its next read", until(page, lambda: card(page, E).count() == 1))
        shot(page, "add-spare-new-account-card", card(page, E))

        say("  refused by the Gateway (409): an account the machine holds")
        machine_row(page, WINDOWS).get_by_role("button", name="Add a spare").click()
        dialog(page).get_by_label("Claude account to sign in to").fill(A)
        status, body = answered(page, "POST", ADD_SPARE, lambda: dialog(page).get_by_role("button", name="Add a spare").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Add a spare was refused 409 already_held", status == 409 and body.get("code") == "already_held")
        say(f"  the page shows: \"{refusal_in(page)}\"")
        check("the page shows the Gateway's sentence, word for word", refusal_in(page) == body["error"])
        shot(page, "add-spare-refused-409-already-held")

        say("  refused by the Gateway (400): what was typed is not an account")
        dialog(page).get_by_label("Claude account to sign in to").fill("not an account")
        status, body = answered(page, "POST", ADD_SPARE, lambda: dialog(page).get_by_role("button", name="Add a spare").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Add a spare was refused 400 invalid_account: the page sent what was typed and the Gateway judged it", status == 400 and body.get("code") == "invalid_account")
        say(f"  the page shows: \"{refusal_in(page)}\"")
        check("the page shows the Gateway's sentence, word for word", until(page, lambda: refusal_in(page) == body["error"], 5000))
        shot(page, "add-spare-refused-400-invalid-account")
        dialog(page).get_by_role("button", name="Cancel").click()

        # ---- 4. Renew now ----
        say()
        say("4. Renew now, by the owner's signed-in browser")
        page.locator("[data-testid=lic-sign-in-answer]").get_by_role("button", name="Dismiss").click()
        _, overview = get(port, "/gateway/licenses", OWNER)
        windows = [m for m in overview["machines"] if m["machineName"] == WINDOWS][0]
        spare = [h for h in windows["spares"] if h["accountEmail"] == B][0]
        writes_before = len(sent)
        holding(page, B, WINDOWS).get_by_role("button", name="Renew now").click()
        check("the confirmation says what it does in the Gateway's sentence for that sign-in, and opening it sends nothing",
              dialog(page).locator(".ui-confirm-message").text_content() == spare["renewNowText"] and len(sent) == writes_before)
        shot(page, "renew-now-confirmation")
        status, body = answered(page, "POST", RENEW, lambda: dialog(page).get_by_role("button", name="Renew now").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("POST /gateway/licenses/sign-ins/renew answered 202", status == 202)
        check("the body was sent as application/json", sent[-1]["contentType"] == "application/json")
        check("the body names the account and the machine's identifier and nothing else", json.loads(sent[-1]["body"]) == {"accountEmail": B, "machineId": windows["machineId"]})
        page.wait_for_selector("[data-testid=lic-sign-in-answer]")
        check("the page shows the Gateway's sentence for what was started, whole", page.locator("[data-testid=lic-sign-in-answer-text]").text_content() == body["text"])
        check("the whole row is the Gateway's, nothing added", page.locator("[data-testid=lic-sign-in-answer-row]").text_content() == body["entry"]["stateText"] + " " + body["entry"]["summary"])
        shot(page, "renew-now-started", page.locator("[data-testid=lic-sign-in-answer]"))
        _, overview = get(port, "/gateway/licenses", OWNER)
        now_says = [h for h in [m for m in overview["machines"] if m["machineName"] == WINDOWS][0]["spares"] if h["accountEmail"] == B][0]
        say(f"  GET /gateway/licenses now says of that sign-in: canRenewNow={now_says['canRenewNow']}, renewNowText=\"{now_says['renewNowText']}\"")
        check("while it runs the Gateway withdraws Renew now for that sign-in, and the page follows",
              now_says["canRenewNow"] is False and until(page, lambda: holding(page, B, WINDOWS).get_by_role("button", name="Renew now").count() == 0, 10000)
              and holding(page, B, WINDOWS).locator("[data-testid=lic-holding-renew-text]").text_content() == now_says["renewNowText"])

        say("  the Director answers: failed, no approver")
        command("finish failed")
        entry = ended(body["entry"]["entryId"])
        say(f"  the log row is now: \"{entry['stateText']}\" - {entry['summary']} ({entry['reasonCode']}: {entry['reasonText']})")
        check("the Gateway settled the row as failed, with a reason", entry["state"] == "failed" and bool(entry["reasonText"]))
        check("the page reads the failure from that row of the log and shows it whole",
              until(page, lambda: page.locator("[data-testid=lic-sign-in-answer-row]").text_content() == entry["stateText"] + " " + entry["summary"]))
        check("with the Gateway's reason, whole, and in the Gateway's tone",
              page.locator("[data-testid=lic-sign-in-answer-reason]").text_content() == entry["reasonText"]
              and ("lic-tone-" + entry["stateTone"]) in page.locator("[data-testid=lic-sign-in-answer-row] .lic-pill").get_attribute("class"))
        shot(page, "renew-now-failed", page.locator("[data-testid=lic-sign-in-answer]"))

        say("  asked again, and the Director answers: renewed")
        check("the Gateway offers Renew now for that sign-in again, and the page follows", until(page, lambda: holding(page, B, WINDOWS).get_by_role("button", name="Renew now").count() == 1))
        holding(page, B, WINDOWS).get_by_role("button", name="Renew now").click()
        status, body = answered(page, "POST", RENEW, lambda: dialog(page).get_by_role("button", name="Renew now").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("the second Renew now answered 202 with a row of its own", status == 202 and body["entry"]["entryId"] != entry["entryId"])
        command("finish ok")
        entry = ended(body["entry"]["entryId"])
        say(f"  the log row is now: \"{entry['stateText']}\" - {entry['summary']}")
        check("the Gateway settled the row as renewed", entry["state"] == "renewed")
        check("the page reads it from that row of the log and shows it whole",
              until(page, lambda: page.locator("[data-testid=lic-sign-in-answer-row]").text_content() == entry["stateText"] + " " + entry["summary"]))
        shot(page, "renew-now-renewed", page.locator("[data-testid=lic-sign-in-answer]"))
        _, log = get(port, "/gateway/licenses/log", OWNER)
        save("answer-log-after-sign-ins.json", log)
        check("the switch log on the page lists every sign-in the Gateway lists: one added, one failed, one renewed",
              until(page, lambda: page.locator("[data-testid=lic-log-row]").count() == len(log["entries"]))
              and sorted(e["state"] for e in log["entries"] if e["kind"] == "sign-in") == ["added", "failed", "renewed"])
        shot(page, "sign-ins-in-the-log", page.locator("[data-testid=lic-log]"))

        # ---- 5. All four writes with a credential that is not a signed-in person: 403 owner_only ----
        say()
        say("5. All four writes with a Director's own credential (a workstation key): refused 403 owner_only")
        before = get(port, APPROVERS, OWNER)[1]["approvers"]
        log_before = len(get(port, "/gateway/licenses/log", OWNER)[1]["entries"])
        page2, sent2 = open_as(WORKSTATION)
        check("that credential reads the page, the approvers with it", page2.locator("[data-testid=lic-account]").count() > 0 and approver_row(page2, B).count() == 1)

        page2.get_by_role("button", name="Set an approver").click()
        fill_approver(page2, E, MAC, "owner", "work")
        status, body = save_approver(page2)
        said(sent2[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("setting an approver was refused 403 owner_only", status == 403 and body.get("code") == "owner_only")
        refusal = editor(page2).locator("[data-testid=lic-approver-error]")
        refusal.wait_for()
        say(f"  the page shows: \"{refusal.text_content()}\"")
        check("the page shows the Gateway's sentence, word for word, and the editor stays open", refusal.text_content() == body["error"] and editor(page2).count() == 1)
        shot(page2, "approver-set-refused-owner-only", editor(page2))
        editor(page2).get_by_role("button", name="Cancel").click()

        approver_row(page2, B).get_by_role("button", name="Remove").click()
        status, body = answered(page2, "DELETE", APPROVERS, lambda: dialog(page2).get_by_role("button", name="Remove").click())
        said(sent2[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("removing an approver was refused 403 owner_only", status == 403 and body.get("code") == "owner_only")
        say(f"  the page shows: \"{refusal_in(page2)}\"")
        check("the page shows the Gateway's sentence, word for word, and the row is still listed", refusal_in(page2) == body["error"] and approver_row(page2, B).count() == 1)
        shot(page2, "approver-remove-refused-owner-only")
        dialog(page2).get_by_role("button", name="Cancel").click()

        machine_row(page2, WINDOWS).get_by_role("button", name="Add a spare").click()
        dialog(page2).get_by_label("Claude account to sign in to").fill("account-f@example.com")
        status, body = answered(page2, "POST", ADD_SPARE, lambda: dialog(page2).get_by_role("button", name="Add a spare").click())
        said(sent2[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Add a spare was refused 403 owner_only", status == 403 and body.get("code") == "owner_only")
        say(f"  the page shows: \"{refusal_in(page2)}\"")
        check("the page shows the Gateway's sentence, word for word, and no answer panel is drawn", refusal_in(page2) == body["error"] and page2.locator("[data-testid=lic-sign-in-answer]").count() == 0)
        shot(page2, "add-spare-refused-owner-only")
        dialog(page2).get_by_role("button", name="Cancel").click()

        holding(page2, A, WINDOWS).get_by_role("button", name="Renew now").click()
        status, body = answered(page2, "POST", RENEW, lambda: dialog(page2).get_by_role("button", name="Renew now").click())
        said(sent2[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Renew now was refused 403 owner_only", status == 403 and body.get("code") == "owner_only")
        say(f"  the page shows: \"{refusal_in(page2)}\"")
        check("the page shows the Gateway's sentence, word for word, and no answer panel is drawn", refusal_in(page2) == body["error"] and page2.locator("[data-testid=lic-sign-in-answer]").count() == 0)
        shot(page2, "renew-now-refused-owner-only")
        dialog(page2).get_by_role("button", name="Cancel").click()
        check("nothing was stored and nothing was started: the approvers and the log are as they were",
              get(port, APPROVERS, OWNER)[1]["approvers"] == before and len(get(port, "/gateway/licenses/log", OWNER)[1]["entries"]) == log_before)

        # ---- 6. A sign-in the page offered, refused because the Director went away ----
        say()
        say("6. Renew now the page offered, refused by the Gateway (409): the machine's Director lost its connection after the page read it")
        holding(page, A, WINDOWS).get_by_role("button", name="Renew now").click()
        dialog(page)
        command("drop-windows")
        status, body = answered(page, "POST", RENEW, lambda: dialog(page).get_by_role("button", name="Renew now").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Renew now was refused 409 director_offline", status == 409 and body.get("code") == "director_offline")
        say(f"  the page shows: \"{refusal_in(page)}\"")
        check("the page shows the Gateway's sentence, word for word", refusal_in(page) == body["error"])
        shot(page, "renew-now-refused-409-director-offline")
        dialog(page).get_by_role("button", name="Cancel").click()
        _, overview = get(port, "/gateway/licenses", OWNER)
        windows = [m for m in overview["machines"] if m["machineName"] == WINDOWS][0]
        say(f"  GET /gateway/licenses now says of the-windows-machine: canAddSpare={windows['canAddSpare']}, addSpareText=\"{windows['addSpareText']}\"")
        check("the Gateway no longer offers a sign-in on that machine, and the page follows at its next read: no button, the Gateway's sentence",
              windows["canAddSpare"] is False
              and until(page, lambda: machine_row(page, WINDOWS).get_by_role("button", name="Add a spare").count() == 0)
              and machine_row(page, WINDOWS).locator("[data-testid=lic-add-spare-text]").text_content() == windows["addSpareText"]
              and page.get_by_role("button", name="Renew now").count() == 0)
        shot(page, "sign-ins-not-offered-director-not-connected", machine_row(page, WINDOWS))

        # ---- 7. Nothing of a sign-in itself reached the browser ----
        say()
        say("7. Nothing of a sign-in itself reached the browser or left it")
        named = sorted(name for name in seen_names if FORBIDDEN_NAME.search(name))
        say(f"  {answers_seen[0]} answers of Licenses routes reached the browser, with {len(seen_names)} different property names")
        check("no property name in any of them is one of a sign-in key, an address a sign-in is approved at, or an approval" + (": " + ", ".join(named) if named else ""),
              answers_seen[0] > 20 and len(named) == 0)
        bodies = [json.loads(s["body"]) for s in sent + sent2 if s["body"] is not None]
        check("every body the browser sent is an account with a machine identifier, or an account with a machine, its user and a browser profile",
              len(bodies) > 8 and all(sorted(b) in (["accountEmail", "machineId"], ["accountEmail", "browserProfile", "machineName", "osUser"]) for b in bodies))
        browser.close()
finally:
    if vite is not None:
        subprocess.run(f"taskkill /PID {vite.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        gateway.stdin.write("\n")
        gateway.stdin.flush()
        gateway.wait(timeout=60)
    except Exception:
        gateway.kill()

say()
if failures:
    say(f"QA RUN FAILED: {len(failures)} of {checks} check(s) did not hold")
    for failure in failures:
        say("  - " + failure)
    sys.exit(1)
say(f"QA RUN DONE: all {checks} checks held")
