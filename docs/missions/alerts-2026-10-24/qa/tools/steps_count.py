# The bell at a count, and Alerts opened at that count: the phone app at 390 x 844 (Alerts is its own screen) and the
# Cockpit at 1440 x 900 (Alerts is a popover under the bell). LABEL names the state ("zero", "one", "twelve"); the
# scenario the host was started with decides the alerts.
import os
LABEL = os.environ["QA_LABEL"]
SUMMARY = r'''
def summary(root):
    return js("JSON.stringify({cards:document.querySelectorAll('[data-testid=alert-card]').length, empty:!!document.querySelector('.alerts-empty'), earlier:(document.querySelector('.alerts-section-toggle')||{}).textContent||'none'})") + " overflow: " + overflow(root)
'''
bh(SUMMARY + f'''
new_tab("about:blank")
phone()
enrol("{BASE}/mobile/", "{PHONE_KEY}")
wait_js("document.querySelector('[data-testid=alerts-bell]') && document.querySelector('.roster, .empty, .group')")
time.sleep(1.5)
print("phone bell:", js("document.querySelector('[data-testid=alerts-bell]').getAttribute('aria-label')"))
print("phone badge:", js("(document.querySelector('[data-testid=alerts-badge]')||{{}}).textContent || 'none'"))
shot("phone-bell-{LABEL}")
click('[data-testid=alerts-bell]')
wait_js("document.querySelector('.alerts-page .alerts-section')")
time.sleep(0.8)
print("phone alerts:", js("location.pathname"), summary(".alerts-page"))
shot("phone-alerts-{LABEL}")
desktop()
enrol("{BASE}/sessions", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-bell]')")
time.sleep(2)
print("desktop bell:", js("document.querySelector('[data-testid=alerts-bell]').getAttribute('aria-label')"))
print("desktop title:", js("document.title"))
shot("desktop-bell-{LABEL}")
click('[data-testid=alerts-bell]')
wait_js("document.querySelector('.alerts-tray .alerts-section')")
time.sleep(0.8)
print("desktop alerts:", summary(".alerts-tray"))
shot("desktop-alerts-{LABEL}")
''')
