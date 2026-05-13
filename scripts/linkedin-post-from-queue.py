"""Post a queued cc-comm-queue item to LinkedIn (text + image) via Playwright.

Connects over CDP to a Brave instance launched by `cc-playwright start`.
Reads the post body and media BLOB from the cc-comm-queue SQLite database,
then drives LinkedIn's web composer end-to-end.

Why this exists
---------------
LinkedIn's composer is a Quill rich-text editor mounted inside an open
Shadow DOM. The "Add media" button opens an inline media-editor modal whose
"Upload from computer" button triggers an OS file dialog -- which Playwright
cannot dismiss programmatically and which does not need to be opened at all
because the modal also exposes a hidden <input type="file"> we can drive
directly with set_input_files. This script avoids the OS dialog by skipping
the visible "Upload from computer" button and writing straight to the input.

Prerequisites
-------------
1. cc-browser's "linkedin" connection must be CLOSED (Chrome locks the dir).
2. cc-playwright must be running on the linkedin connection:

       cc-playwright --connection linkedin stop  # if any stale state
       cc-playwright --connection linkedin start --url https://www.linkedin.com/feed/

3. The cc-comm-queue item must be status=approved.

Usage
-----
    python scripts/linkedin-post-from-queue.py <queue_id_prefix>
    # e.g. python scripts/linkedin-post-from-queue.py 78965f75

The script does NOT call mark-posted. The caller decides when to mark the
queue item as posted (after visually verifying the post landed).
"""
from __future__ import annotations

import json
import os
import sqlite3
import sys
import tempfile
import time
from pathlib import Path

LOCALAPPDATA = Path(os.environ["LOCALAPPDATA"])
QUEUE_DB = LOCALAPPDATA / "cc-director" / "config" / "comm-queue" / "communications.db"
PLAYWRIGHT_STATE = LOCALAPPDATA / "cc-playwright" / "state" / "linkedin.json"


def log(msg: str) -> None:
    ts = time.strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def fetch_queue_item(prefix: str) -> dict:
    """Return {'id', 'content', 'notes', 'media': [{'filename','data','mime'}, ...]}"""
    con = sqlite3.connect(str(QUEUE_DB))
    cur = con.cursor()
    cur.execute(
        "SELECT id, content, notes FROM communications WHERE id LIKE ? || '%'",
        (prefix,),
    )
    row = cur.fetchone()
    if not row:
        raise SystemExit(f"No queue item starting with '{prefix}'")
    qid, content, notes = row
    cur.execute(
        "SELECT filename, data, mime_type FROM media WHERE communication_id = ?",
        (qid,),
    )
    media = [
        {"filename": r[0], "data": r[1], "mime": r[2]} for r in cur.fetchall()
    ]
    con.close()
    return {"id": qid, "content": content, "notes": notes, "media": media}


def write_media_to_temp(media: list[dict]) -> list[str]:
    """Write media BLOBs to %TEMP% and return absolute paths."""
    paths = []
    tmp = Path(tempfile.gettempdir())
    for m in media:
        out = tmp / m["filename"]
        out.write_bytes(m["data"])
        paths.append(str(out))
        log(f"  media -> {out} ({len(m['data'])} bytes)")
    return paths


def cdp_endpoint() -> str:
    """Return CDP HTTP endpoint from cc-playwright's state file."""
    if not PLAYWRIGHT_STATE.exists():
        raise SystemExit(
            "cc-playwright linkedin state not found. Start it first:\n"
            "  cc-playwright --connection linkedin start --url https://www.linkedin.com/feed/"
        )
    state = json.loads(PLAYWRIGHT_STATE.read_text(encoding="utf-8"))
    port = state.get("port")
    if not port:
        raise SystemExit("No port in cc-playwright linkedin state")
    return f"http://localhost:{port}"


