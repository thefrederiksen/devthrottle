"""Measure the four Cockpit fixes of Web UI Polish phase 2 at 1600 by 900 in a real browser, and screenshot each.

Run from the repository root after build.mjs:
    python scripts/qa/web-ui-polish-p2/measure.py <screenshot folder>

Each check is a PRESENCE that can fail: a number read off the laid-out page, compared with a limit. A check that finds
no element fails, so an empty page can never pass. Exits 1 when any check fails.
"""

import json
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[3]
PAGE = (ROOT / "artifacts" / "web-ui-polish-p2" / "cockpit.html").as_uri()

results = []


def check(name, ok, detail):
    results.append({"check": name, "ok": bool(ok), "detail": detail})
    print(("PASS " if ok else "FAIL ") + name + " - " + json.dumps(detail))


def open_page(browser, hash_name, ready_selector):
    page = browser.new_page(viewport={"width": 1600, "height": 900})
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))
    page.goto(PAGE + "#" + hash_name)
    page.wait_for_selector(ready_selector, timeout=15000)
    page.wait_for_timeout(600)
    if errors:
        check(hash_name + ": no page errors", False, errors)
    return page


def session(browser, out):
    page = open_page(browser, "session", ".chat-filter .session-mission")
    m = page.evaluate(
        """() => {
        const tabs = [...document.querySelectorAll('.session-tabs .session-tab')];
        const strip = document.querySelector('.session-tabs').getBoundingClientRect();
        const lineHeight = (el) => parseFloat(getComputedStyle(el).lineHeight) || 16;
        const rows = tabs.map(t => ({ text: t.textContent.trim(), height: t.getBoundingClientRect().height,
            lines: Math.round((t.scrollHeight - parseFloat(getComputedStyle(t).paddingTop) - parseFloat(getComputedStyle(t).paddingBottom)) / lineHeight(t)) }));
        const mission = document.querySelector('.chat-filter .session-mission').getBoundingClientRect();
        const chip = document.querySelector('.chat-filter .session-model').getBoundingClientRect();
        const filter = document.querySelector('.chat-filter').getBoundingClientRect();
        const density = document.querySelector('.chat-density').getBoundingClientRect();
        const live = document.querySelector('.chat-live').getBoundingClientRect();
        const label = document.querySelector('.chat-filter .mb-btn');
        const dock = document.querySelector('.dock-tabs');
        const dockLeft = dock ? dock.getBoundingClientRect().left : null;
        return { rows, stripTop: strip.top, stripHeight: strip.height, missionRight: mission.right, missionTop: mission.top,
                 chipTop: chip.top, chipMid: (chip.top + chip.bottom) / 2, missionMid: (mission.top + mission.bottom) / 2,
                 densityMid: (density.top + density.bottom) / 2, liveRight: live.right, filterRight: filter.right,
                 missionTitle: label ? label.title : null, filterTop: filter.top, filterBottom: filter.bottom, dockLeft,
                 missionInTabs: !!document.querySelector('.session-tabs .session-mission') };
    }"""
    )
    check("6: six tabs on the top row", len(m["rows"]) == 6, [r["text"] for r in m["rows"]])
    check("6: every tab is one line", all(r["lines"] == 1 for r in m["rows"]), m["rows"])
    check("6: Mission button is not in the tab strip", not m["missionInTabs"], {})
    check(
        "6: chip and Mission button sit ON the filter row",
        m["filterTop"] <= m["missionTop"] < m["filterBottom"] and m["filterTop"] <= m["chipTop"] < m["filterBottom"],
        {k: m[k] for k in ("filterTop", "filterBottom", "missionTop", "chipTop")},
    )
    check(
        "6: the filter row is ONE line: chip and Mission button level with the detail switch",
        abs(m["chipMid"] - m["densityMid"]) <= 3 and abs(m["missionMid"] - m["densityMid"]) <= 3 and m["filterBottom"] - m["filterTop"] <= 48,
        {k: m[k] for k in ("densityMid", "chipMid", "missionMid", "filterTop", "filterBottom")},
    )
    check("6: LIVE stays inside the filter row", m["liveRight"] <= m["filterRight"] + 1, {"liveRight": m["liveRight"], "filterRight": m["filterRight"]})
    check(
        "6: Mission button ends left of the Queue panel",
        m["dockLeft"] is not None and m["missionRight"] <= m["dockLeft"],
        {"missionRight": m["missionRight"], "dockLeft": m["dockLeft"]},
    )
    page.screenshot(path=str(out / "p2-06-session-chat.png"))
    page.close()

    page = open_page(browser, "session-terminal", ".session-subbar .session-mission")
    m = page.evaluate(
        """() => {
        const dock = document.querySelector('.dock-tabs').getBoundingClientRect();
        const mission = document.querySelector('.session-subbar .session-mission').getBoundingClientRect();
        const tabs = [...document.querySelectorAll('.session-tabs .session-tab')].map(t => t.getBoundingClientRect().height);
        return { missionRight: mission.right, dockLeft: dock.left, tabHeights: tabs };
    }"""
    )
    check(
        "6: on Terminal the pair sits on the slim row, left of the Queue panel",
        m["missionRight"] <= m["dockLeft"] and len(set(round(h) for h in m["tabHeights"])) == 1,
        m,
    )
    page.screenshot(path=str(out / "p2-06-session-terminal.png"))
    page.close()


