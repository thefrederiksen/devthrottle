"""QA for Factory Definition, phase 6b: New factory on the Factories screen, on a throwaway Gateway, in the Cockpit.

From a blank Factories screen, as the owner would: New factory with Eric's zip, the draft row under the list, the draft's
factory page (every tab but Definition says it starts when published), the Definition tab, Publish in its confirm, and
the factory on the list with its schedule. And one refusal: New factory again with the same zip, now that eric exists.

It reuses the phase 5b rig (../../phase-5b/rig/run_qa.py) for the Gateway and the person, on its own port and its own
storage root: a Gateway (Debug build of THIS tree) on port 7991, storage root under %TEMP%, Tailscale off,
authentication ON, factory agents on in the rig's OWN config.json, serving this tree's Cockpit build from its wwwroot/c.
No Director is started: a publish of Eric's factory asks no machine anything (it has no secret and no repository), and
"No Director on <machine> is connected now" is a notice, never a refusal. So nothing is launched, and no scheduled task
is read or changed.

The zip is NOT in this repository (it is kept with the mission's private briefs); the rig takes its path.

Usage (from the repository root, after building the Gateway and the Cockpit):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    uv run --no-project --with "requests>=2.31.0" --with playwright python docs/qa/factory-definition/phase-6b/rig/run_qa.py <eric zip> <out dir>
"""

import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
import traceback
import urllib.error
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("p5b", HERE.parents[1] / "phase-5b" / "rig" / "run_qa.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)

ZIP = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[2]).resolve()

p.PORT = 7991
p.GW = f"http://127.0.0.1:{p.PORT}"
p.FACTORY = "eric"
p.RIG = Path(os.environ["TEMP"]) / "factory-definition-p6b-new"
p.GW_ROOT = p.RIG / "gateway-root"
COCKPIT_DIST = p.REPO / "apps" / "cockpit" / "dist"
_shown = p.shown


def shown(text):
    """The phase 5b masking, and the private zip's path as <eric zip>."""
    text = str(text)
    for form in (str(ZIP), str(ZIP).replace("\\", "/"), json.dumps(str(ZIP))[1:-1]):
        text = text.replace(form, "<eric zip>")
    return _shown(text)


p.shown = shown


def gateway_serving():
    try:
        p.get("/factory-definition-schemas", key="qa-not-enrolled")
        return True
    except urllib.error.HTTPError as exc:
        return exc.code in (401, 403)


def read(title, path):
    """One read as the person, kept as answered."""
    answer = p.get(path)
    p.record(title, f"  GET {path}\n" + "".join(f"  {line}\n" for line in json.dumps(answer, indent=2).splitlines()))
    return answer


def text_of(page, selector):
    return page.locator(selector).first.inner_text().strip()


def shoot(page, name, what):
    time.sleep(1.5)
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=True)
    p.record(f"picture {name}.png", f"  {what}\n  address: {page.url.replace(p.GW, '')}")
    print(f"picture {name}.png", flush=True)


def check(what, ok, seen):
    p.record(("PASS " if ok else "FAIL ") + what, f"  seen: {seen}")
    if not ok:
        raise SystemExit(f"FAIL {what}: {seen}")


