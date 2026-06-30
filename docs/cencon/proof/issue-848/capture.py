"""
Issue #848 proof capture. Drives the built mobile PWA (served by mock-server.py) on an iPhone-sized
viewport and captures the "+ New session" machine picker before/after a live tick, plus the repo
step and the create flow (AC7 no-regression). ASCII only; loopback only.

Run:  python capture.py <base-url> <out-dir>
  e.g. python capture.py http://127.0.0.1:8850 .
"""
import json
import sys
import time

from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8850"
OUT = sys.argv[2] if len(sys.argv) > 2 else "."


def rows(page):
    """Return [{name, subtitle}] for every machine row, in display order."""
    return page.eval_on_selector_all(
        "ul.roster li .row-body",
        """els => els.map(e => ({
              name: (e.querySelector('.row-name')||{}).textContent || '',
              subtitle: (e.querySelector('.row-context')||{}).textContent || ''
           }))""",
    )


def machine_rows(page):
    # Step 1 is the first .group; scope to it so repo rows (Step 2) are excluded.
    return page.eval_on_selector_all(
        "section.group:has(h2:text('1. Machine')) ul.roster li .row-body",
        """els => els.map(e => ({
              name: (e.querySelector('.row-name')||{}).textContent || '',
              subtitle: (e.querySelector('.row-context')||{}).textContent || ''
           }))""",
    )


def main():
    result = {}
    with sync_playwright() as p:
        browser = p.chromium.launch()
        ctx = browser.new_context(
            viewport={"width": 390, "height": 844},
            device_scale_factor=2,
            is_mobile=True,
            has_touch=True,
            user_agent=(
                "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) "
                "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1"
            ),
        )
        page = ctx.new_page()
        page.set_default_timeout(15000)
        page.goto(BASE + "/m/new", wait_until="networkidle")
        page.wait_for_selector("ul.roster li .row-context")
        time.sleep(0.5)

        t0 = machine_rows(page)
        result["t0_machine_rows"] = t0
        page.screenshot(path=OUT + "/ac1-ac3-ac5-ac6-machine-list-t0.png", full_page=True)
        print("T0 machine rows:")
        for r in t0:
            print("  %-14s | %s" % (r["name"], r["subtitle"]))

        # AC2 live tick: wait across a minute boundary, recapture, show the uptime advanced.
        print("waiting 65s for the live uptime tick...")
        time.sleep(65)
        t1 = machine_rows(page)
        result["t1_machine_rows"] = t1
        page.screenshot(path=OUT + "/ac2-machine-list-t1-after-65s.png", full_page=True)
        print("T1 machine rows (after 65s):")
        for r in t1:
            print("  %-14s | %s" % (r["name"], r["subtitle"]))

        # AC7 no-regression: the default-selected machine's repos loaded (Step 2). Capture, then
        # tap a repo to create a session and confirm the app navigates to the session view.
        repo_status = page.eval_on_selector(
            "section.group:has(h2:text('2. Repository')) .status-line",
            "e => e.textContent",
        )
        result["repo_status"] = repo_status
        print("Repo step status:", repo_status)
        page.screenshot(path=OUT + "/ac7-repos-loaded.png", full_page=True)

        page.click("section.group:has(h2:text('2. Repository')) ul.roster li button")
        page.wait_for_url("**/m/session/**", timeout=15000)
        result["after_create_url"] = page.url
        print("After create, URL =", page.url)
        page.screenshot(path=OUT + "/ac7-after-create.png")

        ctx.close()
        browser.close()

    with open(OUT + "/capture-data.json", "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print("WROTE capture-data.json")


if __name__ == "__main__":
    main()
