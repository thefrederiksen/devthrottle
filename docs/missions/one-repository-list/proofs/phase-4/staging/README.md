# The staging these screenshots were taken through

Three files, and no product code among them. They exist so the screenshots in `../screens/` can be
retaken, and so a reader can see exactly what was staged rather than take the report's word for it.

- `stub_gateway.py` - a stand-in Gateway serving only what the New Session dialog reads. It re-reads
  the scenario on every request, so a scenario can be swapped between shots without restarting it.
- `set_scenario.py` - writes `scenario.json`. It holds the five scenarios the shots use: the full
  list, an empty machine, a Director that is not connected (502), an Add that succeeds without
  landing in the catalogue, and an Add the machine refuses. **Every value in it is invented** - this
  repository is public, and no real machine, account or path appears in a committed screenshot.
- `shot.py` - opens the dialog on a fresh load and saves a PNG.

To run them:

```
# 1. the stand-in Gateway
python3 stub_gateway.py 5299

# 2. the Cockpit's own dev server, with the stand-in behind it
cd apps/cockpit && COCKPIT_PROXY_TARGET=http://127.0.0.1:5299 npx vite --port 5199 --strictPort

# 3. a Director-owned browser profile, never a hand-launched one
cc-devthrottle browser start <profile>
eval "$(cc-devthrottle browser attach '<profile>')"

# 4. a scenario, then a shot
python3 set_scenario.py full
browser-harness <<'PY'
import sys; sys.path.insert(0, "<this folder>")
from shot import open_dialog, shoot
cdp("Emulation.setDeviceMetricsOverride", width=1440, height=1040, deviceScaleFactor=2, mobile=False)
open_dialog(cdp, goto_url, wait_for_load, wait)
shoot(cdp, "01-the-new-session-tab.png", ".newsess-modal")
PY
```

**One trap, and it will cost you an afternoon if you meet it cold.** The Cockpit dev server's proxy
fronts `/sessions`, which is ALSO the Cockpit's own route for the page this dialog opens from. A hard
navigation to `/sessions` is therefore answered by the Gateway and the browser renders JSON.
`open_dialog` enters on a route the proxy does not front and reaches Sessions through the left rail,
so the router does the navigating and the proxy never sees it.

**The browser must be a Director-owned profile.** Never hand-launch Chrome with a debugging port - see
the `browsers` skill for why that fails into the wrong session rather than failing loudly.
