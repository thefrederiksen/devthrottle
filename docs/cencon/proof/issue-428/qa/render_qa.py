"""QA render harness for issue #428 - /voice light/dark theme.

Serves the ACTUAL Cockpit wwwroot from the PR branch over a local HTTP server,
loads the real /pages/voice/index.html + voice.css, then drives the page's own
view-switching JS-equivalent (unhide session/wingman views) and injects
representative outbox/error markup so every themed token paints. Screenshots
the page under prefers-color-scheme light and dark. Also samples computed
colors + button sizes and runs a relative-luminance contrast check.
"""
import http.server, socketserver, threading, functools, os, sys, json
from playwright.sync_api import sync_playwright

WWWROOT = sys.argv[1]
OUTDIR = sys.argv[2]
PORT = 8731

Handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=WWWROOT)
httpd = socketserver.TCPServer(("127.0.0.1", PORT), Handler)
httpd.allow_reuse_address = True
t = threading.Thread(target=httpd.serve_forever, daemon=True)
t.start()

# JS to reveal all views and inject representative themed content so tokens paint.
REVEAL = r"""
() => {
  // Show the single-session view (Speak/Play, stage, danger buttons live here + wingman).
  document.querySelectorAll('.view').forEach(v => v.classList.add('hidden'));
  const sv = document.getElementById('session-view');
  sv.classList.remove('hidden');
  document.getElementById('session-name').textContent = 'cc-director (QA theme check)';
  document.getElementById('session-state').textContent = 'idle';
  document.getElementById('session-repo').textContent = 'D:/ReposFred/cc-director';
  document.getElementById('stage-line').textContent = 'Tap Speak and talk.';
  // an error stage to exercise --error-line / --red
  const errStage = document.createElement('div');
  errStage.className = 'stage error';
  errStage.textContent = 'Microphone permission denied.';
  sv.querySelector('.controls').after(errStage);
  // Outbox badges (every status -> every badge token)
  const ob = document.getElementById('outbox-list');
  const statuses = [['pending','Pending'],['uploading','Uploading'],['uploaded','Uploaded'],['failed','Failed'],['stale','Stale']];
  ob.innerHTML = statuses.map(([c,l]) =>
    `<li class="ob-item"><div class="ob-top"><span class="ob-badge ${c}">${l}</span>`
    + `<span class="ob-saved">saved 12:03</span></div>`
    + (c==='failed' ? `<div class="ob-error">upload failed: network</div>` : '')
    + `</li>`).join('');
  document.getElementById('outbox-empty').classList.add('hidden');
  // History sample
  const hl = document.getElementById('history-list');
  hl.innerHTML = `<li class="hist-item"><div class="muted">12:01</div><div>You: what changed in main?</div></li>`;
  document.getElementById('history-empty').classList.add('hidden');
}
"""

REVEAL_WM = r"""
() => {
  document.querySelectorAll('.view').forEach(v => v.classList.add('hidden'));
  const wm = document.getElementById('wingman-view');
  wm.classList.remove('hidden');
  const chat = document.getElementById('wm-chat');
  chat.innerHTML =
    `<div class="wm-msg user">Read the last error</div>`
    + `<div class="wm-msg tool"><div class="wm-meta">tool: read buffer</div>tail -n 20 ...</div>`
    + `<div class="wm-msg error">Session not responding.</div>`
    + `<div class="wm-msg pending">thinking...</div>`;
}
"""

def srgb_to_lin(c):
    c = c/255.0
    return c/12.92 if c <= 0.03928 else ((c+0.055)/1.055)**2.4

def luminance(rgb):
    r,g,b = rgb
    return 0.2126*srgb_to_lin(r)+0.7152*srgb_to_lin(g)+0.0722*srgb_to_lin(b)

def contrast(fg, bg):
    l1, l2 = luminance(fg), luminance(bg)
    hi, lo = max(l1,l2), min(l1,l2)
    return (hi+0.05)/(lo+0.05)

def parse_rgb(s):
    # "rgb(r, g, b)" or "rgba(r, g, b, a)"
    nums = s.replace('rgba','').replace('rgb','').strip('() ').split(',')
    return tuple(float(x) for x in nums[:3])

results = {}
with sync_playwright() as p:
    browser = p.chromium.launch()
    for scheme in ('light','dark'):
        ctx = browser.new_context(color_scheme=scheme, viewport={'width':420,'height':1500},
                                  device_scale_factor=2)
        page = ctx.new_page()
        page.goto(f"http://127.0.0.1:{PORT}/pages/voice/index.html")
        page.wait_for_load_state('networkidle')
        page.evaluate(REVEAL)
        page.wait_for_timeout(150)
        page.screenshot(path=os.path.join(OUTDIR, f"voice-session-{scheme}.png"), full_page=True)

        # sample computed colors
        sample = page.evaluate(r"""
        () => {
          const cs = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
          const el = s => document.querySelector(s);
          const comp = (s, prop) => { const e=el(s); return e?getComputedStyle(e)[prop]:null; };
          const rect = s => { const e=el(s); if(!e) return null; const r=e.getBoundingClientRect(); return {w:Math.round(r.width),h:Math.round(r.height)}; };
          return {
            bodyBg: getComputedStyle(document.body).backgroundColor,
            bodyColor: getComputedStyle(document.body).color,
            h1Color: comp('h1','color'),
            muted: comp('.ob-saved','color'),
            mutedBg: getComputedStyle(document.body).backgroundColor,
            stageErrColor: comp('.stage.error','color'),
            primaryBg: comp('.primary','backgroundColor'),
            primaryColor: comp('.primary','color'),
            dangerBg: comp('.danger','backgroundColor'),
            speakRect: rect('#speak-btn'),
            playRect: rect('#play-btn'),
            wingmanRect: rect('#wingman-btn'),
            badgePendingBorder: comp('.ob-badge.pending','borderColor'),
            badgeStaleBg: comp('.ob-badge.stale','backgroundColor'),
          };
        }
        """)
        # wingman view shot
        page.evaluate(REVEAL_WM)
        page.wait_for_timeout(150)
        page.screenshot(path=os.path.join(OUTDIR, f"voice-wingman-{scheme}.png"), full_page=True)
        results[scheme] = sample
        ctx.close()
    browser.close()

httpd.shutdown()

# Contrast computations
report = {}
for scheme, s in results.items():
    body_bg = parse_rgb(s['bodyBg'])
    ink = parse_rgb(s['bodyColor'])
    muted = parse_rgb(s['muted'])
    primary_bg = parse_rgb(s['primaryBg'])
    primary_ink = parse_rgb(s['primaryColor'])
    err = parse_rgb(s['stageErrColor'])
    report[scheme] = {
        'bodyBg': s['bodyBg'], 'bodyColor': s['bodyColor'],
        'ink_on_bg_contrast': round(contrast(ink, body_bg), 2),
        'muted_on_bg_contrast': round(contrast(muted, body_bg), 2),
        'primaryInk_on_primaryBg_contrast': round(contrast(primary_ink, primary_bg), 2),
        'stageErr_on_bg_contrast': round(contrast(err, body_bg), 2),
        'speakRect': s['speakRect'], 'playRect': s['playRect'], 'wingmanRect': s['wingmanRect'],
        'primaryBg': s['primaryBg'], 'dangerBg': s['dangerBg'],
        'badgeStaleBg': s['badgeStaleBg'], 'badgePendingBorder': s['badgePendingBorder'],
    }

print(json.dumps(report, indent=2))
with open(os.path.join(OUTDIR,'metrics.json'),'w') as f:
    json.dump(report, f, indent=2)
