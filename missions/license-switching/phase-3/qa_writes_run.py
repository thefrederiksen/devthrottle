# THE QA RUN OF THE TWO WRITES OF THE LICENSES PAGE AGAINST A REAL LOCAL GATEWAY (License Switching, phase 3).
#
# Switch now and the renewal date of a subscription, each sent by the real page in a real browser to the real routes
# of a Gateway built from this tree, with a screenshot of each write and of its failure. Also: an account barred from
# the automatic choice, and a failed sign-in renewal the Gateway stored from a machine's report.
#
# One command, everything in the foreground, everything stopped at the end:
#   1. starts qa-gateway (the product's own GatewayHost, hosted mode, throwaway storage, seeded license data);
#   2. starts the Cockpit's own development server, forwarding Gateway routes to that Gateway;
#   3. opens the page in a browser enrolled with a seeded key - NO request is intercepted or stubbed - and does each
#      write the way a person does, recording what the browser sent and what the Gateway answered;
#   4. stops the development server and the Gateway.
#
# No Director is started and no Claude sign-in of this machine is read or touched: the Gateway's storage is a
# throwaway folder, and the usage reports in it are made up by qa-gateway.
#
# Run from anywhere:  python missions\license-switching\phase-3\qa_writes_run.py     (after `npm ci` at the repository root)
import datetime, json, os, subprocess, sys, time, urllib.request, urllib.error
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT = os.path.join(HERE, "qa-writes")
os.makedirs(OUT, exist_ok=True)
VITE_PORT = 5199
A, B = "account-a@example.com", "account-b@example.com"
SWITCH_NOW, RENEWAL = "/gateway/licenses/switch-now", "/gateway/licenses/accounts/renewal"

LOG = open(os.path.join(OUT, "qa-writes-run-output.txt"), "w", encoding="utf-8", newline="\n")
failures = []


def say(text=""):
    print(text)
    sys.stdout.flush()
    LOG.write(text + "\n")
    LOG.flush()