def find_linkedin_page(ctx):
    """Pick the page on linkedin.com (last one if multiple)."""
    matches = [p for p in ctx.pages if "linkedin.com" in (p.url or "")]
    if not matches:
        raise SystemExit("No linkedin.com tab found in this Brave instance")
    return matches[-1]


# JavaScript helpers ------------------------------------------------------

JS_QUERY_SHADOW = """
(sel) => {
    const interop = document.querySelector('[data-testid="interop-shadowdom"]');
    const sr = interop?.shadowRoot;
    return sr ? !!sr.querySelector(sel) : false;
}
"""

JS_CLICK_SHADOW = """
(sel) => {
    const interop = document.querySelector('[data-testid="interop-shadowdom"]');
    const sr = interop?.shadowRoot;
    const el = sr?.querySelector(sel);
    if (!el) return { ok: false, reason: 'not found' };
    el.click();
    return { ok: true };
}
"""

JS_CLICK_BY_TEXT_SHADOW = """
({ tag, text }) => {
    const interop = document.querySelector('[data-testid="interop-shadowdom"]');
    const sr = interop?.shadowRoot;
    if (!sr) return { ok: false, reason: 'no shadow root' };
    const el = Array.from(sr.querySelectorAll(tag))
        .find(e => (e.textContent || '').trim() === text);
    if (!el) return { ok: false, reason: 'not found' };
    el.click();
    return { ok: true };
}
"""

JS_COMPOSER_STATE = """
() => {
    const interop = document.querySelector('[data-testid="interop-shadowdom"]');
    const sr = interop?.shadowRoot;
    if (!sr) return { ready: false, reason: 'no shadow root' };
    const editor = sr.querySelector('.ql-editor');
    const post = Array.from(sr.querySelectorAll('button')).find(b => b.textContent.trim() === 'Post');
    const imgs = Array.from(sr.querySelectorAll('img')).filter(i => {
        const r = i.getBoundingClientRect();
        return r.width > 100 && r.height > 100 && i.src.startsWith('data:');
    });
    return {
        editorPresent: !!editor,
        textLen: editor ? editor.innerText.length : 0,
        textStart: editor ? editor.innerText.slice(0, 80) : '',
        imageAttached: imgs.length > 0,
        imageCount: imgs.length,
        postEnabled: post ? !post.disabled && post.getAttribute('aria-disabled') !== 'true' : false,
    };
}
"""