def drive():
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        browser = pw.chromium.launch(channel="chrome", headless=True)
        try:
            context = browser.new_context(viewport={"width": 1280, "height": 1200})
            context.add_cookies([{"name": "cc-gateway-token", "value": p.PERSON_KEY, "url": p.GW}])
            page = context.new_page()
            page.goto(p.GW + "/", wait_until="domcontentloaded")
            page.evaluate("""(key) => {
                localStorage.setItem('cc.accounts', JSON.stringify([
                  { id: 'rig', label: 'Rig', email: null, deviceKey: key, installId: 'rig' }]));
                localStorage.setItem('cc.activeAccount', 'rig');
                localStorage.setItem('cc.deviceKey', key);
            }""", p.PERSON_KEY)

            p.note("STEP 1 - the blank Factories screen: no factory, no draft, and New factory.")
            page.goto(p.GW + "/factories", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-new-factory-button]", timeout=30000)
            check("New factory is on the blank screen", text_of(page, "[data-testid=fa-new-factory-button]") == "New factory",
                  text_of(page, "[data-testid=fa-new-factory-button]"))
            check("no draft row on the blank screen", page.locator("[data-testid=fa-drafts]").count() == 0,
                  page.locator("[data-testid=fa-drafts]").count())
            shoot(page, "01-blank-factories-screen", "the blank Factories screen")

            p.note("STEP 2 - New factory with Eric's zip: the Cockpit opens the new factory's Definition tab.")
            page.set_input_files("[data-testid=fa-new-factory-file]", str(ZIP))
            page.wait_for_url("**/factories/eric/definition", timeout=30000)
            page.wait_for_selector("[data-testid=fa-def-state]", timeout=30000)
            state = text_of(page, "[data-testid=fa-def-state]")
            check("the tab says version 1 is a draft, nothing published", state.startswith("Nothing is published yet. Version 1"), state)
            check("the tab offers Publish version 1", text_of(page, "[data-testid=fa-def-publish]") == "Publish version 1",
                  text_of(page, "[data-testid=fa-def-publish]"))
            shoot(page, "02-definition-tab-draft", "the draft's Definition tab, right after New factory")

            p.note("STEP 3 - the draft row under the list.")
            page.goto(p.GW + "/factories", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-draft-eric]", timeout=30000)
            row = text_of(page, "[data-testid=fa-draft-eric]")
            check("the draft row reads 'Eric - draft, not published yet'", row.startswith("Eric - draft, not published yet"), row)
            shoot(page, "03-factories-screen-draft-row", "the Factories screen with the draft row")

            p.note("STEP 4 - the draft's other tabs: each says it starts when published, and nothing errors.")
            for tab in ("seats", "overview"):
                page.goto(p.GW + f"/factories/eric{'' if tab == 'overview' else '/' + tab}", wait_until="networkidle")
                page.wait_for_selector("[data-testid=fa-tab-not-yet]", timeout=30000)
                said = text_of(page, "[data-testid=fa-tab-not-yet]")
                check(f"the {tab} tab says it starts when Eric is published", said.startswith("This starts when Eric is published."), said)
                check(f"the {tab} tab shows no error", page.locator(".ui-error-banner, [role=alert]").count() == 0,
                      page.locator(".ui-error-banner, [role=alert]").count())
            shoot(page, "04-draft-page-overview", "the draft's factory page on the Overview tab")

            p.note("STEP 5 - a second New factory with the same zip is refused: eric has a draft already.")
            page.goto(p.GW + "/factories", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-new-factory-button]", timeout=30000)
            page.set_input_files("[data-testid=fa-new-factory-file]", str(ZIP))
            page.wait_for_selector("[data-testid=fa-new-factory-refused]", timeout=30000)
            refused = text_of(page, "[data-testid=fa-new-factory-refused]")
            check("the refusal names the draft and where to import", "already has a draft that is not published yet" in refused, refused)
            shoot(page, "05-new-factory-refused-draft-exists", "New factory refused: Eric already has a draft")

            p.note("STEP 6 - Publish on the Definition tab, through its confirm.")
            page.goto(p.GW + "/factories/eric/definition", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-def-publish]", timeout=30000)
            page.click("[data-testid=fa-def-publish]")
            dialog = page.get_by_role("alertdialog")
            dialog.wait_for(timeout=30000)
            p.record("the Publish confirm, as the Gateway worded it", "".join(f"  {line}\n" for line in dialog.inner_text().splitlines()))
            dialog.get_by_role("button", name="Publish").click()
            page.wait_for_selector("[data-testid=fa-def-outcome]", timeout=120000)
            outcome = text_of(page, "[data-testid=fa-def-outcome]")
            p.record("what the publish said and applied", "".join(f"  {line}\n" for line in outcome.splitlines()))
            check("the publish went through", outcome.startswith("Version 1 is published."), outcome)
            page.wait_for_function("() => !document.querySelector('h1.ui-page-title').innerText.includes('DRAFT')", timeout=30000)
            header = text_of(page, "h1.ui-page-title")
            check("the page header no longer says DRAFT after the publish", "DRAFT" not in header, header)
            shoot(page, "06-definition-tab-published", "the Definition tab after Publish")

            p.note("STEP 7 - the factory on the list, the draft row gone, and its seat with its schedule.")
            page.goto(p.GW + "/factories", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-card-eric]", timeout=30000)
            check("no draft row after the publish", page.locator("[data-testid=fa-drafts]").count() == 0,
                  page.locator("[data-testid=fa-drafts]").count())
            shoot(page, "07-factories-screen-published", "the Factories screen with Eric on the list")
            page.goto(p.GW + "/factories/eric/seats", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-seats-table]", timeout=30000)
            seats = text_of(page, "[data-testid=fa-seats-table]")
            check("the seat Mail Desk runs daily at 08:00", "Mail Desk" in seats and "08:00" in seats, seats)
            shoot(page, "08-seats-tab-schedule", "Eric's Seats tab: Mail Desk and its daily 08:00 start")

            p.note("STEP 8 - New factory with the same zip once more: eric is a live factory now.")
            page.goto(p.GW + "/factories", wait_until="networkidle")
            page.wait_for_selector("[data-testid=fa-new-factory-button]", timeout=30000)
            page.set_input_files("[data-testid=fa-new-factory-file]", str(ZIP))
            page.wait_for_selector("[data-testid=fa-new-factory-refused]", timeout=30000)
            refused = text_of(page, "[data-testid=fa-new-factory-refused]")
            check("the refusal says the factory exists and to import on its page", "that factory already exists" in refused, refused)
            shoot(page, "09-new-factory-refused-exists", "New factory refused: eric exists")
            context.close()
        finally:
            browser.close()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for needed in (p.EXE, COCKPIT_DIST / "index.html", ZIP):
        if not needed.exists():
            raise SystemExit(f"build it first: {needed} does not exist")
    if p.RIG.exists() and p.rig_processes():
        raise SystemExit(f"processes still run from {p.RIG}: {p.rig_processes()}; wait for them to end")
    if p.RIG.exists():
        p.remove_tree(p.RIG)
    p.GW_ROOT.mkdir(parents=True)
    (p.GW_ROOT / "config").mkdir()
    (p.GW_ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    cockpit = p.EXE.parent / "wwwroot" / "c"
    if cockpit.exists():
        shutil.rmtree(cockpit)
    shutil.copytree(COCKPIT_DIST, cockpit)
    data = ZIP.read_bytes()
    p.record("the zip (private; its size and hash only)", f"  <eric zip>  {len(data)} bytes  sha256 {hashlib.sha256(data).hexdigest()}")
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(p.GW_ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN"):
        env.pop(k, None)
    log = open(p.RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(p.EXE), "--port", str(p.PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(p.EXE.parent))
    try:
        p.wait_for("the rig Gateway wrote no gateway.db",
                   lambda: bool(list(p.GW_ROOT.rglob("gateway.db"))) or gw.poll() is not None)
        if gw.poll() is not None:
            raise SystemExit("the rig Gateway exited; see gateway.out.txt")
        db = list(p.GW_ROOT.rglob("gateway.db"))[0]
        p.wait_for("the rig gateway.db did not get its tables", lambda: {"device_credentials"} <=
                   {r[0] for r in p.query(db, "SELECT name FROM sqlite_master WHERE type='table'")})
        p.wait_for("the rig Gateway did not finish starting", gateway_serving, seconds=600, every=2)
        p.enroll(db)
        p.wait_for("the rig Gateway did not answer", lambda: p.get("/factory-definition-schemas") is not None)
        p.note(f"Gateway: {p.GW}, built from this tree, storage root <rig>\\gateway-root made empty for this run, "
               "serving this tree's Cockpit build. No Director.")
        read("before: the Factories list (rows, draftRows, newFactory)", "/gateway/factories")
        drive()
        read("after: the Factories list", "/gateway/factories")
        read("after: the factory's schedules", "/cron/jobs")
    except BaseException:
        traceback.print_exc()
        raise
    finally:
        gw.terminate()
        try:
            gw.wait(timeout=30)
        except subprocess.TimeoutExpired:
            gw.kill()
        log.close()
        (OUT / "transcript.txt").write_text("\n".join(p.TRANSCRIPT), encoding="utf-8", newline="\n")
        print(f"wrote {OUT / 'transcript.txt'}")


if __name__ == "__main__":
    main()
