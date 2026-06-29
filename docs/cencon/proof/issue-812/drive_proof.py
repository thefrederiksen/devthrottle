"""
Drive the session-management proof for issue #812 against the mock Gateway, at a phone-sized
viewport. Captures one (or more) screenshots per acceptance criterion into
docs/cencon/proof/issue-812/ and copies the wire-log for the list/create/hold/remove bodies +
Bearer captures in both auth modes.

AC1  machine list from GET /directors            -> 02-newsession-machines.png
AC2  repo list from GET /repos + manual entry    -> 03-newsession-repos.png
AC3  create (POST /directors/{id}/sessions) + new session in roster + opened session screen
                                                  -> 04-created-session-screen.png, 05-roster-after-create.png
AC4  Hold -> Resume + held; Resume clears hold    -> 06-held-resume-button.png, 07-resumed.png
AC5  Remove confirm; cancel does nothing; confirm DELETE -> back to roster, gone
                                                  -> 08-remove-confirm.png, 09-after-cancel.png, 10-roster-after-remove.png
AC6  held parity (session screen + roster)        -> 11-session-held.png, 12-roster-held-parity.png
AC7  Bearer in both auth modes                    -> requests-auth-on.jsonl / requests-auth-off.jsonl + 13-authoff-*.png
"""
import json
import subprocess
import sys
import time
import urllib.request
import urllib.error
from pathlib import Path
from playwright.sync_api import sync_playwright

HERE = Path(__file__).parent
WT = HERE.parents[3]  # docs/cencon/proof/issue-812 -> worktree root
DIST = WT / "mobile" / "dist"
OUT = HERE
OUT.mkdir(parents=True, exist_ok=True)
EXISTING_SID = "812e0000-0000-0000-0000-000000000001"
PY = sys.executable
VIEWPORT = {"width": 390, "height": 844}


def wait_port(port, timeout=20):
    for _ in range(timeout * 5):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/sessions", timeout=1).read()
            return True
        except urllib.error.HTTPError:
            return True
        except Exception:
            time.sleep(0.2)
    return False


def start_server(port, auth, log_path):
    p = subprocess.Popen([PY, str(HERE / "mock_gateway.py"), str(DIST), str(port), auth, str(log_path)])
    if not wait_port(port):
        raise RuntimeError(f"server on {port} did not start")
    return p


def shot(page, name):
    page.screenshot(path=str(OUT / name))
    print("captured", name)


