# The overflow fixture: a restart request with a 46-character launcher version and a 300-character reason with no
# break points, and a secret with a long unbroken entry name. Opened with Details expanded, on the phone at 360 px and
# in a Cockpit popover held to 280 px. overflow() lists every element in the tray whose box passes the viewport's edge
# or whose content is wider than its box; an empty list is the pass, and the tray's own width is printed beside it so an
# empty list is never an empty tray.
OPEN = r'''
def open_first(kind):
    r = js("(()=>{const r=[...document.querySelectorAll('[data-testid=alerts-row]')].find(r=>r.getAttribute('data-alert-id').startsWith(%s)); const b=r.querySelector('.alerts-row-open').getBoundingClientRect(); return [b.left+b.width/2,b.top+b.height/2];})()" % json.dumps(kind))
    click_at_xy(r[0], r[1]); time.sleep(0.6)
    js("document.querySelectorAll('.alert-details').forEach(d=>d.open=true); 1"); time.sleep(0.4)
def report(label):
    print(label, "tray width:", js("Math.round(document.querySelector('.alerts-tray').getBoundingClientRect().width)"), "overflow:", overflow())
'''
bh(OPEN + f'''
new_tab("about:blank")
phone360()
enrol("{BASE}/mobile/", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-badge]')")
click('[data-testid=alerts-bell]')
wait_js("document.querySelector('[data-testid=alerts-row]')")
time.sleep(0.6)
report("phone 360 list")
shot("phone-360-overflow-list")
open_first("restart-request")
report("phone 360 restart")
shot("phone-360-overflow-restart-details")
js("document.querySelector('.alerts-tray-body').scrollTop = 100000; 1"); time.sleep(0.3)
shot("phone-360-overflow-restart-details-scrolled")
click('[aria-label="Back to alerts"]')
open_first("secret-transfer")
report("phone 360 secret")
shot("phone-360-overflow-secret-details")
''')
bh(OPEN + f'''
desktop()
enrol("{BASE}/sessions", "{KEY}")
wait_js("document.querySelector('[data-testid=alerts-badge]')")
js("document.querySelector('[data-testid=rail-alerts]').style.setProperty('--alerts-tray-width','280px'); 1")
click('[data-testid=alerts-bell]')
wait_js("document.querySelector('[data-testid=alerts-row]')")
js("document.querySelector('[data-testid=rail-alerts]').style.setProperty('--alerts-tray-width','280px'); 1")
time.sleep(0.6)
report("desktop 280 list")
shot("desktop-280-popover-overflow-list")
open_first("restart-request")
report("desktop 280 restart")
shot("desktop-280-popover-overflow-restart-details")
''')
