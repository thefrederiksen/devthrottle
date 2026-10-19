# Run with browser-harness after building the fixture and attaching a Director-owned test browser.
# No live account, real service, sign-in or writes are involved. Every path comes from the real route table.
import json
from pathlib import Path

fixture = Path('artifacts/license-alarm-layout/index.html').resolve().as_uri()
new_tab(fixture)
wait_for_load()
paths = js('window.licenseLayoutPaths')
assert isinstance(paths, list) and len(paths) >= 40, 'The production route inventory was not loaded'
assert paths[0] == '/sessions', 'The long Sessions list must run first'
results = []
for width, height in [(1440, 900), (390, 844)]:
    cdp('Emulation.setDeviceMetricsOverride', width=width, height=height, deviceScaleFactor=1, mobile=False)
    for path in paths:
        print(f'Checking {width}: {path}', flush=True)
        js('window.openLicenseLayout(' + json.dumps(path) + ')')
        # Wait on rendered frames and on a specific element, not a guessed sleep.
        answer = js('''(async () => {
          for (let i = 0; i < 120; i++) {
            await new Promise(requestAnimationFrame);
            const banner = document.querySelector('.license-alarm-banner');
            const page = document.querySelector('.routed-page');
            if (banner && page?.firstElementChild) {
              await new Promise(requestAnimationFrame);
              return window.measureLicenseLayout();
            }
          }
          throw new Error('The expected banner and routed content did not render');
        })()''')
        assert answer['banner']['text'] == '4 license problems - Open Licenses', (path, answer)
        assert 0 < answer['banner']['height'] < 60, (path, answer)
        assert answer['banner']['left'] >= 0 and answer['banner']['right'] <= width, (path, answer)
        assert answer['linkRight'] is not None and answer['linkRight'] <= width, (path, answer)
        assert answer['page']['height'] > 0, (path, answer)
        assert answer['page']['top'] >= answer['banner']['bottom'] - 1, (path, answer)
        assert answer['page']['contentTop'] >= answer['banner']['bottom'] - 1, (path, answer)
        assert answer['page']['bottom'] <= height + 1, (path, answer)
        if path == '/sessions':
            assert answer['rows'] == 100 and answer['scrolls'] is True, answer
            assert answer['screenBottom'] <= height + 1, answer
            print(capture_screenshot())
        results.append({'width': width, 'path': path, **answer})

# A known-bad control: make the routed page own the viewport as it did before the frame reserved banner space.
js("window.openLicenseLayout('/sessions')")
js('''(async () => { for (let i = 0; i < 4; i++) await new Promise(requestAnimationFrame); })()''')
js("document.querySelector('.routed-page').style.cssText = 'flex:none;height:100vh'")
bad = js('window.measureLicenseLayout()')
assert bad['page']['bottom'] > bad['viewportHeight'], 'The overlap detector did not reject the old full-height page'
Path('artifacts/license-alarm-layout/results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
print(json.dumps({'passed': len(results), 'routes': len(paths), 'widths': [1440, 390], 'knownBadRejected': True}))
