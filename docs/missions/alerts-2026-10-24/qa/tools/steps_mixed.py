# The whole flow on one app, against the "mixed" scenario: three alerts need an answer (a restart, a secret transfer, a
# message request) and four are Earlier (a declined restart, an accepted restart still running, a denied transfer, an
# allowed message request). APP is "phone" (the phone app at 390 x 844, where Alerts is a screen of its own) or
# "desktop" (the Cockpit at 1440 x 900, where Alerts is a popover under the bell).
#
# Phase 5: every alert that needs an answer is a card with its buttons on it, so each kind is answered straight from
# its card - one tap, the restart's two - and the card leaves with a line saying what happened. Earlier is collapsed.
import os
APP = os.environ["QA_APP"]
URL = f"{BASE}/mobile/" if APP == "phone" else f"{BASE}/sessions"
SIZE = "phone()" if APP == "phone" else "desktop()"
DEVICE_KEY = PHONE_KEY if APP == "phone" else KEY
ROOT = ".alerts-page" if APP == "phone" else ".alerts-tray"

HELP = r'''
ROOT = %r
def card(kind):
    return "document.querySelector('[data-testid=alert-card][data-alert-id^=\"%%s\"]')" %% kind
def press_on_card(kind, label):
    r = js("(()=>{const c=%%s; if(!c) return null; const b=[...c.querySelectorAll('button')].find(b=>b.textContent.trim()===%%s); if(!b) return null; const x=b.getBoundingClientRect(); return [x.left+x.width/2,x.top+x.height/2];})()" %% (card(kind), json.dumps(label)))
    if not r: raise RuntimeError("no %%s button on the %%s card" %% (label, kind))
    click_at_xy(r[0], r[1]); time.sleep(0.6)
def scroll_to(expr):
    js("(()=>{const e=%%s; if(e) e.scrollIntoView({block:'center'}); return 1;})()" %% expr); time.sleep(0.4)
def row(text):
    return "[...document.querySelectorAll('[data-testid=alerts-row]')].find(r=>r.textContent.includes(%%s))" %% json.dumps(text)
def clear_row(text):
    r = js("(()=>{const r=%%s; if(!r) return null; r.scrollIntoView({block:'center'}); const b=r.querySelector('.alerts-row-clear').getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" %% row(text))
    if not r: raise RuntimeError("no clear button on " + text)
    click_at_xy(r[0], r[1]); time.sleep(0.8)
def open_row(text):
    r = js("(()=>{const r=%%s; if(!r) return null; r.scrollIntoView({block:'center'}); const b=r.querySelector('.alerts-row-open').getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" %% row(text))
    if not r: raise RuntimeError("no row " + text)
    click_at_xy(r[0], r[1]); time.sleep(0.6)
def earlier(open_it):
    is_open = js("(document.querySelector('.alerts-section-toggle')||{}).getAttribute ? document.querySelector('.alerts-section-toggle').getAttribute('aria-expanded') : 'none'")
    if (is_open == "true") != open_it and is_open != "none":
        click('.alerts-section-toggle')
def count():
    return js("(document.querySelector('[data-testid=alerts-badge]')||{}).textContent || 'none'")
def waiting():
    # What still needs an answer, measured on what this screen shows: the bell's badge in the Cockpit, and the cards on
    # the phone's Alerts screen, which has no bell (it IS Alerts). Never a measurement of an element that is not there.
    if ROOT == ".alerts-page":
        return "cards " + str(js("document.querySelectorAll('[data-testid=alert-card]').length"))
    return "badge " + count()
def state():
    return js("JSON.stringify({needsYou:[...document.querySelectorAll('[data-testid=alert-card] .alert-detail-title')].map(t=>t.textContent), earlier:(document.querySelector('.alerts-section-toggle')||{}).textContent||'none', rows:[...document.querySelectorAll('.alerts-row-title')].map(t=>t.textContent), answered:[...document.querySelectorAll('[data-testid=alerts-answered]')].map(t=>t.textContent), errors:[...document.querySelectorAll(%%s)].map(e=>e.textContent), empty:!!document.querySelector('.alerts-empty')})" %% json.dumps(ROOT + " [role=alert]"))
''' % ROOT

def open_alerts():
    return "click('[data-testid=alerts-bell]')\nwait_js(\"document.querySelector('" + ROOT + " .alerts-section')\")\ntime.sleep(0.8)\n"

# 1. The session list with no cards in it, and the bell with its count.
bh(HELP + f'''
new_tab("about:blank")
{SIZE}
enrol("{URL}", "{DEVICE_KEY}")
wait_js("document.querySelector('[data-testid=alerts-badge]')")
time.sleep(1.5)
print("cards in the session list:", js("document.querySelectorAll('.restart-request, .secret-transfer, .link-request, .restart-requests, .secret-transfers, .link-requests, [data-testid=alert-card]').length"))
print("badge:", count())
shot("{APP}-session-list-no-cards")
''')

# 2. Alerts opened: three cards in Needs you, Earlier collapsed. On the phone it is its own screen: no shade, no sheet.
bh(HELP + open_alerts() + f'''
print("address:", js("location.pathname"), "dialog:", js("!!document.querySelector('[role=dialog]')"), "shade:", js("!!document.querySelector('.alerts-backdrop')"))
print("state:", state())
print("overflow:", overflow(ROOT))
shot("{APP}-alerts-three-need-you-earlier-collapsed")
''')
if APP == "phone":
    # The screen scrolls like any page: the last card and the Earlier header below it.
    bh(HELP + f'''
js("window.scrollTo(0, document.body.scrollHeight); 1"); time.sleep(0.5)
shot("phone-alerts-three-need-you-scrolled")
js("window.scrollTo(0, 0); 1"); time.sleep(0.3)
''')

