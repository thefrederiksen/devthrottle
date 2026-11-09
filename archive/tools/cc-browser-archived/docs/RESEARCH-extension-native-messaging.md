# Research: Browser Extension + Native Messaging for cc-browser

**Date:** 2026-03-04
**Status:** Research only -- no implementation planned
**Author:** Claude Code research session

---

## Problem Statement

cc-browser uses Playwright with stealth JS patches to control Chrome. Cloudflare Turnstile
and similar advanced bot detection systems detect this because:

1. **CDP protocol artifacts** -- Chrome DevTools Protocol leaves fingerprints in the browser
   environment (`Runtime.enable`, debugger domain markers, etc.)
2. **JS prototype modifications** -- Stealth patches (e.g. overriding `navigator.webdriver`,
   patching `Permissions.query`) modify JS prototypes in ways that fingerprinting scripts detect
3. **Playwright pipe transport** -- Even using `--remote-debugging-pipe` instead of WebSocket,
   the browser is launched with automation flags that are visible to detection scripts
4. **`chrome.runtime` absence** -- Automated browsers lack extension APIs that real user
   browsers always have (at minimum the Chrome Web Store extension)

These are **fundamental architectural limitations** of controlling Chrome from outside. No amount
of stealth patching can fully eliminate them because the detection scripts run in the same JS
environment and can probe for artifacts.

---

## The Extension + Native Messaging Alternative

### Core Idea

Instead of controlling Chrome from the outside (Playwright/CDP), run a **browser extension
inside Chrome** that communicates with the cc-browser Node.js process via Chrome's
**Native Messaging API**.

```
cc-browser CLI
    |
    v
cc-browser daemon (Node.js)
    |
    | stdin/stdout JSON (Native Messaging protocol)
    |
    v
Chrome Extension (manifest v3)
    |
    | chrome.* APIs (tabs, scripting, debugger, etc.)
    |
    v
Web Pages
```

### Why This Is Undetectable

- The extension runs **inside Chrome's extension sandbox** -- the same environment as
  1Password, uBlock Origin, and every other extension
- Native Messaging uses **stdin/stdout** between the extension and a registered native host
  -- this is a documented, supported Chrome API, not a hack
- No CDP connection exists -- there is no `--remote-debugging-port`, no `--remote-debugging-pipe`,
  no WebSocket debugger URL
- No automation flags -- Chrome launches as a **normal user browser**, not an automated instance
- `navigator.webdriver` is genuinely `false` (not patched to appear false)
- Detection scripts cannot distinguish the extension from an ad-blocker or password manager

### Who Uses This Approach

| Product | Type | Notes |
|---------|------|-------|
| Axiom.ai | Browser automation | Extension-based, no CDP |
| Browserflow | Browser automation | Extension-first architecture |
| 1Password | Password manager | Native Messaging for desktop app sync |
| Bitwarden | Password manager | Native Messaging for CLI integration |
| Corporate RPA tools | Enterprise automation | Extension + native host |
| Grammarly | Writing assistant | Extension with native component |

---

## Architecture Deep Dive

### 1. Chrome Extension (Manifest V3)

The extension is a standard Chrome extension with these key permissions:

```json
{
  "manifest_version": 3,
  "name": "cc-browser-bridge",
  "permissions": [
    "tabs",
    "activeTab",
    "scripting",
    "nativeMessaging",
    "storage",
    "cookies",
    "downloads"
  ],
  "background": {
    "service_worker": "background.js"
  },
  "content_scripts": [{
    "matches": ["<all_urls>"],
    "js": ["content.js"]
  }]
}
```

**Background service worker** (`background.js`):
- Connects to the native host via `chrome.runtime.connectNative("cc_browser")`
- Receives commands from cc-browser daemon (navigate, click, type, screenshot, etc.)
- Dispatches commands to the appropriate chrome.* API
- Sends results back through the native messaging port

**Content script** (`content.js`):
- Injected into web pages to interact with DOM
- Can read/modify page content, click elements, fill forms
- Communicates with background worker via `chrome.runtime.sendMessage`

### 2. Native Messaging Host

A small executable (or Node.js script) registered with Chrome that communicates via
stdin/stdout using Chrome's native messaging protocol:

**Message format:** Each message is prefixed with a 4-byte little-endian length, followed
by UTF-8 JSON.

```
[4 bytes: length][JSON payload]
```