def check(what, ok):
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
    say(f"Gateway on loopback port {port}, storage under a throwaway folder")

    def command(word):
        gateway.stdin.write(word + "\n")
        gateway.stdin.flush()
        answer = gateway.stdout.readline().strip()
        say(f"qa-gateway {word} -> {answer}")
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

        def open_as(key):
            """The Licenses page in a browser enrolled with `key`. Returns the page and every write it sends."""
            context = browser.new_context(viewport={"width": 1440, "height": 1000})
            context.add_init_script(f"localStorage.setItem('cc.deviceKey', {json.dumps(key)})")
            page = context.new_page()
            sent = []

            def on_request(request):
                path = request.url.split(str(VITE_PORT))[1]
                if path in (SWITCH_NOW, RENEWAL):
                    sent.append({"method": request.method, "path": path, "contentType": request.headers.get("content-type"), "body": request.post_data})

            page.on("request", on_request)
            page.goto(f"http://localhost:{VITE_PORT}/licenses")
            page.wait_for_selector("[data-testid=lic-account]")
            page.wait_for_selector("[data-testid=lic-schedule-row]")
            return page, sent

        def card(page, email):
            return page.get_by_role("region", name=email, exact=True)

        def shot(target, name):
            target.page.wait_for_timeout(500) if hasattr(target, "page") else target.wait_for_timeout(500)
            target.screenshot(path=os.path.join(OUT, name + ".png"))
            say(f"  screenshot qa-writes/{name}.png")

        def answered(page, path, act):
            """Do `act` and return the Gateway's answer to the write it causes: (status, body)."""
            with page.expect_response(lambda r: r.url.endswith(path), timeout=100000) as info:
                act()
            response = info.value
            return response.status, json.loads(response.text())

        def said(sent_one):
            say(f"  the browser sent: {sent_one['method']} {sent_one['path']}  Content-Type: {sent_one['contentType']}  body: {sent_one['body']}")

        def save_renewal(page, email, date):
            card(page, email).get_by_role("button", name="Set renewal date").click()
            card(page, email).get_by_label(f"Renewal date of {email}").fill(date)
            return answered(page, RENEWAL, lambda: card(page, email).get_by_role("button", name="Save").click())

        def open_switch_now(page, email):
            card(page, email).get_by_role("button", name="Switch now").click()
            page.wait_for_selector("[role=alertdialog]")
            return page.get_by_role("alertdialog")

        def confirm(dialog):
            return dialog.get_by_role("button", name="Switch now")

        renews_on = (datetime.date.today() + datetime.timedelta(days=20)).isoformat()

        # ---- 1. A stored failed renewal of a sign-in, as the Gateway gives it ----
        say()
        say("1. A failed renewal of a sign-in, stored by the Gateway from a machine's usage report")
        status, overview = get(port, "/gateway/licenses", seeded["ownerKey"])
        save("answer-overview.json", overview)
        windows = [m for m in overview["machines"] if m["machineName"] == "the-windows-machine"][0]
        spare = [h for h in windows["spares"] if h["accountEmail"] == B][0]
        say(f"  GET /gateway/licenses -> {status}; the holding of {B} on the-windows-machine says:")
        say(f"    renewalFailedAtUtc: {spare['renewalFailedAtUtc']}")
        say(f"    renewalFailedText:  {spare['renewalFailedText']}")
        say(f"    renewalFailedTone:  {spare['renewalFailedTone']}")
        page, sent = open_as(seeded["ownerKey"])
        failed = card(page, B).locator("[data-testid=lic-holding-renewal-failed]")
        check("the page shows exactly one failed renewal on the card of account B", failed.count() == 1)
        check("its words are the Gateway's renewalFailedText", failed.count() == 1 and failed.inner_text() == spare["renewalFailedText"])
        check("it is drawn in the Gateway's tone", failed.count() == 1 and ("lic-tone-" + spare["renewalFailedTone"]) in failed.get_attribute("class"))
        check("no other card shows a failed renewal", page.locator("[data-testid=lic-holding-renewal-failed]").count() == 1)
        shot(card(page, B), "failed-renewal-stored")

        # ---- 2. The renewal date, set and cleared by the owner's signed-in browser ----
        say()
        say("2. The renewal date of a subscription, by the owner's signed-in browser")
        before = card(page, A).locator("[data-testid=lic-account-renewal]").inner_text()
        say(f"  before: \"{before}\"")
        status, body = save_renewal(page, A, renews_on)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("PUT /gateway/licenses/accounts/renewal answered 200", status == 200)
        check("the body was sent as application/json", sent[-1]["contentType"] == "application/json")
        check("the body names the account and the date", json.loads(sent[-1]["body"]) == {"accountEmail": A, "renewsOn": renews_on})
        page.wait_for_selector("[data-testid=lic-renewal-editor]", state="detached")
        shown = card(page, A).locator("[data-testid=lic-account-renewal]").inner_text()
        say(f"  after:  \"{shown}\"")
        check("the card shows the Gateway's renewsText", shown == "Max - " + body["renewsText"] and body["renewsText"] != "Renewal date not set")
        _, again = get(port, "/gateway/licenses", seeded["ownerKey"])
        stored = [a for a in again["accounts"] if a["accountEmail"] == A][0]
        say(f"  GET /gateway/licenses now says for {A}: renewsOn={stored['renewsOn']}, renewsText=\"{stored['renewsText']}\"")
        check("the date is stored: the next read of the overview carries it", stored["renewsOn"] == renews_on)
        shot(card(page, A), "renewal-date-set")

        card(page, A).get_by_role("button", name="Set renewal date").click()
        status, body = answered(page, RENEWAL, lambda: card(page, A).get_by_role("button", name="Clear date").click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("clearing sends an explicit null", sent[-1]["body"] == json.dumps({"accountEmail": A, "renewsOn": None}, separators=(",", ":")))
        page.wait_for_selector("[data-testid=lic-renewal-editor]", state="detached")
        shown = card(page, A).locator("[data-testid=lic-account-renewal]").inner_text()
        check("the card shows the Gateway's sentence for no date", status == 200 and shown == "Max - " + body["renewsText"] == "Max - Renewal date not set")
        shot(card(page, A), "renewal-date-cleared")
        save_renewal(page, A, renews_on)
        page.wait_for_selector("[data-testid=lic-renewal-editor]", state="detached")

        # ---- 3. Switch now, accepted ----
        say()
        say("3. Switch now, by the owner's signed-in browser: accepted")
        account = [a for a in again["accounts"] if a["accountEmail"] == A][0]
        say(f"  the Gateway offers it: canSwitchNow={account['canSwitchNow']}, switchNowTargetAccountEmail={account['switchNowTargetAccountEmail']}")
        say(f"  switchNowText: \"{account['switchNowText']}\"")
        writes_before = len(sent)
        dialog = open_switch_now(page, A)
        check("opening the confirmation sends nothing", len(sent) == writes_before)
        check("the confirmation shows the Gateway's sentence", dialog.get_by_text(account["switchNowText"], exact=True).count() == 1)
        shot(page, "switch-now-confirmation")
        status, body = answered(page, SWITCH_NOW, lambda: confirm(dialog).click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("POST /gateway/licenses/switch-now answered 200", status == 200)
        check("the body was sent as application/json", sent[-1]["contentType"] == "application/json")
        check("the body names the account and the target", json.loads(sent[-1]["body"]) == {"accountEmail": A, "targetAccountEmail": account["switchNowTargetAccountEmail"]})
        check("exactly one Switch now was sent", len([s for s in sent if s["path"] == SWITCH_NOW]) == 1)
        page.wait_for_selector("[data-testid=lic-switch-answer]")
        check("the page shows the Gateway's sentence for the outcome", page.locator("[data-testid=lic-switch-answer-text]").inner_text() == body["text"])
        rows = page.locator("[data-testid=lic-switch-answer-machine]")
        check("one row per machine that was sent a switch", rows.count() == len(body["switches"]) and rows.count() > 0)
        for index, machine in enumerate(body["switches"]):
            check(f"the row of {machine['label']} is the Gateway's: \"{machine['stateText']}\" - {machine['summary']}",
                  machine["stateText"] in rows.nth(index).inner_text() and machine["summary"] in rows.nth(index).inner_text())
        page.locator("[data-testid=lic-switch-answer]").scroll_into_view_if_needed()
        shot(page, "switch-now-accepted")
        # The page read the log again; the rows of this switch are in it, found by the entryId of the answer.
        page.wait_for_timeout(1500)
        _, log = get(port, "/gateway/licenses/log", seeded["ownerKey"])
        save("answer-log-after-switch-now.json", log)
        ids = {entry["entryId"] for entry in log["entries"]}
        check("every machine's entryId in the answer is a row of the switch log", all(m["entryId"] in ids for m in body["switches"]))
        check("the switch log on the page lists those rows", page.locator("[data-testid=lic-log-row]").count() == len(log["entries"]))
        page.locator("[data-testid=lic-log]").scroll_into_view_if_needed()
        shot(page, "switch-now-accepted-log")
        check("the same Switch now was never sent again", len([s for s in sent if s["path"] == SWITCH_NOW]) == 1)

        # ---- 4. Both writes with a credential that is not a signed-in person: 403 owner_only ----
        say()
        say("4. Both writes with a Director's own credential (a workstation key): refused 403 owner_only")
        page, sent = open_as(seeded["workstationKey"])
        check("that credential reads the page", page.locator("[data-testid=lic-account]").count() > 0)
        before = card(page, B).locator("[data-testid=lic-account-renewal]").inner_text()
        status, body = save_renewal(page, B, renews_on)
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("the renewal date was refused 403 owner_only", status == 403 and body.get("code") == "owner_only")
        refusal = card(page, B).locator("[data-testid=lic-renewal-error]")
        refusal.wait_for()
        say(f"  the page shows: \"{refusal.inner_text()}\"")
        check("the page shows the Gateway's sentence, word for word", refusal.inner_text() == body["error"])
        check("the date on the card is unchanged", card(page, B).locator("[data-testid=lic-account-renewal]").inner_text() == before)
        shot(card(page, B), "renewal-date-refused-owner-only")
        card(page, B).get_by_role("button", name="Cancel").click()

        dialog = open_switch_now(page, A)
        status, body = answered(page, SWITCH_NOW, lambda: confirm(dialog).click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Switch now was refused 403 owner_only", status == 403 and body.get("code") == "owner_only")
        page.wait_for_selector(".ui-confirm-error")
        say(f"  the page shows: \"{page.locator('.ui-confirm-error').inner_text()}\"")
        check("the page shows the Gateway's sentence, word for word", page.locator(".ui-confirm-error").inner_text() == body["error"])
        check("the confirmation stays open and no answer panel is drawn", page.get_by_role("alertdialog").count() == 1 and page.locator("[data-testid=lic-switch-answer]").count() == 0)
        shot(page, "switch-now-refused-owner-only")

        # ---- 5. Switch now refused by the Gateway: 409 ----
        say()
        say("5. Switch now the page offered, refused by the Gateway (409): the target filled up after the page read it")
        page, sent = open_as(seeded["refusedKey"])
        dialog = open_switch_now(page, A)
        command("fill-b")
        status, body = answered(page, SWITCH_NOW, lambda: confirm(dialog).click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("Switch now was refused 409 with a word and a sentence", status == 409 and bool(body.get("code")) and bool(body.get("error")))
        page.wait_for_selector(".ui-confirm-error")
        say(f"  the page shows: \"{page.locator('.ui-confirm-error').inner_text()}\"")
        check("the page shows the Gateway's sentence, word for word", page.locator(".ui-confirm-error").inner_text() == body["error"])
        check("no answer panel is drawn", page.locator("[data-testid=lic-switch-answer]").count() == 0)
        shot(page, "switch-now-refused-409")

        # ---- 6. A barred account, and Switch now where the Gateway chose no target ----
        say()
        say("6. An account barred from the automatic choice, and Switch now where the Gateway chose no target")
        status, overview = get(port, "/gateway/licenses", seeded["barredKey"])
        save("answer-overview-barred.json", overview)
        barred = [a for a in overview["accounts"] if a["accountEmail"] == B][0]
        account = [a for a in overview["accounts"] if a["accountEmail"] == A][0]
        say(f"  GET /gateway/licenses -> {status}; {B} says:")
        say(f"    barredUntilUtc: {barred['barredUntilUtc']}")
        say(f"    barredText:     {barred['barredText']}")
        say(f"  {A} says: canSwitchNow={account['canSwitchNow']}, switchNowTargetAccountEmail={account['switchNowTargetAccountEmail']}")
        say(f"    switchNowText: \"{account['switchNowText']}\"")
        check("the Gateway bars account B and gives both fields", barred["barredUntilUtc"] is not None and barred["barredText"] is not None)
        check("the Gateway offers Switch now on account A and chooses no target", account["canSwitchNow"] is True and account["switchNowTargetAccountEmail"] is None)
        page, sent = open_as(seeded["barredKey"])
        line = card(page, B).locator("[data-testid=lic-account-barred]")
        check("the card of account B shows the Gateway's barredText", line.count() == 1 and barred["barredText"] in line.inner_text())
        check("and the time the Gateway gave, as that time", line.count() == 1 and line.locator("time").get_attribute("datetime") == barred["barredUntilUtc"])
        check("no other card says it is barred", page.locator("[data-testid=lic-account-barred]").count() == 1)
        shot(card(page, B), "barred-account")

        dialog = open_switch_now(page, A)
        check("no target is selected", dialog.locator("input[type=radio]:checked").count() == 0)
        check("the confirmation cannot be given", confirm(dialog).is_disabled())
        confirm(dialog).click(force=True)
        page.wait_for_timeout(1500)
        check("pressing it sends nothing: no Switch now left the browser", len(sent) == 0)
        shot(page, "switch-now-no-target-nothing-sent")
        dialog.get_by_role("radio", name=B).check()
        check("with a target picked by hand it can be given", confirm(dialog).is_enabled())
        status, body = answered(page, SWITCH_NOW, lambda: confirm(dialog).click())
        said(sent[-1])
        say(f"  the Gateway answered {status}: {json.dumps(body)}")
        check("the one call sent names the target picked by hand", len(sent) == 1 and json.loads(sent[0]["body"]) == {"accountEmail": A, "targetAccountEmail": B})
        check("the Gateway accepted it: a barred account can be chosen by hand", status == 200 and body.get("targetAccountEmail") == B)
        page.wait_for_selector("[data-testid=lic-switch-answer]")
        page.locator("[data-testid=lic-switch-answer]").scroll_into_view_if_needed()
        shot(page, "switch-now-target-picked-by-hand")
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
    say(f"QA RUN FAILED: {len(failures)} check(s) did not hold")
    for failure in failures:
        say("  - " + failure)
    sys.exit(1)
say("QA RUN DONE: every check held")