# 3. Earlier expanded, then one row opened in place (the accepted restart still running).
bh(HELP + f'''
earlier(True)
scroll_to("document.querySelector('.alerts-earlier')")
print("state:", state())
shot("{APP}-alerts-earlier-expanded")
open_row("linux-rig")
print("row opened:", js("(document.querySelector('.alerts-row-more .alert-summary')||{{}}).textContent"))
shot("{APP}-alerts-earlier-row-opened")
open_row("linux-rig")
earlier(False)
''')

# 4. A card's Details, opened (the restart's machine diagnostics), then closed again.
bh(HELP + f'''
js("(()=>{{const c=%s; c.querySelector('details').open=true; c.scrollIntoView({{block:'start'}}); return 1;}})()" % card("restart-request")); time.sleep(0.4)
shot("{APP}-card-restart-details")
js("(()=>{{const c=%s; c.querySelector('details').open=false; return 1;}})()" % card("restart-request")); time.sleep(0.2)
''')

# 5. The secret transfer, approved straight from its card: it leaves Needs you with a line saying so.
bh(HELP + f'''
before = waiting()
scroll_to(card("secret-transfer"))
press_on_card("secret-transfer", "Approve")
wait_js("document.querySelector('[data-testid=alerts-answered]')")
js("window.scrollTo(0, 0); document.querySelectorAll('.alerts-tray-body').forEach(b=>b.scrollTop=0); 1"); time.sleep(0.3)
print("needs an answer, before:", before, "after:", waiting())
print("state:", state())
shot("{APP}-answer-secret-transfer-approved-then-failed")
''')

# 6. The message request, approved straight from its card.
bh(HELP + f'''
before = waiting()
scroll_to(card("message-request"))
press_on_card("message-request", "Approve")
time.sleep(1.0)
js("window.scrollTo(0, 0); document.querySelectorAll('.alerts-tray-body').forEach(b=>b.scrollTop=0); 1"); time.sleep(0.3)
print("needs an answer, before:", before, "after:", waiting())
print("state:", state())
shot("{APP}-answer-message-request-allowed")
''')

# 7. The restart, accepted straight from its card in two steps. With no Director attached to this local Gateway, the
#    Gateway re-checks the machine and closes the request with its own word, Stopped; either way it leaves Needs you.
bh(HELP + f'''
before = waiting()
press_on_card("restart-request", "Accept")
wait_js("document.querySelector('[data-testid=alert-card] .alert-btn-danger')")
time.sleep(0.3)
print("confirm:", js("document.querySelector('[data-testid=alert-card] .alert-confirm').textContent"))
shot("{APP}-answer-restart-step-1-confirm")
press_on_card("restart-request", "Yes, restart it")
time.sleep(2.5)
print("needs an answer, before:", before, "after:", waiting())
print("state:", state())
shot("{APP}-answer-restart-step-2-answered")
''')

# 8. Nothing needs the owner now: one calm line, and Earlier still there beneath it.
bh(HELP + f'''
time.sleep(6.5)
print("state:", state())
shot("{APP}-alerts-nothing-needs-you-earlier-below")
''')

# 9. Clear one Earlier alert.
bh(HELP + f'''
earlier(True)
clear_row("build-box")
time.sleep(0.8)
print("state:", state())
shot("{APP}-cleared-one")
''')

# 10. A clear refused. The list read is held in the browser for this one step (Fetch pauses GET /gateway/alerts only;
#     the clear itself goes through), the allowed message request is put back to waiting on the Gateway, and its X is
#     pressed on the row still shown as Earlier: the Gateway refuses and the screen says so in its words.
bh(HELP + """
cdp("Fetch.enable", patterns=[{"urlPattern": "*/gateway/alerts", "requestStage": "Request"}])
print("list read held")
""")
host.send("reopen")
bh(HELP + f"""
clear_row("Docs Writer (130) may message")
time.sleep(0.8)
print("state:", state())
shot("{APP}-clear-refused-needs-an-answer")
cdp("Fetch.disable")
""")

# 11. Clear all, from Earlier's own header. The reopened request, which needs an answer, stays as a card.
bh(HELP + f'''
time.sleep(5.5)
click('.alerts-clear-all')
time.sleep(1.2)
js("window.scrollTo(0, 0); 1")
print("state:", state())
print("clear all still offered:", js("!!document.querySelector('.alerts-clear-all')"))
shot("{APP}-cleared-all")
''')

if APP == "phone":
    # 12. Back returns to where the owner was, with the bell focused again.
    bh(HELP + f'''
click('.app-bar .back-link')
wait_js("document.querySelector('[data-testid=alerts-bell]')")
time.sleep(0.8)
print("address:", js("location.pathname"), "focus on the bell:", js("document.activeElement && document.activeElement.getAttribute('data-testid')"), "badge:", count())
shot("phone-back-to-session-list")
''' + open_alerts())

# 13. The Gateway unreachable: the last-known list stays and the screen says the read failed.
host.send("down")
bh(HELP + f'''
time.sleep(7)
js("window.scrollTo(0, 0); 1")
print("state:", state())
shot("{APP}-gateway-unreachable")
''')
