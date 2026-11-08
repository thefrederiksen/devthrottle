"""Measure the phone fixes of Web UI Polish phase 3 at 390 by 844 in a real browser, and screenshot each page.

Run from the repository root after build.mjs:
    python scripts/qa/web-ui-polish-p3/measure.py <screenshot folder>

Each check is a PRESENCE that can fail: a number or a word read off the laid-out page. A check that finds no element
fails, so an empty page can never pass. Exits 1 when any check fails.
"""

import json
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[3]
PAGE = (ROOT / "artifacts" / "web-ui-polish-p3" / "phone.html").as_uri()
WIDTH, HEIGHT = 390, 844

results = []


def check(name, ok, detail):
    results.append({"check": name, "ok": bool(ok), "detail": detail})
    print(("PASS " if ok else "FAIL ") + name + " - " + json.dumps(detail))


def open_page(browser, hash_name, ready_selector):
    context = browser.new_context(viewport={"width": WIDTH, "height": HEIGHT}, has_touch=True, is_mobile=True,
                                  device_scale_factor=2, color_scheme="dark")
    page = context.new_page()
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.goto(PAGE + "#" + hash_name)
    page.wait_for_selector(ready_selector, timeout=15000)
    page.wait_for_timeout(700)
    check(hash_name + ": no page errors", not errors, errors)
    return page


BACK = """() => {
    const bar = document.querySelector('header.app-bar');
    const back = bar ? bar.querySelector('.back-link') : null;
    if (!back) return null;
    const r = back.getBoundingClientRect();
    const cs = getComputedStyle(back);
    return { text: back.textContent.trim(), tag: back.tagName, height: Math.round(r.height), width: Math.round(r.width),
             left: Math.round(r.left), radius: cs.borderTopLeftRadius, fontSize: cs.fontSize, fontWeight: cs.fontWeight,
             others: [...bar.querySelectorAll('.file-view-back, svg')].filter(e => e.closest('.back-link') === null && e.closest('button, a') && e.closest('button, a').getBoundingClientRect().left < 80).length };
}"""


def back_button(browser, out, hash_name, ready, shot):
    page = open_page(browser, hash_name, ready)
    b = page.evaluate(BACK)
    check(hash_name + ": the app bar has the shared Back", b is not None, b)
    page.screenshot(path=str(out / shot))
    page.close()
    return b


def settings(browser, out):
    page = open_page(browser, "settings", ".settings-tabs .settings-tab")
    m = page.evaluate(
        """() => {
        const tabs = [...document.querySelectorAll('.settings-tabs .settings-tab')].map(t => {
            const r = t.getBoundingClientRect();
            return { text: t.textContent.trim(), left: Math.round(r.left), right: Math.round(r.right), top: Math.round(r.top),
                     truncated: t.scrollWidth > t.clientWidth + 1 };
        });
        const strip = document.querySelector('.settings-tabs');
        return { tabs, viewport: window.innerWidth, stripScroll: strip.scrollWidth - strip.clientWidth,
                 docScroll: document.documentElement.scrollWidth - window.innerWidth };
    }"""
    )
    tabs = m["tabs"]
    check("10: four tabs", [t["text"] for t in tabs] == ["Notifications", "Language", "Transcription", "Fleet Manager"],
          [t["text"] for t in tabs])
    check("10: every tab lies inside the screen", all(t["left"] >= 0 and t["right"] <= m["viewport"] for t in tabs), tabs)
    check("10: no tab label is cut off", not any(t["truncated"] for t in tabs), tabs)
    check("10: two rows of two", len({t["top"] for t in tabs}) == 2 and len(tabs) == 4
          and tabs[0]["top"] == tabs[1]["top"] and tabs[2]["top"] == tabs[3]["top"], [t["top"] for t in tabs])
    check("10: nothing scrolls sideways", m["stripScroll"] <= 0 and m["docScroll"] <= 0,
          {"strip": m["stripScroll"], "page": m["docScroll"]})
    page.screenshot(path=str(out / "p3-10-settings-tabs.png"))
    # The Fleet Manager tab, picked: it was the one cut off at "Fle...".
    page.get_by_role("tab", name="Fleet Manager").click()
    page.wait_for_timeout(400)
    selected = page.evaluate("() => document.querySelector('.settings-tab.active').textContent.trim()")
    check("10: the Fleet Manager tab can be picked", selected == "Fleet Manager", selected)
    page.screenshot(path=str(out / "p3-10-settings-fleet-manager.png"))
    page.close()


def about(browser, out):
    page = open_page(browser, "about", ".about-list")
    page.wait_for_function("() => [...document.querySelectorAll('.about-row')].some(r => r.textContent.includes('Directors') && !r.textContent.includes('Loading'))", timeout=10000)
    rows = page.evaluate(
        "() => Object.fromEntries([...document.querySelectorAll('.about-row')].map(r => [r.querySelector('.about-label').textContent, r.querySelector('.about-value').textContent]))"
    )
    check("13: Directors shows the number", rows.get("Directors") == "3", rows)
    check("13: Sessions shows the number", rows.get("Sessions") == "17", rows)
    check("13: nothing says Not reported", "Not reported" not in rows.values(), rows)
    b = page.evaluate(BACK)
    page.screenshot(path=str(out / "p3-13-about.png"))
    page.close()
    return b


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "artifacts" / "web-ui-polish-p3" / "shots"
    out.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        settings(browser, out)
        backs = {
            "about": about(browser, out),
            "settings": back_button(browser, out, "settings", ".settings-tab", "p3-11-back-settings.png"),
            "alerts": back_button(browser, out, "alerts", "header.app-bar .back-link", "p3-11-back-alerts.png"),
            "new": back_button(browser, out, "new", "header.app-bar .back-link", "p3-11-back-new-session.png"),
            "file": back_button(browser, out, "file", "header.app-bar .back-link", "p3-11-back-file.png"),
            "report": back_button(browser, out, "report", "header.app-bar .back-link", "p3-11-back-report.png"),
            "session": back_button(browser, out, "session", "header.app-bar .back-link", "p3-11-back-session.png"),
        }
        browser.close()
    found = {k: v for k, v in backs.items() if v is not None}
    check("11: every page found its Back", len(found) == len(backs), list(found))
    check("11: every Back says Back", all(v["text"] == "Back" for v in found.values()), {k: v["text"] for k, v in found.items()})
    shape = {k: (v["height"], v["radius"], v["fontSize"], v["fontWeight"]) for k, v in found.items()}
    check("11: every Back is the same size and shape", len(set(shape.values())) == 1, shape)
    check("11: no second arrow or file-viewer button beside it", all(v["others"] == 0 for v in found.values()),
          {k: v["others"] for k, v in found.items()})
    failed = [r for r in results if not r["ok"]]
    (out / "p3-measure.json").write_text(json.dumps(results, indent=2))
    print(f"{len(results) - len(failed)} of {len(results)} checks passed; screenshots in {out}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
