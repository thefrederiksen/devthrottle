# The overflow fixture: a restart request with a 46-character launcher version and a 300-character reason with no
# break points, and a secret with a long unbroken entry name. Each is a card in Needs you; their Details are opened.
# On the phone's Alerts screen at 360 px, and in a Cockpit popover held to 280 px. overflow() lists every element of the
# Alerts content whose box passes the window's edge or whose content is wider than its box; an empty list is the pass,
# and the content's own width is printed beside it so an empty list is never an empty screen.
OPEN = r'''
def details_open():
    js("document.querySelectorAll('[data-testid=alert-card] details').forEach(d=>d.open=true); 1"); time.sleep(0.4)
def report(label, root):
    print(label, "content width:", js("Math.round(document.querySelector(%s).getBoundingClientRect().width)" % json.dumps(root)), "cards:", js("document.querySelectorAll('[data-testid=alert-card]').length"), "overflow:", overflow(root))
def to(kind):
    js("document.querySelector('[data-testid=alert-card][data-alert-id^=\"%s\"]').scrollIntoView({block:'start'}); 1" % kind); time.sleep(0.4)
'''
bh(OPEN + f'''
new_tab("about:blank")
phone360()
enrol("{BASE}/mobile/alerts", "{PHONE_KEY}")
wait_js("document.querySelector('[data-testid=alert-card]')")
time.sleep(0.8)
report("phone 360 cards", ".alerts-page")
shot("phone-360-overflow-cards")
details_open()
report("phone 360 details", ".alerts-page")
to("restart-request")
shot("phone-360-overflow-restart-details")
js("window.scrollBy(0, 600); 1"); time.sleep(0.3)
shot("phone-360-overflow-restart-details-scrolled")
to("secret-transfer")
shot("phone-360-overflow-secret-details")
''')
bh(OPEN + f'''
desktop()
enrol("{BASE}/sessions", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-badge]')")
js("document.querySelector('[data-testid=rail-alerts]').style.setProperty('--alerts-tray-width','280px'); 1")
click('[data-testid=alerts-bell]')
wait_js("document.querySelector('[data-testid=alert-card]')")
js("document.querySelector('[data-testid=rail-alerts]').style.setProperty('--alerts-tray-width','280px'); 1")
time.sleep(0.6)
report("desktop 280 cards", ".alerts-tray")
shot("desktop-280-popover-overflow-cards")
details_open()
report("desktop 280 details", ".alerts-tray")
shot("desktop-280-popover-overflow-restart-details")
''')
