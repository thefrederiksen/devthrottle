"""
Capture session-view screenshots for the wingman-state report.

Opens the Session tab of /sessions/<sid>/view and screenshots it. Polls the
Control API for the session color so it can grab the GREEN idle state (the
scenario that used to flip-flop red) as well as the current state.

Usage:
    python capture.py --base http://127.0.0.1:7880 --sid <id> --out screenshots
"""
import argparse
import json
import sys
import time
import urllib.request
from playwright.sync_api import sync_playwright

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def get_state(base, sid):
    try:
        with urllib.request.urlopen(f"{base}/sessions/{sid}", timeout=5) as r:
            d = json.load(r)
        return d.get("activityState", "?"), d.get("statusColor", "?")
    except Exception as e:
        return "err", str(e)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", required=True)
    ap.add_argument("--sid", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    with sync_playwright() as p:
        b = p.chromium.launch(headless=True)
        ctx = b.new_context(viewport={"width": 1100, "height": 820}, ignore_https_errors=True)
        page = ctx.new_page()
        page.goto(f"{args.base}/sessions/{args.sid}/view", wait_until="domcontentloaded")
        page.wait_for_timeout(1500)

        # Make sure we are on the Session tab (the wingman banner lives there).
        tab = page.query_selector('.tab[data-view="session"]')
        if tab:
            tab.click()
            page.wait_for_timeout(800)

        # Try to catch the GREEN idle state within ~70s (it appears between the
        # turn finishing and the global stop-hook re-activating).
        green_shot = False
        deadline = time.time() + 70
        while time.time() < deadline:
            st, color = get_state(args.base, args.sid)
            if color == "green":
                page.reload(wait_until="domcontentloaded")
                page.wait_for_timeout(1200)
                t2 = page.query_selector('.tab[data-view="session"]')
                if t2:
                    t2.click(); page.wait_for_timeout(600)
                page.screenshot(path=f"{args.out}/session_idle_green.png", full_page=False)
                print(f"captured GREEN idle (state={st})")
                green_shot = True
                break
            page.wait_for_timeout(2500)

        # Always capture whatever the current state is too.
        st, color = get_state(args.base, args.sid)
        page.reload(wait_until="domcontentloaded")
        page.wait_for_timeout(1200)
        t3 = page.query_selector('.tab[data-view="session"]')
        if t3:
            t3.click(); page.wait_for_timeout(600)
        page.screenshot(path=f"{args.out}/session_current.png", full_page=False)
        print(f"captured current state={st} color={color}; green_shot={green_shot}")

        ctx.close(); b.close()


if __name__ == "__main__":
    main()
