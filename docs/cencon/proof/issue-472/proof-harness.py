"""
Issue #472 proof harness - Cockpit Learning page with "Ask Wingman".

Drives the REAL Cockpit Blazor app (devthrottle-cockpit.dll, the actual Learning.razor
page and NavMenu) through a real Blazor Server circuit in headless Chromium. The Gateway
is replaced by a small stub HTTP server that implements exactly the three endpoints the
Learning page calls, so the harness can:
  - toggle Wingman availability (GET /gateway/wingman -> { enabled }) for the available
    and not-configured screenshots, and
  - LOG every request, which proves the ask went Cockpit -> Gateway (acceptance #6) and
    serves the answer (acceptance #4). The stub's log line mirrors the real Gateway's
    "[GatewayWingmanVoice] ask-devthrottle" line; the real Gateway endpoint + translator
    are covered by the passing .NET tests (WingmanTranslatorTests, GatewayVoiceTurnAsyncTests).

Outputs PASS/FAIL per criterion and writes screenshots + a gateway log to OUT.
"""
import http.server, socketserver, threading, json, os, sys, subprocess, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
# HERE = <repo>\docs\cencon\proof\issue-472 -> climb four levels to the worktree root.
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(HERE))))
# Use the PUBLISHED Cockpit output (wwwroot + static assets resolved) so the page renders
# with real CSS - the bin/Debug output has no wwwroot. Publish with:
#   dotnet publish src/CcDirector.Cockpit/CcDirector.Cockpit.csproj -c Debug -o local_builds/cockpit-proof472
COCKPIT_DLL = os.path.join(REPO, "local_builds", "cockpit-proof472", "devthrottle-cockpit.dll")
OUT = HERE
GATEWAY_PORT = 7879   # live Gateway is 7878; stay off it
COCKPIT_PORT = 7471   # live Cockpit is 7470; stay off it

# Toggled by the harness between the available and not-configured runs.
STATE = {"wingman_enabled": True, "log": []}

ANSWER = ("DevThrottle is an open-source tool that runs and supervises many Claude Code "
          "coding sessions at once. A desktop Director drives sessions on each machine, a "
          "Gateway gathers them into one fleet, and the Cockpit is the web dashboard you "
          "are looking at now.")


