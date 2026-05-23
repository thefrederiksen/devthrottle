"""linkedin-connect-from-queue.py - send approved LinkedIn connection requests.

Drives the LinkedIn UI via browser-harness against the cc-director linkedin
Brave profile (CDP on 127.0.0.1:9224). The Brave instance must already be
running - launch it first with:

    cc-playwright --connection linkedin start --url https://www.linkedin.com/feed/

Then run this script. It pulls every queue item where:
    status='approved', platform='linkedin', type='message',
    tags LIKE '%connection-request%'
and runs the full flow on each (resolve URL, open Connect dialog, fill note,
JS-click Send, verify on the Sent Invitations page, mark posted).

Usage:
    python linkedin-connect-from-queue.py            # process all approved
    python linkedin-connect-from-queue.py <id-prefix>  # just one item

Why JS .click() and not click_at_xy or cc-playwright click:
    - LinkedIn's Connect button accepts JS .click() (closes dialog, fires submit).
    - click_at_xy in browser-harness uses screenshot pixel coordinates; CSS
      coordinates from getBoundingClientRect do NOT align (verified
      2026-05-22 - clicked at correct CSS coords, button did not respond).
    - cc-playwright .click() is also trusted but adds a second tool layer
      we do not need.

Why the React-native setter for the textarea:
    - Plain `ta.value = "..."` does not fire React's internal state update,
      so the Send button stays disabled and the note is empty on submit.
    - HTMLTextAreaElement.prototype.value setter + bubbled 'input' event is
      the documented browser-harness pattern (see Gmail recipe in
      D:\\BrowserProfiles\\BROWSER-HARNESS-INSTRUCTIONS.md).
"""
import argparse
import json
import os
import sqlite3
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

DB_PATH = Path(os.environ["LOCALAPPDATA"]) / "cc-director" / "config" / "comm-queue" / "communications.db"
CDP_URL = "http://127.0.0.1:9224"
SENT_PAGE = "https://www.linkedin.com/mynetwork/invitation-manager/sent/"


def get_items(id_prefix=None):
    con = sqlite3.connect(DB_PATH)
    con.row_factory = sqlite3.Row
    rows = con.execute(
        """SELECT id, content, recipient, tags
           FROM communications
           WHERE status = 'approved'
             AND platform = 'linkedin'
             AND type = 'message'
             AND tags LIKE '%connection-request%'"""
    ).fetchall()
    items = [dict(r) for r in rows]
    if id_prefix:
        items = [i for i in items if i["id"].startswith(id_prefix)]
    return items


def ensure_cdp():
    try:
        urllib.request.urlopen(f"{CDP_URL}/json/version", timeout=3).read()
        return True
    except Exception as e:
        print(f"ERROR: CDP endpoint {CDP_URL} not reachable: {e}")
        print("Start the linkedin Brave first:")
        print("    cc-playwright --connection linkedin start --url https://www.linkedin.com/feed/")
        return False


def resolve_profile_url(profile_url, name, bh):
    """If profile_url is a /in/ link, return it. Otherwise navigate to the
    search URL and resolve the first /in/ result."""
    if "/in/" in profile_url:
        return profile_url
    bh.goto_url(profile_url)
    bh.wait_for_load(timeout=15)
    bh.wait(3)
    href = bh.js("""(() => {
        const link = document.querySelector('main a[href*="/in/"]');
        return link?.href || null;
    })()""")
    if not href:
        raise RuntimeError(f"could not resolve profile URL from search for {name}")
    return href.split("?")[0]


def open_connect_dialog(name, bh):
    """Click Connect button. Tries direct button on profile first, falls back
    to the More-actions dropdown when LinkedIn shows Follow as primary action.
    The aria-label pattern is "Invite <full-name-with-credentials> to connect"
    so we match by prefix."""
    aria_prefix = f"Invite {name}"
    result = bh.js(
        f"""(() => {{
            const btns = Array.from(document.querySelectorAll('main button')).filter(b => b.offsetParent !== null);
            const direct = btns.find(b => (b.getAttribute('aria-label')||'').startsWith({json.dumps(aria_prefix)}));
            if (direct) {{ direct.click(); return {{ method: 'direct' }}; }}
            const more = document.querySelector('main button[aria-label="More actions"]');
            if (!more) return {{ error: 'no Connect button and no More menu' }};
            more.click();
            return {{ method: 'more_clicked' }};
        }})()"""
    )
    if result.get("error"):
        raise RuntimeError(result["error"])
    bh.wait(1.2)
    if result["method"] == "more_clicked":
        # Click the Connect entry inside the dropdown (it is a DIV, not BUTTON).
        followup = bh.js(
            f"""(() => {{
                const el = Array.from(document.querySelectorAll('div[role=button], button, [aria-label]'))
                    .find(e => (e.getAttribute('aria-label')||'').startsWith({json.dumps(aria_prefix)}));
                if (!el) return {{ error: 'Connect option not in dropdown' }};
                el.click();
                return {{ clicked: true }};
            }})()"""
        )
        if followup.get("error"):
            raise RuntimeError(followup["error"])
        bh.wait(1.5)
    state = bh.js("""(() => {
        const d = document.querySelector('div[role=dialog]');
        return d ? { heading: d.querySelector('h2')?.innerText?.trim() } : { error: 'dialog did not open' };
    })()""")
    if state.get("error"):
        raise RuntimeError(state["error"])


