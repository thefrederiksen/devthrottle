# The whole flow on one app, against the "mixed" scenario: three alerts need an answer (a restart, a secret transfer, a
# message request) and four are Earlier (a declined restart, an accepted restart still running, a denied transfer, an
# allowed message request). APP is "phone" (the phone app at 390 x 844) or "desktop" (the Cockpit at 1440 x 900).
import os
APP = os.environ["QA_APP"]
URL = f"{BASE}/mobile/" if APP == "phone" else f"{BASE}/sessions"
SIZE = "phone()" if APP == "phone" else "desktop()"

ROWS = r'''
def row(text):
    return "[...document.querySelectorAll('[data-testid=alerts-row]')].find(r=>r.textContent.includes(%s))" % json.dumps(text)
def open_row(text):
    r = js("(()=>{const r=%s; if(!r) return null; const b=r.querySelector('.alerts-row-open').getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" % row(text))
    if not r: raise RuntimeError("no row " + text)
    click_at_xy(r[0], r[1]); time.sleep(0.6)
def clear_row(text):
    r = js("(()=>{const r=%s; if(!r) return null; const b=r.querySelector('.alerts-row-clear').getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" % row(text))
    if not r: raise RuntimeError("no clear button on " + text)
    click_at_xy(r[0], r[1]); time.sleep(0.8)
def back():
    click('[aria-label="Back to alerts"]')
def count():
    return js("(document.querySelector('[data-testid=alerts-badge]')||{}).textContent || 'none'")
def sections():
    return js("[...document.querySelectorAll('.alerts-section')].map(s=>s.getAttribute('aria-label')+': '+[...s.querySelectorAll('.alerts-row-title')].map(t=>t.textContent).join(' | ')).join('  //  ')")
'''

# 1. The session list with no cards in it, and the bell with its count.
bh(ROWS + f'''
new_tab("about:blank")
{SIZE}
enrol("{URL}", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-badge]')")
time.sleep(1.5)
print("cards in the session list:", js("document.querySelectorAll('.restart-request, .secret-transfer, .link-request, .restart-requests, .secret-transfers, .link-requests').length"))
print("badge:", count())
shot("{APP}-session-list-no-cards")
''')

# 2. The tray: Needs you, then Earlier.
bh(ROWS + f'''
click('[data-testid=alerts-bell]')
wait_js("document.querySelector('.alerts-section')")
time.sleep(0.8)
print("tray:", sections())
shot("{APP}-tray-needs-you-and-earlier")
''')

# 3. Each kind, opened.
for text, name in [("Restart the Director on mac-mini", "restart"), ("vercel-bypass", "secret-transfer"), ("121", "message-request")]:
    bh(ROWS + f'''
open_row({text!r})
wait_js("document.querySelector('[data-testid=alert-detail]')")
print("opened:", js("document.querySelector('[data-testid=alert-detail]').getAttribute('data-kind')"))
js("document.querySelectorAll('.alert-details').forEach(d=>d.open=true); 1")
time.sleep(0.4)
shot("{APP}-opened-{name}")
back()
''')

# 4. Accept the restart, in two steps. With no Director attached to this local Gateway, the Gateway re-checks the
#    machine and closes the request with its own sentence; either way it leaves Needs you at once.
bh(ROWS + f'''
before = count()
open_row("Restart the Director on mac-mini")
click('[data-testid=alert-detail] .alert-btn-primary')
wait_js("document.querySelector('[data-testid=alert-detail] .alert-btn-danger')")
time.sleep(0.3)
shot("{APP}-accept-restart-step-1-confirm")
click('[data-testid=alert-detail] .alert-btn-danger')
time.sleep(2.5)
print("after accept, detail status:", js("(document.querySelector('[data-testid=alert-detail] .alert-status')||{{}}).textContent"))
shot("{APP}-accept-restart-step-2-answered")
back()
time.sleep(0.8)
print("badge before:", before, "after:", count())
print("tray:", sections())
shot("{APP}-accept-restart-step-3-moved-to-earlier")
''')

# 5. Clear one Earlier alert.
bh(ROWS + f'''
clear_row("build-box")
time.sleep(0.8)
print("tray:", sections())
shot("{APP}-cleared-one")
''')

# 6. A clear refused. The tray's list read is held in the browser for this one step (Fetch pauses GET /gateway/alerts
#    only; the clear itself goes through), the allowed message request is put back to waiting on the Gateway, and its X
#    is pressed on the row the tray still shows as Earlier: the Gateway refuses and the tray says so in its words.
bh(ROWS + """
cdp("Fetch.enable", patterns=[{"urlPattern": "*/gateway/alerts", "requestStage": "Request"}])
print("list read held")
""")
host.send("reopen")
bh(ROWS + f"""
clear_row("may message 142")
time.sleep(0.8)
print("refusal:", js("[...document.querySelectorAll('.alerts-tray [role=alert]')].map(e=>e.textContent).join(' / ')"))
shot("{APP}-clear-refused-needs-an-answer")
cdp("Fetch.disable")
""")

# 7. Clear all.
bh(ROWS + f'''
time.sleep(5.5)
click('.alerts-clear-all')
time.sleep(1.2)
print("tray:", sections())
print("clear all still offered:", js("!!document.querySelector('.alerts-clear-all')"))
shot("{APP}-cleared-all")
''')

# 8. The Gateway unreachable: the last-known list stays and the tray says the read failed.
host.send("down")
bh(ROWS + f'''
time.sleep(7)
print("failure line:", js("[...document.querySelectorAll('.alerts-tray [role=alert]')].map(e=>e.textContent).join(' / ')"))
print("tray:", sections())
shot("{APP}-gateway-unreachable")
''')