def schedule(browser, out):
    page = open_page(browser, "schedule", ".sched-rowmenu-btn")
    m = page.evaluate(
        """() => {
        const scroller = document.querySelector('.ui-table-scroll, .dtbl-scroll, .ui-table-wrap') || document.querySelector('table').parentElement;
        const table = document.querySelector('table');
        const heads = [...table.querySelectorAll('thead th')].map(th => th.textContent.trim()).filter(Boolean);
        const main = document.querySelector('main') || document.body;
        const lastHead = [...table.querySelectorAll('thead th')].pop().getBoundingClientRect();
        return { heads, scrollWidth: scroller.scrollWidth, clientWidth: scroller.clientWidth,
                 docScroll: document.documentElement.scrollWidth, viewport: window.innerWidth,
                 tableRight: table.getBoundingClientRect().right, lastHeadRight: lastHead.right,
                 rows: table.querySelectorAll('tbody tr .sched-cell-name').length,
                 menus: document.querySelectorAll('.sched-rowmenu-btn').length,
                 runNow: [...document.querySelectorAll('.sched-linkbtn')].filter(b => b.textContent.trim() === 'Run now').length,
                 shiftsUnderTime: [...document.querySelectorAll('.sched-cell-stack .sched-shift')].length,
                 recordsUnderLastRun: [...document.querySelectorAll('.sched-lastrun-cell .sched-runrecord')].length };
    }"""
    )
    check("7: no Shift or Closes itself column", "Shift" not in m["heads"] and "Closes itself" not in m["heads"], m["heads"])
    check(
        "7: the table does not scroll sideways",
        m["scrollWidth"] <= m["clientWidth"] + 1 and m["docScroll"] <= m["viewport"],
        {k: m[k] for k in ("scrollWidth", "clientWidth", "docScroll", "viewport", "tableRight")},
    )
    check("7: last column inside the window", m["lastHeadRight"] <= 1600, {"lastHeadRight": m["lastHeadRight"]})
    n = m["rows"]
    check("7: the Active list has rows", n == 5, {"rows": n})
    check("7: every row has Run now and a ... menu", m["menus"] == n and m["runNow"] == n, {"menus": m["menus"], "runNow": m["runNow"]})
    check("7: Shift folded under the time on every row", m["shiftsUnderTime"] == n, {"count": m["shiftsUnderTime"]})
    check("7: Closes itself folded under Last run", m["recordsUnderLastRun"] == n, {"count": m["recordsUnderLastRun"]})
    page.screenshot(path=str(out / "p2-07-schedule.png"))
    page.locator(".sched-rowmenu-btn").first.click()
    page.wait_for_selector("[role=menu]")
    items = page.locator("[role=menu] [role=menuitem]").all_inner_texts()
    check("7: the ... menu holds Edit and Delete", items == ["Edit", "Delete"], items)
    page.screenshot(path=str(out / "p2-07-schedule-menu-open.png"))
    page.close()