**Registration** (`cc_browser.json` in Chrome's NativeMessagingHosts directory):

```json
{
  "name": "cc_browser",
  "description": "cc-browser native messaging host",
  "path": "C:\\Users\\alice\\AppData\\Local\\cc-director\\bin\\cc-browser-host.cmd",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://[extension-id]/"
  ]
}
```

**Registry key** (Windows):
```
HKCU\Software\Google\Chrome\NativeMessagingHosts\cc_browser
  (Default) = "C:\Users\alice\AppData\Local\cc-director\bin\cc_browser.json"
```

### 3. cc-browser Daemon

The existing daemon would add a new transport layer alongside (or replacing) Playwright:

```
Current:  daemon -> Playwright -> CDP -> Chrome
New:      daemon -> Native Messaging Host -> Extension -> chrome.* APIs -> Chrome
```

The daemon's HTTP API (used by CLI and other tools) stays the same. Only the internal
transport changes.

---

## Chrome API Mapping

Each Playwright capability maps to one or more chrome.* extension APIs:

| Playwright | Extension API | Notes |
|-----------|--------------|-------|
| `page.goto(url)` | `chrome.tabs.update({url})` | Tab navigation |
| `page.click(selector)` | Content script `document.querySelector().click()` | DOM interaction |
| `page.type(selector, text)` | Content script + `InputEvent` dispatch | Must simulate real events |
| `page.screenshot()` | `chrome.tabs.captureVisibleTab()` | Returns base64 PNG |
| `page.evaluate(fn)` | `chrome.scripting.executeScript()` | Run JS in page context |
| `page.waitForSelector()` | Content script MutationObserver | Poll or observe DOM |
| `page.setExtraHTTPHeaders()` | `chrome.declarativeNetRequest` | Modify request headers |
| `page.cookies()` | `chrome.cookies.getAll()` | Read/write cookies |
| `page.pdf()` | `chrome.printing` (limited) | Extension printing API is limited |
| `page.content()` | Content script `document.documentElement.outerHTML` | Full page HTML |
| `page.$$(selector)` | Content script `querySelectorAll` | Element queries |
| `page.waitForNavigation()` | `chrome.webNavigation.onCompleted` | Navigation events |
| `elementHandle.boundingBox()` | Content script `getBoundingClientRect()` | Element geometry |
| `page.mouse.move/click` | Content script `dispatchEvent(MouseEvent)` | Synthetic mouse events |
| `page.keyboard.type` | Content script `dispatchEvent(KeyboardEvent)` | Synthetic keyboard events |

### Gaps and Limitations

| Capability | Extension Support | Workaround |
|-----------|------------------|------------|
| Network interception | `chrome.declarativeNetRequest` (limited rules) | Can block/redirect but not modify response bodies |
| Full page PDF | Not available in extensions | Use `chrome.tabs.captureVisibleTab()` + scroll stitching |
| Headless mode | Not applicable | Extension requires a visible browser window |
| Multiple browser contexts | One profile per Chrome instance | Launch separate Chrome instances with different user data dirs |
| iframe interaction | Content scripts can target iframes | Must declare `all_frames: true` in manifest |
| File download interception | `chrome.downloads` API | Can monitor and control downloads |
| Geolocation spoofing | Not directly available | Could use content script to override `navigator.geolocation` |

---

## Detection Resistance Analysis

### What Turnstile/Bot Detection Checks

| Check | CDP/Playwright | Extension Approach |
|-------|---------------|-------------------|
| `navigator.webdriver` | `true` (must be patched) | Genuinely `false` |
| CDP artifacts in JS | Present (even with stealth) | Absent |
| `window.chrome.runtime` | Missing or inconsistent | Present and functional |
| Automation flags in User-Agent | Sometimes leaked | None present |
| WebDriver BiDi markers | Present | Absent |
| `chrome.csi()` timing | Inconsistent with automation | Normal |
| Plugin/extension enumeration | Empty or suspicious | Normal (has real extensions) |
| Stack trace analysis | Reveals Playwright frames | Normal browser frames |
| `Error().stack` fingerprint | CDP injection artifacts | Clean |
| Permission API behavior | Patched (detectable) | Genuine browser behavior |

**Result:** Extension approach eliminates ALL known automation detection vectors.

### Theoretical Risks

1. **Extension enumeration** -- Sites could detect the cc-browser extension specifically.
   Mitigation: Use a generic extension name/ID, or install as an unpacked extension with
   randomized ID.
2. **Behavioral analysis** -- Superhuman speed or mechanical patterns are detectable
   regardless of approach. Mitigation: Human-mode delays (already implemented in cc-browser).
3. **Chrome policy changes** -- Google could restrict Native Messaging or extension
   capabilities. Risk: Low, these APIs are used by enterprise tools and password managers.

---

## Implementation Complexity

### Effort Estimate

| Component | Complexity | Description |
|-----------|-----------|-------------|
| Chrome extension scaffold | Low | manifest.json, background worker, content script |
| Native messaging host | Low | stdin/stdout JSON protocol handler |
| Windows registry setup | Low | One registry key for native host registration |
| Extension installation | Low | `--load-extension` flag or Chrome policies |
| Command protocol design | Medium | Define JSON message format for all operations |
| Tab/navigation commands | Low | Direct chrome.tabs API mapping |
| DOM interaction commands | Medium | Content script injection and message passing |
| Screenshot capture | Low | `chrome.tabs.captureVisibleTab()` |
| Cookie management | Low | Direct chrome.cookies API mapping |
| Form interaction (type, click) | Medium | Realistic event dispatch in content scripts |
| Element querying (ARIA snapshot) | Medium-High | Reimplement snapshot.mjs logic in content script |
| Wait/polling mechanisms | Medium | MutationObserver + navigation events |
| Network request modification | Medium-High | `declarativeNetRequest` rules |
| Error handling and reconnection | Medium | Extension disconnection, tab crashes |
| Daemon transport layer | Medium | New transport alongside/replacing Playwright |
| Migration of existing commands | High | 27+ CLI commands need extension equivalents |
| Testing | High | Need integration tests without Playwright |

**Total estimate:** Significant rewrite of the transport layer. The CLI and daemon HTTP API
remain unchanged, but all browser interaction code must be reimplemented using chrome.* APIs.

### Migration Strategy (if ever implemented)

1. **Phase 1:** Build extension + native host as a parallel transport (keep Playwright)
2. **Phase 2:** Implement core commands (navigate, click, type, screenshot, snapshot)
3. **Phase 3:** Migrate remaining commands one by one
4. **Phase 4:** Add extension-only capabilities (real cookie access, extension context)
5. **Phase 5:** Deprecate Playwright transport for sites that require stealth

This allows incremental migration with fallback to Playwright for commands not yet ported.

---

## Key Technical Details

### Native Messaging Protocol

Chrome enforces a 1MB message size limit for native messaging. For large payloads
(screenshots, page HTML), messages must be chunked or the result must be written to a
temp file with the path sent via the message.

```javascript
// Background worker - sending a command to native host
const port = chrome.runtime.connectNative("cc_browser");

port.onMessage.addListener((response) => {
  // Handle response from cc-browser daemon
  console.log("Response:", response);
});

port.postMessage({
  id: "cmd_1",
  action: "navigate",
  params: { url: "https://example.com" }
});
```

```javascript
// Native host (Node.js) - reading/writing messages
function readMessage(callback) {
  let header = Buffer.alloc(4);
  let bytesRead = 0;
  process.stdin.on('readable', () => {
    // Read 4-byte length prefix, then JSON payload
    // ...
  });
}

function writeMessage(msg) {
  const json = JSON.stringify(msg);
  const buf = Buffer.from(json, 'utf-8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(buf.length, 0);
  process.stdout.write(header);
  process.stdout.write(buf);
}
```

### Extension Installation Options

1. **Unpacked extension** (`--load-extension=/path/to/ext`) -- Simplest for development,
   shows "Developer mode" banner
2. **Chrome Enterprise Policy** -- Install via registry policy, no banner, requires
   extension ID
3. **Chrome Web Store** -- Most legitimate, but requires publishing and review
4. **Self-hosted CRX** -- Distribute .crx file, install via policy

For cc-browser's use case, option 1 (unpacked) or 2 (enterprise policy) makes the most
sense. Enterprise policy is ideal because it suppresses the developer mode banner and
doesn't require Chrome Web Store publishing.

---

## Comparison Summary

| Aspect | Current (Playwright/CDP) | Extension + Native Messaging |
|--------|------------------------|------------------------------|
| Detection resistance | Low-Medium (stealth patches help but are detectable) | Very High (indistinguishable from real extensions) |
| Implementation effort | Already done | Significant rewrite |
| API coverage | Full (Playwright has everything) | Partial (some capabilities harder/impossible) |
| Headless support | Yes | No |
| Speed | Fast (direct protocol) | Slightly slower (message passing overhead) |
| Maintenance | Playwright team maintains CDP layer | Must maintain extension ourselves |
| Chrome updates | Playwright tracks Chrome releases | Extension APIs are stable and backward-compatible |
| Multi-tab control | Full control | Full control via chrome.tabs |
| Network interception | Full (request/response modification) | Limited (declarativeNetRequest rules only) |
| Debugging | Playwright inspector, trace viewer | Chrome DevTools for extension debugging |

---

## Conclusion

The Extension + Native Messaging approach is the **correct long-term solution** for sites
with aggressive bot detection (Cloudflare Turnstile, Akamai Bot Manager, DataDome, etc.).
It eliminates all CDP/automation markers because it uses the same mechanism as legitimate
browser extensions.

However, it requires a significant engineering investment to reimplement Playwright's
functionality using chrome.* APIs. The current Playwright approach works well for most
sites, and the stealth patches handle basic detection.

**Recommendation:** Keep this as a known option. If Turnstile detection becomes a blocking
issue for critical workflows (LinkedIn, Upwork, etc.), implement the extension approach as
a parallel transport, migrating commands incrementally.

---

## References

- Chrome Native Messaging: https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging
- Chrome Extension APIs: https://developer.chrome.com/docs/extensions/reference/api
- Manifest V3 migration: https://developer.chrome.com/docs/extensions/develop/migrate
- Axiom.ai (extension-based automation): https://axiom.ai
- Browserflow (extension-based automation): https://browserflow.app
