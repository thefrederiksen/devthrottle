"""Helpers the screenshot runs share: open the dialog on a fresh load, and save a PNG."""
import base64, os

# The Cockpit dev server these shots drive. Override with COCKPIT_URL.
COCKPIT = os.environ.get("COCKPIT_URL", "http://localhost:5199")

# Where the PNGs land. Set SHOTS_DIR to send them somewhere else; the default is a "shots" folder
# beside this script, so a run of these three files needs nothing configured.
SHOTS = os.environ.get("SHOTS_DIR") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots")


def open_dialog(cdp, goto_url, wait_for_load, wait):
    # Enter on a route the dev proxy does NOT front (it fronts /sessions itself), then reach Sessions
    # the way a person does - by the left rail - so the router, not a page load, does the navigating.
    goto_url(COCKPIT + "/zzz-staging-entry")
    wait_for_load()
    cdp("Page.reload", ignoreCache=True)
    wait_for_load(); wait(1.5)
    cdp("Runtime.evaluate", returnByValue=True,
        expression="Array.from(document.querySelectorAll('a')).find(a=>a.getAttribute('href')==='/sessions').click(); 1")
    wait(1.5)
    cdp("Runtime.evaluate", returnByValue=True,
        expression="Array.from(document.querySelectorAll('button')).find(b=>b.innerText.trim().startsWith('+ New session')).click(); 1")
    wait(2.0)


def shoot(cdp, name, selector=None, pad=18):
    args = {"format": "png"}
    if selector:
        box = cdp("Runtime.evaluate", returnByValue=True, expression=f"""
            (() => {{ const e = document.querySelector({selector!r});
              if (!e) return null; const r = e.getBoundingClientRect();
              return {{x: r.x, y: r.y, w: r.width, h: r.height}}; }})()
        """)["result"]["value"]
        if box:
            args["clip"] = {"x": max(0, box["x"] - pad), "y": max(0, box["y"] - pad),
                            "width": box["w"] + pad * 2, "height": box["h"] + pad * 2,
                            "scale": 2}
    data = cdp("Page.captureScreenshot", **args)["data"]
    os.makedirs(SHOTS, exist_ok=True)
    path = os.path.join(SHOTS, name)
    with open(path, "wb") as handle:
        handle.write(base64.b64decode(data))
    print("saved", path)