def post_to_linkedin(item: dict, screenshot_dir: Path) -> dict:
    from playwright.sync_api import sync_playwright

    media_paths = write_media_to_temp(item["media"])
    text = item["content"]

    pw = sync_playwright().start()
    try:
        log(f"Connecting to CDP at {cdp_endpoint()} ...")
        browser = pw.chromium.connect_over_cdp(cdp_endpoint())
        ctx = browser.contexts[0]
        page = find_linkedin_page(ctx)
        log(f"Using tab: {page.url}")

        # 1. Make sure we are on the feed (composer trigger lives there)
        if "/feed" not in page.url:
            log("Navigating to /feed/ ...")
            page.goto("https://www.linkedin.com/feed/", wait_until="domcontentloaded")
            page.wait_for_timeout(2000)

        # 2. Click "Start a post" trigger (this lives in the LIGHT DOM)
        log("Opening composer ...")
        page.locator('div[role=button]:has-text("Start a post")').first.click()
        page.wait_for_timeout(1500)

        # 3. Wait for the Quill editor to mount inside the shadow root
        log("Waiting for Quill editor to mount ...")
        deadline = time.time() + 15
        while time.time() < deadline:
            if page.evaluate(JS_QUERY_SHADOW, ".ql-editor"):
                break
            time.sleep(0.3)
        else:
            raise SystemExit("Quill editor never mounted")

        # 4. Type the post body. Playwright's CSS engine pierces open shadow DOMs.
        log(f"Typing {len(text)} chars into Quill ...")
        editor = page.locator(".ql-editor").first
        editor.press_sequentially(text, delay=25, timeout=180_000)
        page.wait_for_timeout(800)

        # 5. Click "Add media" inside the shadow root via JS (bypasses the
        #    interop-outlet click intercept)
        if media_paths:
            log("Opening media editor ...")
            r = page.evaluate(JS_CLICK_SHADOW, 'button[aria-label="Add media"]')
            if not r.get("ok"):
                raise SystemExit(f"Add media click failed: {r}")
            page.wait_for_timeout(1500)

            # 6. Wait for the hidden <input type="file"> to mount
            log("Waiting for file input ...")
            deadline = time.time() + 10
            while time.time() < deadline:
                if page.evaluate(JS_QUERY_SHADOW, "#media-editor-file-selector__file-input"):
                    break
                time.sleep(0.3)
            else:
                raise SystemExit("File input never mounted")

            # 7. set_input_files DIRECTLY on the hidden input. Do NOT click
            #    "Upload from computer" -- that opens an OS dialog Playwright
            #    cannot dismiss.
            log("Attaching file via hidden input ...")
            page.locator("#media-editor-file-selector__file-input").set_input_files(media_paths)
            page.wait_for_timeout(2500)

            # 8. Click "Next" to return to the composer with image attached
            log("Clicking Next ...")
            r = page.evaluate(JS_CLICK_BY_TEXT_SHADOW, {"tag": "button", "text": "Next"})
            if not r.get("ok"):
                raise SystemExit(f"Next click failed: {r}")
            page.wait_for_timeout(2500)

        # 9. Verify final state before posting
        state = page.evaluate(JS_COMPOSER_STATE)
        log(f"Composer state: {state}")
        if not state["editorPresent"]:
            raise SystemExit("Editor not present at final check")
        if state["textLen"] < len(text) // 2:
            raise SystemExit(f"Text too short: got {state['textLen']} chars, expected ~{len(text)}")
        if media_paths and not state["imageAttached"]:
            raise SystemExit("Image not attached at final check")
        if not state["postEnabled"]:
            raise SystemExit("Post button is not enabled")

        # 10. Pre-submit screenshot
        screenshot_dir.mkdir(parents=True, exist_ok=True)
        pre = screenshot_dir / f"linkedin-pre-submit-{int(time.time())}.png"
        page.screenshot(path=str(pre))
        log(f"Pre-submit screenshot: {pre}")

        # 11. Submit
        log("Clicking Post ...")
        r = page.evaluate(JS_CLICK_BY_TEXT_SHADOW, {"tag": "button", "text": "Post"})
        if not r.get("ok"):
            raise SystemExit(f"Post click failed: {r}")
        page.wait_for_timeout(5000)

        # 12. Post-submit verification: composer should be gone
        post_submit_state = page.evaluate(JS_COMPOSER_STATE)
        log(f"Post-submit state: {post_submit_state}")

        # 13. Screenshot whatever's on screen now (feed with the new post on top)
        post_shot = screenshot_dir / f"linkedin-post-submitted-{int(time.time())}.png"
        page.screenshot(path=str(post_shot))
        log(f"Post-submit screenshot: {post_shot}")

        return {
            "ok": True,
            "queue_id": item["id"],
            "pre_screenshot": str(pre),
            "post_screenshot": str(post_shot),
            "final_url": page.url,
        }
    finally:
        pw.stop()


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python linkedin-post-from-queue.py <queue_id_prefix>", file=sys.stderr)
        sys.exit(2)
    prefix = sys.argv[1]
    screenshot_dir = Path(
        os.environ.get(
            "CC_LINKEDIN_SHOTS",
            r"D:\Personal\OneDrive\Pictures\Screenshots",
        )
    )
    log(f"Fetching queue item '{prefix}' ...")
    item = fetch_queue_item(prefix)
    log(f"  id: {item['id']}")
    log(f"  content: {len(item['content'])} chars")
    log(f"  media: {len(item['media'])} items")
    result = post_to_linkedin(item, screenshot_dir)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
