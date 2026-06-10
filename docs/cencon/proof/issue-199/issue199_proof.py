"""Issue #199 proof driver: exercise the Cockpit so the persisted log gets one INFO line per
user action, and capture the browser console (with cockpit.debug=1) showing the terminal ws
lifecycle. Run with the cc-director pyenv python. Not a product file - proof tooling only."""
import sys
import time
from pathlib import Path
from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:7470"
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".")
OUT.mkdir(parents=True, exist_ok=True)

console_lines = []

def run():
    with sync_playwright() as p:
        # The pyenv ships chromium 1217 on disk but its Playwright package points at 1223; resolve
        # the real chrome.exe explicitly so no download is needed.
        import glob, os
        root = os.path.join(os.environ["LOCALAPPDATA"], "ms-playwright")
        candidates = glob.glob(os.path.join(root, "chromium-*", "chrome-win64", "chrome.exe"))
        if not candidates:
            raise RuntimeError("no chromium chrome.exe found under " + root)
        exe = sorted(candidates)[-1]
        print("using chrome:", exe)
        browser = p.chromium.launch(headless=True, executable_path=exe, args=["--headless=new"])
        ctx = browser.new_context(viewport={"width": 1600, "height": 1000})
        page = ctx.new_page()
        page.on("console", lambda m: console_lines.append(f"{m.type}: {m.text}"))

        # Turn on browser-side terminal debug BEFORE the cockpit circuit opens the stream.
        page.goto(BASE + "/cockpit", wait_until="domcontentloaded")
        page.evaluate("() => localStorage.setItem('cockpit.debug','1')")
        page.reload(wait_until="domcontentloaded")

        # Let the 2s poll load the live roster from the gateway.
        page.wait_for_timeout(4000)

        # ---- select the first session in the rail (action=select-session) ----
        # The clickable session row is the .sess div (SessionRail.razor).
        page.locator(".sess").first.click(timeout=8000)
        page.wait_for_timeout(1500)

        # ---- open the kebab on that row + a menu action (action=kebab) ----
        try:
            page.locator(".sess-kebab").first.click(timeout=4000)
            page.wait_for_timeout(500)
            # Copy Handover Info is a safe, non-destructive kebab action.
            page.get_by_role("button", name="Copy Handover Info").first.click(timeout=3000)
            page.wait_for_timeout(500)
        except Exception as e:
            print("kebab:", e)

        # ---- switch center tab to Terminal (action=center-tab + TerminalPane connect + ws) ----
        try:
            page.get_by_role("button", name="Terminal").first.click(timeout=5000)
            page.wait_for_timeout(4000)  # let the ws open + first frame arrive
        except Exception as e:
            print("terminal tab:", e)

        # back to Wingman
        try:
            page.get_by_role("button", name="Wingman").first.click(timeout=4000)
            page.wait_for_timeout(800)
        except Exception as e:
            print("wingman tab:", e)

        # ---- switch a right-panel tab (action=right-tab) ----
        for tab in ("Queue", "Director", "Screenshots"):
            try:
                page.get_by_role("button", name=tab, exact=False).first.click(timeout=3000)
                page.wait_for_timeout(500)
            except Exception as e:
                print(f"right tab {tab}:", e)

        # ---- open the New session dialog (action=new-session-open) ----
        try:
            page.get_by_role("button", name="+ New session").click(timeout=4000)
            page.wait_for_timeout(1200)
            # close without submitting (submit would create a real session)
            page.get_by_role("button", name="Cancel").first.click(timeout=3000)
            page.wait_for_timeout(400)
        except Exception as e:
            print("new session:", e)

        # ---- open Settings (action=settings-open) ----
        try:
            page.get_by_role("button", name="Settings").first.click(timeout=4000)
            page.wait_for_timeout(1200)
            page.get_by_role("button", name="Close").first.click(timeout=3000)
            page.wait_for_timeout(400)
        except Exception as e:
            print("settings:", e)

        # ---- open Fan-out (action=fanout requires text+targets; just open it to prove the path) ----
        try:
            page.get_by_role("button", name="Fan-out").first.click(timeout=4000)
            page.wait_for_timeout(800)
            page.get_by_role("button", name="Cancel").first.click(timeout=3000)
        except Exception as e:
            print("fanout:", e)

        # Re-open Terminal tab so the console screenshot has the ws lifecycle on it.
        try:
            page.get_by_role("button", name="Terminal").first.click(timeout=4000)
            page.wait_for_timeout(3000)
        except Exception as e:
            print("terminal re-open:", e)

        page.screenshot(path=str(OUT / "cockpit-ui.png"), full_page=False)
        (OUT / "browser-console.txt").write_text("\n".join(console_lines), encoding="utf-8")
        print(f"captured {len(console_lines)} console lines")
        ctx.close()
        browser.close()

run()