def run_auth_on(pw):
    port = 7868
    log = OUT / "requests-auth-on.jsonl"
    srv = start_server(port, "on", log)
    browser = pw.chromium.launch()
    ctx = browser.new_context(viewport=VIEWPORT, device_scale_factor=2)
    page = ctx.new_page()
    base = f"http://127.0.0.1:{port}/m"
    notes = []
    try:
        # Roster with the "+ New session" entry (issue #812) and the existing session.
        page.goto(base + "/", wait_until="domcontentloaded")
        page.wait_for_timeout(800)
        shot(page, "01-roster.png")

        # AC1: open the add flow -> Step 1 lists the live machines from GET /directors.
        page.get_by_role("link", name="New session").click()
        page.wait_for_timeout(900)
        shot(page, "02-newsession-machines.png")

        # AC2: the first machine is default-selected, so its repos load (GET /repos); the manual
        # path entry is also present. Scroll so the repo list + manual entry are visible.
        page.locator(".newsession-manual-label").scroll_into_view_if_needed()
        page.wait_for_timeout(400)
        shot(page, "03-newsession-repos.png")

        # AC3: complete the flow by tapping a recent repo -> POST /directors/{id}/sessions, then the
        # app opens the new session (Terminal). Capture the opened session screen.
        page.get_by_text("D:\\ReposFred\\devthrottle", exact=True).first.click()
        page.wait_for_timeout(1500)
        created_url = page.url
        created_sid = created_url.rsplit("/session/", 1)[-1]
        shot(page, "04-created-session-screen.png")
        notes.append(f"created session id = {created_sid} (opened at {created_url})")

        # ... and the new session now appears in the Home roster.
        page.goto(base + "/", wait_until="domcontentloaded")
        page.wait_for_timeout(900)
        shot(page, "05-roster-after-create.png")

        # AC4: on a session screen, Hold -> the session shows held and the button reads Resume.
        page.goto(f"{base}/session/{EXISTING_SID}", wait_until="domcontentloaded")
        page.wait_for_timeout(1200)
        page.get_by_role("button", name="Hold", exact=True).click()
        page.wait_for_timeout(900)
        shot(page, "06-held-resume-button.png")
        # ... pressing Resume clears the hold.
        page.get_by_role("button", name="Resume", exact=True).click()
        page.wait_for_timeout(900)
        shot(page, "07-resumed.png")

        # AC6: held parity - put the session on hold, capture the session screen, then the roster
        # (the same session reads held in both).
        page.get_by_role("button", name="Hold", exact=True).click()
        page.wait_for_timeout(900)
        shot(page, "11-session-held.png")
        page.goto(base + "/", wait_until="domcontentloaded")
        page.wait_for_timeout(900)
        shot(page, "12-roster-held-parity.png")

        # AC5: Remove shows a confirmation; Cancel does nothing; confirming DELETEs and returns to
        # the roster with the session gone.
        page.goto(f"{base}/session/{EXISTING_SID}", wait_until="domcontentloaded")
        page.wait_for_timeout(1000)
        page.locator(".manage-remove").click()  # the large Remove button -> opens the confirmation
        page.wait_for_timeout(500)
        shot(page, "08-remove-confirm.png")
        # Cancel -> dialog closes, still on the session screen, no DELETE sent yet.
        page.locator(".confirm-cancel").click()
        page.wait_for_timeout(500)
        delete_before_confirm = sum(
            1 for line in log.read_text(encoding="utf-8").splitlines()
            if json.loads(line)["method"] == "DELETE")
        shot(page, "09-after-cancel.png")
        notes.append(f"DELETE calls after Cancel (must be 0) = {delete_before_confirm}")
        # Now Remove for real -> confirm in the dialog -> DELETE -> back to roster.
        page.locator(".manage-remove").click()
        page.wait_for_timeout(400)
        page.locator(".confirm-remove").click()
        page.wait_for_timeout(1200)
        after_url = page.url
        shot(page, "10-roster-after-remove.png")
        gone = EXISTING_SID not in json.dumps(json.loads(
            urllib.request.urlopen(urllib.request.Request(
                f"http://127.0.0.1:{port}/sessions",
                headers={"Authorization": "Bearer test-gateway-token-812"})).read().decode()))
        notes.append(f"after Remove the app navigated to {after_url}; existing session present in roster = {not gone}")

        (OUT / "ac5-cancel-and-remove.txt").write_text("\n".join(notes) + "\n", encoding="utf-8")
    finally:
        ctx.close()
        browser.close()
        srv.terminate()
        try:
            srv.wait(timeout=5)
        except Exception:
            srv.kill()


def run_auth_off(pw):
    port = 7869
    log = OUT / "requests-auth-off.jsonl"
    srv = start_server(port, "off", log)
    browser = pw.chromium.launch()
    ctx = browser.new_context(viewport=VIEWPORT, device_scale_factor=2)
    page = ctx.new_page()
    base = f"http://127.0.0.1:{port}/m"
    try:
        # AC7: the whole flow works with global Gateway auth OFF (no Bearer attached). Open the add
        # flow, create a session, then Hold it - all 2xx with no Authorization header in the log.
        page.goto(base + "/", wait_until="domcontentloaded")
        page.wait_for_timeout(700)
        page.get_by_role("link", name="New session").click()
        page.wait_for_timeout(900)
        shot(page, "13-authoff-newsession.png")
        page.get_by_text("D:\\ReposFred\\devthrottle", exact=True).first.click()
        page.wait_for_timeout(1500)
        page.get_by_role("button", name="Hold", exact=True).click()
        page.wait_for_timeout(900)
        shot(page, "14-authoff-session-hold.png")
    finally:
        ctx.close()
        browser.close()
        srv.terminate()
        try:
            srv.wait(timeout=5)
        except Exception:
            srv.kill()


def main():
    with sync_playwright() as pw:
        run_auth_on(pw)
        run_auth_off(pw)
    print("DONE")


if __name__ == "__main__":
    main()
