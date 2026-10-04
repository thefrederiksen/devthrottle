"""Take the Mentor page screenshots for devthrottle_internal#2305 from a real render against the contract's data.

Run from the repository root:  python docs/proof/teams-2305/harness/take-screenshots.py

It copies the harness next to the Cockpit's source, starts the Cockpit's own Vite dev server for the length of the
run, opens the page as a Manager and as the Developer in a headless Chromium (Playwright, used as a library: this is a
repeatable proof script, not interactive work), saves the two screenshots and the rendered text, and removes the
harness files again whatever happens.
"""
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[4]
HARNESS = Path(__file__).resolve().parent
OUT = HARNESS.parent
COCKPIT = ROOT / "apps" / "cockpit"
PORT = 5291
COPIES = [
    (HARNESS / "proofMentor.tsx", COCKPIT / "src" / "proofMentor.tsx"),
    (HARNESS / "proof-mentor.html", COCKPIT / "proof-mentor.html"),
]


def wait_for_server(url: str, seconds: int) -> None:
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            urllib.request.urlopen(url, timeout=2)
            return
        except OSError:
            time.sleep(0.5)
    raise RuntimeError(f"The Vite dev server did not answer at {url} within {seconds} seconds.")


def main() -> int:
    for source, target in COPIES:
        if target.exists():
            raise RuntimeError(f"{target} already exists; refusing to overwrite it.")
        shutil.copyfile(source, target)
    server = subprocess.Popen(
        ["npx", "vite", "--port", str(PORT), "--strictPort"], cwd=COCKPIT, shell=True,
    )
    try:
        base = f"http://localhost:{PORT}/proof-mentor.html"
        wait_for_server(base, 90)
        with sync_playwright() as p:
            browser = p.chromium.launch()
            for who in ("manager", "developer"):
                page = browser.new_page(viewport={"width": 1280, "height": 900})
                page.goto(f"{base}?as={who}")
                page.wait_for_selector("[data-testid=mentor-block]", timeout=30000)
                page.wait_for_selector("text=Mentor >> nth=0")
                page.screenshot(path=str(OUT / f"mentor-{who}.png"), full_page=True)
                text = page.inner_text("main")
                rail = page.inner_text("nav.rail")
                (OUT / f"mentor-{who}.txt").write_text(f"--- rail ---\n{rail}\n\n--- page ---\n{text}\n", encoding="utf-8")
                print(f"[OK] mentor-{who}.png")
                page.close()
            browser.close()
    finally:
        subprocess.run(["taskkill", "/PID", str(server.pid), "/T", "/F"], capture_output=True)
        for _, target in COPIES:
            target.unlink(missing_ok=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
