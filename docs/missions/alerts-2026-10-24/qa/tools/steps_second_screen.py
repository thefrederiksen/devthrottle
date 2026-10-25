# Section 5.4: the bell is on every phone screen that has the app bar, not only the roster. One screen other than the
# roster - Settings - at 390 x 844, with the same bell and the same count the roster shows.
bh(f'''
new_tab("about:blank")
phone()
enrol("{BASE}/mobile/settings", "{KEY}")
wait_js("document.querySelector('.app-bar h1') && document.querySelector('[data-testid=alerts-badge]')")
time.sleep(1.5)
print("screen:", js("location.pathname"), "heading:", js("document.querySelector('.app-bar h1').textContent"))
print("phone badge:", js("document.querySelector('[data-testid=alerts-badge]').textContent"))
print("bell in the app bar:", js("!!document.querySelector('.app-bar [data-testid=alerts-bell]')"))
shot("phone-bell-on-settings")
''')
