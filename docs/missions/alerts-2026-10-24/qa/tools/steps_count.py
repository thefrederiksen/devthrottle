# The bell at a count: the phone app at 390 x 844 and the Cockpit at 1440 x 900. LABEL names the state ("zero",
# "one", "twelve"); the scenario the host was started with decides the alerts.
import os
LABEL = os.environ["QA_LABEL"]
bh(f'''
new_tab("about:blank")
phone()
enrol("{BASE}/mobile/", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-bell]') && document.querySelector('.roster, .empty, .group')")
time.sleep(1.5)
print("phone bell:", js("document.querySelector('[data-testid=alerts-bell]').getAttribute('aria-label')"))
print("phone badge:", js("(document.querySelector('[data-testid=alerts-badge]')||{{}}).textContent || 'none'"))
shot("phone-bell-{LABEL}")
desktop()
enrol("{BASE}/sessions", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-bell]')")
time.sleep(2)
print("desktop bell:", js("document.querySelector('[data-testid=alerts-bell]').getAttribute('aria-label')"))
print("desktop title:", js("document.title"))
shot("desktop-bell-{LABEL}")
''')
if LABEL == "twelve":
    bh(f'''
desktop()
js("localStorage.setItem('cockpit.railCollapsed','true')")
goto_url("{BASE}/sessions"); wait_for_load()
wait_js("document.querySelector('[data-testid=alerts-badge]')")
time.sleep(1)
print("collapsed badge:", js("document.querySelector('[data-testid=alerts-badge]').textContent"))
shot("desktop-bell-twelve-collapsed-rail")
js("localStorage.removeItem('cockpit.railCollapsed')")
''')
