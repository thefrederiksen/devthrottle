"""Screenshot the Schedule page create modal showing the run-complete notify controls (#622)."""
import sys
from playwright.sync_api import sync_playwright

OUT = sys.argv[1] if len(sys.argv) > 1 else "schedule-notify-modal.png"
URL = "http://127.0.0.1:8624/schedule"

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1100, "height": 900})
    page.goto(URL, wait_until="networkidle")
    page.wait_for_timeout(1500)

    # Open the create modal.
    page.click("text=New cron job")
    page.wait_for_selector("text=Notify when run completes")

    # Name it so the modal looks realistic.
    page.fill("input[placeholder='e.g. Tonight - drain work list']", "Nightly drain (notify on)")

    # Find the notify select by its label's containing .fld and choose "Always" so the webhook
    # field reveals beneath it.
    notify_fld = page.locator(".fld", has=page.get_by_text("Notify when run completes"))
    notify_fld.locator("select").select_option("always")
    page.wait_for_selector("text=Webhook URL (optional)")
    page.fill("input[placeholder='https://example.com/hook']", "https://example.com/hook")
    page.wait_for_timeout(400)

    page.screenshot(path=OUT, full_page=True)
    print(f"saved {OUT}")
    browser.close()