def fill_and_send(note, bh):
    r = bh.js("""(() => {
        const d = document.querySelector('div[role=dialog]');
        const btn = Array.from(d.querySelectorAll('button')).find(b => b.getAttribute('aria-label') === 'Add a note');
        if (!btn) return { error: 'Add a note button missing' };
        btn.click();
        return { clicked: true };
    })()""")
    if r.get("error"):
        raise RuntimeError(r["error"])
    bh.wait(1.0)

    r = bh.js(
        f"""(() => {{
            const ta = document.querySelector('textarea#custom-message');
            if (!ta) return {{ error: 'textarea#custom-message missing' }};
            ta.focus();
            const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
            setter.call(ta, {json.dumps(note)});
            ta.dispatchEvent(new Event('input', {{ bubbles: true }}));
            return {{ value_len: ta.value.length }};
        }})()"""
    )
    if r.get("error"):
        raise RuntimeError(r["error"])
    if r["value_len"] != len(note):
        raise RuntimeError(f"textarea length mismatch: got {r['value_len']}, expected {len(note)}")
    bh.wait(0.4)

    r = bh.js("""(() => {
        const d = document.querySelector('div[role=dialog]');
        const send = Array.from(d.querySelectorAll('button')).find(b => /Send invitation/i.test(b.getAttribute('aria-label')||''));
        if (!send) return { error: 'Send button missing' };
        if (send.disabled) return { error: 'Send button disabled' };
        send.click();
        return { clicked: true };
    })()""")
    if r.get("error"):
        raise RuntimeError(r["error"])
    bh.wait(3)

    return bh.js("""(() => ({
        dialog_open: !!document.querySelector('div[role=dialog]'),
        toast: document.querySelector('[role=alert], .artdeco-toast-item')?.innerText?.trim() || null
    }))()""")


def verify_on_sent_page(name, bh):
    bh.goto_url(SENT_PAGE)
    bh.wait_for_load(timeout=15)
    bh.wait(2)
    return bool(bh.js(
        f"""(() => (document.querySelector('main')?.innerText || '').includes({json.dumps(name)}))()"""
    ))


def mark_posted(item_id):
    subprocess.run(
        ["cc-comm-queue", "mark-posted", item_id, "--by", "linkedin-connect-from-queue"],
        check=False,
    )


def process_one(item, bh):
    rid = item["id"]
    recipient = json.loads(item["recipient"])
    name = recipient["name"]
    profile_url = recipient["profile_url"]
    note = item["content"]
    print(f"--- {name} ({rid[:8]}) ---")

    resolved = resolve_profile_url(profile_url, name, bh)
    print(f"  profile: {resolved}")
    bh.goto_url(resolved)
    bh.wait_for_load(timeout=15)
    bh.wait(2)

    open_connect_dialog(name, bh)
    post = fill_and_send(note, bh)
    print(f"  post-send: dialog_open={post['dialog_open']}, toast={post['toast']!r}")
    if post["dialog_open"]:
        raise RuntimeError("dialog did not close after Send click")

    if not verify_on_sent_page(name, bh):
        raise RuntimeError(f"{name} not found on Sent Invitations page")
    print(f"  VERIFIED on sent invitations page")
    mark_posted(rid)
    print(f"  marked posted: {rid[:8]}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("id", nargs="?", help="Specific queue ID prefix (default: all approved connection-requests)")
    args = parser.parse_args()

    os.environ.setdefault("BU_CDP_URL", CDP_URL)
    os.environ.setdefault("BU_NAME", "linkedin")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")

    if not ensure_cdp():
        return 1

    items = get_items(args.id)
    if not items:
        print("No approved connection-request items in queue.")
        return 0
    print(f"Processing {len(items)} item(s).")

    import browser_harness.helpers as bh
    bh.ensure_daemon()

    ok, fail = [], []
    for item in items:
        try:
            process_one(item, bh)
            ok.append(item["id"])
        except Exception as e:
            name = json.loads(item["recipient"]).get("name", "?")
            print(f"  FAIL: {e}")
            fail.append((item["id"], name, str(e)))
            # Try to dismiss any leftover dialog so the next item starts clean.
            try:
                bh.js("(() => { document.querySelectorAll('div[role=dialog] button[aria-label=\"Dismiss\"]').forEach(b => b.click()); })()")
            except Exception:
                pass

    print(f"\n=== {len(ok)} sent, {len(fail)} failed ===")
    for rid, name, reason in fail:
        print(f"  FAIL {rid[:8]} {name}: {reason}")
    return 0 if not fail else 2


if __name__ == "__main__":
    sys.exit(main())