def directors(browser, out):
    page = open_page(browser, "directors", "[data-testid=fm-machine-row]")
    m = page.evaluate(
        """() => {
        const rows = [...document.querySelectorAll('[data-testid=fm-machine-row]')].map(r => ({
            machine: r.dataset.machine, height: r.getBoundingClientRect().height,
            more: !!r.querySelector('[data-testid=fm-status] .clamp-text-more') }));
        const versions = [...document.querySelectorAll('.fm-table .dmono[title]')].map(v => ({ text: v.textContent, title: v.title }));
        const allVersions = [...document.querySelectorAll('.fm-table td .dmono')].map(v => v.textContent).filter(t => /\\d+\\.\\d+/.test(t));
        const scroller = document.querySelector('.dtbl-scroll');
        return { rows, versions, allVersions, scrollWidth: scroller.scrollWidth, clientWidth: scroller.clientWidth };
    }"""
    )
    check("8: three machine rows", len(m["rows"]) == 3, m["rows"])
    check("8: every machine row is at most 110 pixels tall", m["rows"] and all(r["height"] <= 110 for r in m["rows"]), m["rows"])
    check(
        "8: the long statuses carry More",
        [r["more"] for r in m["rows"]] == [True, True, True],
        m["rows"],
    )
    check(
        "8: every version shows short",
        m["allVersions"] and all("+" not in v for v in m["allVersions"]),
        m["allVersions"],
    )
    check(
        "8: a cut version carries the full build on hover",
        len(m["versions"]) >= 1 and all(v["title"].startswith(v["text"] + "+") for v in m["versions"]),
        m["versions"],
    )
    check("8: no sideways scroll", m["scrollWidth"] <= m["clientWidth"] + 1, {"scrollWidth": m["scrollWidth"], "clientWidth": m["clientWidth"]})
    page.screenshot(path=str(out / "p2-08-directors.png"))
    page.locator("[data-testid=fm-status] .clamp-text-more").first.click()
    page.wait_for_timeout(200)
    opened = page.evaluate(
        "() => document.querySelector('[data-testid=fm-machine-row]').getBoundingClientRect().height"
    )
    check("8: More opens the whole status in place", opened > m["rows"][0]["height"], {"before": m["rows"][0]["height"], "after": opened})
    page.screenshot(path=str(out / "p2-08-directors-more-open.png"))
    page.close()


def voice(browser, out):
    page = open_page(browser, "voice", ".ts-card")
    m = page.evaluate(
        """() => {
        const h1 = document.querySelector('h1').getBoundingClientRect();
        const card = document.querySelector('.ts-card').getBoundingClientRect();
        const back = [...document.querySelectorAll('a')].filter(a => /Sessions/.test(a.textContent) && !a.closest('nav, aside, .rail, .nav'));
        return { h1Left: h1.left, cardLeft: card.left, headerInShared: !!document.querySelector('.ui-page-header h1'), backLinks: back.length };
    }"""
    )
    page.goto(PAGE + "#schedule")
    page.wait_for_selector("h1")
    page.wait_for_timeout(400)
    ref = page.evaluate("() => document.querySelector('h1').getBoundingClientRect().left")
    check("9: shared page header", m["headerInShared"], {})
    check("9: heading lines up with the other pages", abs(m["h1Left"] - ref) <= 1, {"voiceH1Left": m["h1Left"], "scheduleH1Left": ref})
    check("9: cards left-aligned under the heading", abs(m["cardLeft"] - m["h1Left"]) <= 1, {"cardLeft": m["cardLeft"], "h1Left": m["h1Left"]})
    check("9: no Sessions back link on the page", m["backLinks"] == 0, {"count": m["backLinks"]})
    page.goto(PAGE + "#voice")
    page.wait_for_selector(".ts-card")
    page.wait_for_timeout(400)
    page.screenshot(path=str(out / "p2-09-voice-recorder.png"))
    page.close()


def main():
    if len(sys.argv) != 2:
        print("usage: measure.py <screenshot folder>")
        return 2
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        for step in (session, schedule, directors, voice):
            step(browser, out)
        browser.close()
    failed = [r for r in results if not r["ok"]]
    (out / "p2-measurements.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(f"{len(results) - len(failed)} of {len(results)} checks passed")
    return 1 if failed or not results else 0


if __name__ == "__main__":
    sys.exit(main())