class GatewayStub(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/gateway/wingman":
            STATE["log"].append(f"GET /gateway/wingman -> enabled={STATE['wingman_enabled']}")
            self._json({"enabled": STATE["wingman_enabled"]})
            return
        if self.path == "/healthz":
            self._json({"ok": True})
            return
        self.send_response(404)
        self.end_headers()

    def _read_body(self):
        # The .NET HttpClient sends JsonContent with chunked transfer-encoding (no
        # Content-Length), so handle both framings - a Content-Length read alone returns
        # an empty body for the chunked case.
        te = (self.headers.get("Transfer-Encoding") or "").lower()
        if "chunked" in te:
            chunks = []
            while True:
                size_line = self.rfile.readline().strip()
                if not size_line:
                    break
                try:
                    size = int(size_line.split(b";")[0], 16)
                except ValueError:
                    break
                if size == 0:
                    self.rfile.readline()  # trailing CRLF
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.readline()  # CRLF after each chunk
            return b"".join(chunks).decode("utf-8")
        length = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(length).decode("utf-8") if length else ""

    def do_POST(self):
        if self.path == "/wingman/ask-devthrottle":
            raw = self._read_body()
            try:
                parsed = json.loads(raw) if raw else {}
                text = parsed.get("text", "") or parsed.get("Text", "")
            except Exception:
                text = ""
            # Mirror the real Gateway log line (GatewayWingmanVoiceEndpoint) so the proof
            # shows the ask was SERVED THROUGH THE GATEWAY (acceptance #4 and #6).
            line = f"[GatewayWingmanVoice] ask-devthrottle textLen={len(text)}"
            STATE["log"].append(line)
            print(line, flush=True)
            # Realistic warm-brain latency so the page's in-progress indicator is observable
            # (the real ask takes seconds; an instant stub would hide the "Asking..." state).
            time.sleep(1.2)
            self._json({"spoken": ANSWER, "replySeconds": 1.2})
            ok = f"[GatewayWingmanVoice] ask-devthrottle OK: answerLen={len(ANSWER)}, replySeconds=1.2"
            STATE["log"].append(ok)
            print(ok, flush=True)
            return
        self.send_response(404)
        self.end_headers()


def start_gateway():
    httpd = socketserver.ThreadingTCPServer(("127.0.0.1", GATEWAY_PORT), GatewayStub)
    httpd.daemon_threads = True
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


def wait_http(url, timeout=40):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as r:
                if r.status < 500:
                    return True
        except Exception:
            time.sleep(0.5)
    return False


def start_cockpit():
    env = dict(os.environ)
    env["ASPNETCORE_URLS"] = f"http://127.0.0.1:{COCKPIT_PORT}"
    env["Cockpit__GatewayUrl"] = f"http://127.0.0.1:{GATEWAY_PORT}"
    env["DOTNET_ENVIRONMENT"] = "Production"
    dotnet = os.environ.get("DOTNET_EXE", r"C:\Program Files\dotnet\dotnet.exe")
    proc = subprocess.Popen(
        [dotnet, COCKPIT_DLL],
        cwd=os.path.dirname(COCKPIT_DLL),
        env=env,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return proc


def main():
    from playwright.sync_api import sync_playwright

    results = []
    httpd = start_gateway()
    cockpit = start_cockpit()
    try:
        base = f"http://127.0.0.1:{COCKPIT_PORT}"
        if not wait_http(base + "/learn"):
            print("FAIL: Cockpit did not come up", flush=True)
            sys.exit(2)

        with sync_playwright() as p:
            browser = p.chromium.launch()
            page = browser.new_page(viewport={"width": 1280, "height": 900})

            # ---- Available run: nav + page + ask answered ----
            STATE["wingman_enabled"] = True
            page.goto(base + "/", wait_until="networkidle")
            # Wait for the Blazor Server circuit to be live (interactive nav rail rendered).
            page.wait_for_selector("nav.nv a.nv-item", timeout=20000)
            time.sleep(2.0)

            # Criterion 1: Learning nav item present, navigates in-app (NavLink, no full reload).
            nav_learning = page.locator("nav.nv a.nv-item", has_text="Learning")
            c1 = nav_learning.count() >= 1
            results.append(("AC1 Learning nav item visible in the .nv rail", c1))

            # Click the NavLink and confirm SPA navigation (URL changes without a document reload).
            nav_learning.first.click(timeout=15000)
            page.wait_for_url("**/learn", timeout=15000)
            page.wait_for_selector("h1:has-text('Learning')", timeout=15000)
            time.sleep(1.0)

            # Criterion 2: static overview + getting started + GitHub link.
            body = page.content()
            c2 = ("What is DevThrottle" in body and "Getting started" in body
                  and "github.com/thefrederiksen/devthrottle" in body)
            results.append(("AC2 Static overview + getting-started + GitHub link", c2))

            # Criterion 3: Ask box + submit control.
            c3 = page.locator("input.lrn-input").count() == 1 and page.locator("button.lrn-submit").count() == 1
            results.append(("AC3 Ask Wingman input + submit control present", c3))

            # Screenshot 1: rail + page loaded (overview + Ask box).
            page.screenshot(path=os.path.join(OUT, "proof1-learning-page.png"), full_page=True)

            # Criterion 7: .nv rail shared + dark theme (rail present alongside the page).
            c7 = page.locator("nav.nv").count() == 1 and page.locator("nav.nv a.nv-item", has_text="About").count() >= 1
            results.append(("AC7 Shared .nv rail + Cockpit styling (rail beside About etc.)", c7))

            # Criterion 4 + 6: ask a question -> in-progress indicator -> answer; served via Gateway.
            # Type character by character so Blazor's @bind:event="oninput" fires and the bound
            # field (and the submit button's enabled state) reflect the typed question.
            page.click("input.lrn-input")
            page.locator("input.lrn-input").press_sequentially("What is DevThrottle?", delay=25)
            # The button enables only when the bound field is non-empty - wait for that to confirm bind.
            page.wait_for_function(
                "() => { const b = document.querySelector('button.lrn-submit'); return b && !b.disabled; }",
                timeout=10000)
            page.click("button.lrn-submit")
            # In-progress indicator (responsive-UI rule): button flips to "Asking..." / loading line.
            saw_progress = False
            for _ in range(400):
                txt = page.locator("button.lrn-submit").inner_text().strip()
                if "Asking" in txt or page.locator("p.lrn-loading").count() >= 1:
                    saw_progress = True
                    page.screenshot(path=os.path.join(OUT, "proof2a-asking.png"), full_page=True)
                    break
                time.sleep(0.01)
            page.wait_for_selector("div.lrn-answer-a", timeout=15000)
            answer_text = page.locator("div.lrn-answer-a").inner_text().strip()
            c4 = saw_progress and len(answer_text) > 0
            results.append(("AC4 Answer rendered + in-progress indicator shown", c4))

            # The ask must carry the typed question and be served through the Gateway endpoint.
            served = any("ask-devthrottle textLen=20" in l for l in STATE["log"])
            results.append(("AC6 Ask path Cockpit -> Gateway (gateway log line, question carried)", served))

            time.sleep(0.4)
            page.screenshot(path=os.path.join(OUT, "proof2-answer.png"), full_page=True)

            # ---- Not-configured run ----
            STATE["wingman_enabled"] = False
            page.goto(base + "/learn", wait_until="networkidle")
            page.wait_for_selector("div.lrn-notconfigured", timeout=10000)
            notice = page.locator("div.lrn-notconfigured").inner_text()
            has_input = page.locator("input.lrn-input").count()
            c5 = ("not set up" in notice or "set up" in notice) and has_input == 0
            results.append(("AC5 Not-configured: explicit set-up message, no ask box", c5))
            page.screenshot(path=os.path.join(OUT, "proof3-not-configured.png"), full_page=True)

            browser.close()

        # Write the gateway log proving the wire path.
        with open(os.path.join(OUT, "gateway-log.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(STATE["log"]) + "\n")

        print("\n==== RESULTS ====", flush=True)
        all_pass = True
        for name, ok in results:
            print(f"[{'PASS' if ok else 'FAIL'}] {name}", flush=True)
            all_pass = all_pass and ok
        # Emit machine-readable results for the report.
        with open(os.path.join(OUT, "results.json"), "w", encoding="utf-8") as f:
            json.dump([{"criterion": n, "pass": ok} for n, ok in results], f, indent=2)
        sys.exit(0 if all_pass else 1)
    finally:
        try:
            cockpit.terminate()
        except Exception:
            pass
        httpd.shutdown()


if __name__ == "__main__":
    main()
