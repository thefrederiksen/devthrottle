"""
Voice-unification harness.

Drives the rebuilt session-view Voice tab in a mobile viewport to prove the two
features this branch adds to the HTML client:

  1. The cleaned-up Voice CTA (hero "Tap to talk" + the new secondary
     "What's happening?" button + a concise state line - no wall of hint text).
  2. The "What's happening?" button: one tap fetches a fresh wingman briefing
     (POST /sessions/{sid}/wingman/ask mode=explain - the SAME endpoint the
     Android client now uses) and reads it aloud, rendering it in the latest
     reply slot.

It also exercises the shared voice-mode flag over pure REST (no browser):
POST /voice-mode then GET /sessions, asserting SessionDto.voiceMode flips - the
field the desktop tile and the Android roster read.

Usage:
    python unification_harness.py --base http://127.0.0.1:PORT --sid <sid> --out <dir>
"""
import argparse
import json
import os
import sys
import time
import urllib.request
from datetime import datetime, timezone
from playwright.sync_api import sync_playwright

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def http_json(method, url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=30) as r:
        raw = r.read().decode("utf-8", "replace")
    try:
        return r.status, json.loads(raw)
    except Exception:
        return r.status, raw


def find_voicemode(sessions, sid):
    for s in sessions:
        if s.get("sessionId") == sid:
            return s.get("voiceMode")
    return None


class Rec:
    def __init__(self, out):
        self.out = out
        self.stages = []
        self.network = []

    def stage(self, key, ok, note, shot=None, extra=None):
        r = {"key": key, "ok": bool(ok), "note": note, "screenshot": shot, "at": now_iso()}
        if extra:
            r.update(extra)
        self.stages.append(r)
        print(f"  [{'PASS' if ok else 'FAIL'}] {key}: {note}")

    def shot(self, page, name):
        fn = f"{name}.png"
        page.screenshot(path=os.path.join(self.out, fn), full_page=False)
        return fn


def attach(page, rec):
    interesting = ("/wingman/ask", "/tts", "/voice-mode", "/sessions")

    def on_response(resp):
        if any(seg in resp.url for seg in interesting):
            entry = {"url": resp.url.split("://", 1)[-1], "status": resp.status,
                     "method": resp.request.method, "at": now_iso()}
            if "/wingman/ask" in resp.url:
                try:
                    entry["body"] = resp.json()
                except Exception:
                    pass
            rec.network.append(entry)

    page.on("response", on_response)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", required=True)
    ap.add_argument("--sid", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    results = {"started": now_iso(), "base": args.base, "sid": args.sid, "stages": [], "network": [], "wire": {}}

    # ---- Wire test (pure REST): the voice-mode flag round-trips on SessionDto ----
    wire = {}
    try:
        _, before = http_json("GET", f"{args.base}/sessions")
        wire["voiceMode_before"] = find_voicemode(before, args.sid)
        http_json("POST", f"{args.base}/sessions/{args.sid}/voice-mode", {"enabled": True})
        _, mid = http_json("GET", f"{args.base}/sessions")
        wire["voiceMode_after_enable"] = find_voicemode(mid, args.sid)
        http_json("POST", f"{args.base}/sessions/{args.sid}/voice-mode", {"enabled": False})
        _, after = http_json("GET", f"{args.base}/sessions")
        wire["voiceMode_after_disable"] = find_voicemode(after, args.sid)
        wire["ok"] = (wire["voiceMode_after_enable"] is True and wire["voiceMode_after_disable"] is False)
        print(f"  [WIRE] voiceMode before={wire['voiceMode_before']} "
              f"enable->{wire['voiceMode_after_enable']} disable->{wire['voiceMode_after_disable']}")
    except Exception as e:
        wire["ok"] = False
        wire["error"] = str(e)
        print(f"  [WIRE] FAILED: {e}")
    results["wire"] = wire

    mobile = {"viewport": {"width": 390, "height": 844}, "device_scale_factor": 3,
              "is_mobile": True, "has_touch": True}

    rec = Rec(args.out)
    with sync_playwright() as p:
        b = p.chromium.launch(headless=True, args=[
            "--use-fake-ui-for-media-stream",
            "--use-fake-device-for-media-stream",
            "--autoplay-policy=no-user-gesture-required",
        ])
        ctx = b.new_context(**mobile, ignore_https_errors=True)
        ctx.grant_permissions(["microphone"], origin=args.base)
        page = ctx.new_page()
        attach(page, rec)
        try:
            page.goto(f"{args.base}/sessions/{args.sid}/view", wait_until="domcontentloaded")
            page.wait_for_timeout(1200)
            rec.stage("01_loaded", True, "Session view loaded (mobile viewport)", rec.shot(page, "01_loaded"))

            tab = page.query_selector('.tab[data-view="voice"]')
            tab.click()
            page.wait_for_timeout(1200)
            # The cleaned CTA: hero button + secondary "What's happening?" present,
            # and the old verbose walkie-talkie hint paragraph is gone.
            has_whats = page.query_selector("#voiceWhatsHappeningBtn") is not None
            hint_gone = page.query_selector(".voice-cta .voice-hint") is None
            state = (page.text_content("#voiceState") or "").strip()
            rec.stage("02_voice_tab_clean", has_whats and hint_gone,
                      f"Voice tab: What's-happening button present={has_whats}, "
                      f"verbose hint removed={hint_gone}, state='{state}'",
                      rec.shot(page, "02_voice_tab_clean"),
                      extra={"has_whats_button": has_whats, "hint_removed": hint_gone})

            # Click "What's happening?" -> fresh briefing rendered + spoken.
            page.click("#voiceWhatsHappeningBtn")
            page.wait_for_timeout(800)
            rec.stage("03_whats_happening_tapped", True,
                      "Tapped 'What's happening?'; briefing requested",
                      rec.shot(page, "03_whats_happening_tapped"))

            briefing = None
            try:
                page.wait_for_selector("#voiceLatestReply:not([hidden])", timeout=120000)
                briefing = (page.text_content("#voiceLatestReply") or "").strip()
            except Exception:
                pass
            page.wait_for_timeout(4000)  # let /tts fire
            ask_calls = [n for n in rec.network if "/wingman/ask" in n["url"] and n["method"] == "POST"]
            tts_calls = [n for n in rec.network if "/tts" in n["url"]]
            rec.stage("04_briefing_rendered", bool(briefing),
                      (f"Briefing rendered ({len(briefing)} chars): {briefing[:160]!r}; "
                       f"wingman/ask calls={len(ask_calls)}, tts calls={len(tts_calls)}")
                      if briefing else "No briefing rendered within timeout",
                      rec.shot(page, "04_briefing_rendered"),
                      extra={"briefing": briefing, "ask_calls": len(ask_calls), "tts_calls": len(tts_calls)})
        except Exception as e:
            rec.stage("ERROR", False, f"harness crashed: {e}")
        ctx.close(); b.close()

    results["stages"] = rec.stages
    results["network"] = rec.network
    results["finished"] = now_iso()
    with open(os.path.join(args.out, "results.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print(f"\nDONE. results.json + screenshots in {args.out}")


if __name__ == "__main__":
    main()
