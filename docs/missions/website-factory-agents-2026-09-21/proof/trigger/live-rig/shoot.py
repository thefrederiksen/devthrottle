"""Cockpit screenshots of the Factory Agents area on the isolated live-check stack (Gateway on port 7898).

Playwright as a library, deliberately: a repeatable scripted run against a throwaway local Gateway with no
authentication, not interactive work in a signed-in browser profile.

Usage: shoot.py <out dir> <prefix> <path> [<path> ...]
Each path is a Cockpit route; the screenshot is named <prefix>-<n>.png in the order given.
"""
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

GW = "http://127.0.0.1:7898"  # the Cockpit is served at the site root


def main():
    out, prefix, paths = Path(sys.argv[1]), sys.argv[2], sys.argv[3:]
    out.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        ctx = browser.new_context(viewport={"width": 1440, "height": 950})
        # The rig runs with no authentication; any device key passes the Cockpit's own gate.
        ctx.add_init_script("try { localStorage.setItem('cc.deviceKey', 'wbf-live-no-auth'); } catch (e) {}")
        page = ctx.new_page()
        for n, path in enumerate(paths, 1):
            page.goto(f"{GW}{path}")
            page.wait_for_load_state("networkidle")
            page.wait_for_timeout(1500)
            name = f"{prefix}-{n}.png"
            page.screenshot(path=str(out / name), full_page=True)
            print("saved", name, "<-", path)
        browser.close()


if __name__ == "__main__":
    main()
