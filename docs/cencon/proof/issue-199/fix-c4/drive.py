"""Issue #199 criterion-4 proof driver.

Loads the Cockpit (pointed at the stub gateway), selects the ENDPOINT-LESS session,
and screenshots the "no tailnet endpoint" guard. The persisted cockpit log is then
checked (by the harness) for the OnSelect empty-DirectorBase WARNING.
"""
import glob
import os
import sys
import time
from pathlib import Path
from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:7471"
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(".")
OUT.mkdir(parents=True, exist_ok=True)


def chrome():
    root = os.path.join(os.environ["LOCALAPPDATA"], "ms-playwright")
    cands = glob.glob(os.path.join(root, "chromium-*", "chrome-win64", "chrome.exe"))
    if not cands:
        raise RuntimeError("no chromium chrome.exe under " + root)
    return sorted(cands)[-1]


def run():
    exe = chrome()
    print("chrome:", exe, flush=True)
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, executable_path=exe, args=["--headless=new"])
        ctx = browser.new_context(viewport={"width": 1500, "height": 950})
        page = ctx.new_page()
        page.on("console", lambda m: print("console:", m.type, m.text, flush=True))

        page.goto(BASE + "/cockpit", wait_until="domcontentloaded")
        # let the 2s poll load the roster from the stub gateway
        page.wait_for_timeout(5000)

        # First select the session WITH an endpoint (control: no WARNING expected).
        page.get_by_text("alpha (has endpoint)", exact=False).first.click(timeout=8000)
        page.wait_for_timeout(1500)

        # Now select the ENDPOINT-LESS session -> blank-terminal failure mode -> WARNING.
        page.get_by_text("beta (NO endpoint)", exact=False).first.click(timeout=8000)
        page.wait_for_timeout(2000)

        page.screenshot(path=str(OUT / "nodir-guard.png"), full_page=True)
        print("OK selected endpoint-less session", flush=True)
        ctx.close()
        browser.close()


if __name__ == "__main__":
    run()
