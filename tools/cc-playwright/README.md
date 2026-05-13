# cc-playwright

Browser automation CLI - trusted-event sibling to cc-browser.

## Why this exists

`cc-browser` uses Chrome's extension `chrome.debugger` API for CDP, which produces events with `isTrusted: false`. React forms that validate `event.isTrusted === true` (Luma signin/registration, most react-hook-form sites, Stripe checkout) silently reject every fill, click, and submit. The result: cc-browser navigates and reads fine, but cannot complete real form workflows.

`cc-playwright` launches Brave with `--remote-debugging-port` and connects via Playwright's `connect_over_cdp()`. CDP events sent this way ARE marked `isTrusted: true` by Chrome.

## Install

```bash
uv tool install --reinstall D:/ReposFred/cc-director/tools/cc-playwright
```

The `cc-playwright` executable is installed to `~/.local/bin/`.

## Usage

```bash
# Launch Brave (once per session)
cc-playwright start

# Navigate, fill, click - all use trusted events
cc-playwright navigate --url https://lu.ma/signin
cc-playwright fill --selector "input[type=email]" --value "you@example.com"
cc-playwright click --text "Continue with Email"

# Dropdown selection
cc-playwright select --selector "select#country" --label "Canada"

# File upload
cc-playwright set-files --selector "input[type=file]" --path "C:\\Users\\you\\resume.pdf"

# Read state
cc-playwright info
cc-playwright snapshot     # all interactive elements + coordinates

# Run JS
cc-playwright evaluate --fn "() => document.title"

# Clean up
cc-playwright stop
```

## Named connections

Multiple Brave instances can run side by side, each pinned to a named connection. The `--connection / -c` flag (or `CC_PLAYWRIGHT_CONNECTION` env var) selects which one. Each connection has its own auto-allocated port, profile directory, and state file.

```bash
cc-playwright --connection linkedin start
cc-playwright --connection linkedin navigate --url https://www.linkedin.com/feed/

cc-playwright --connection upwork start --url https://www.upwork.com/
cc-playwright list                                  # See all known connections
```

The implicit `default` connection uses cc-playwright's own profile at `%LOCALAPPDATA%\cc-playwright\profile`. Any named connection defaults to `%LOCALAPPDATA%\cc-director\connections\<name>`, sharing cookies with cc-browser's connection of the same name.

## Commands

| Command | Purpose |
|---|---|
| `start [--profile] [--port] [--url]` | Launch Brave with auto-allocated debug port |
| `stop` | Kill this connection's Brave instance |
| `status` | Show running state for the current connection |
| `list` | List all connections and their running state |
| `navigate --url URL` | Navigate (waits for domcontentloaded) |
| `click --selector \| --text \| --role` | Trusted click |
| `fill --selector --value` | Set input value (trusted) |
| `type --selector --text [--delay]` | Real keystrokes (for autocomplete-style fields) |
| `press --key KEY [--selector]` | Keyboard press (Enter, Tab, Control+a) |
| `select --selector --value\|--label\|--index` | Dropdown option select |
| `check --selector [--uncheck]` | Checkbox/radio |
| `set-files --selector --path FILE` | Attach files to file input (repeat `--path` for multiple) |
| `evaluate --fn FN` | Run JS, return result |
| `screenshot [--output FILE] [--full-page]` | PNG capture |
| `info` | Current URL, title, viewport |
| `snapshot` | All visible buttons/inputs/links with selectors |
| `wait --selector\|--text\|--networkidle` | Wait for condition |
| `tabs` | List tabs |
| `new-tab [--url]` | Open tab |

Global flag: `--connection / -c <name>` (default: `default`).

## When to use cc-browser vs cc-playwright

| Use cc-browser when | Use cc-playwright when |
|---|---|
| Reading page state | Filling form fields |
| Navigating, clicking simple links | Submitting React forms (Luma, Stripe, etc.) |
| Reusing your real Brave session (cookies, history) | Need trusted-event input |
| Sites that detect Playwright (rare on real React apps) | Auth flows (signin, OTP) |

You can run both simultaneously - they use different Brave instances. Named cc-playwright connections share cookies with the matching cc-browser connection through the shared profile directory.

## Known limitations

- Each command spawns a fresh Playwright connection (~100-200ms overhead). For tight loops, write a Python script that connects via `playwright.chromium.connect_over_cdp(f"http://localhost:{port}")` using the port from `state/<name>.json`.
- The default profile is separate from your real Brave; sign in once per site. Named connections sharing cc-director profiles inherit cc-browser's existing cookies.
- Custom-styled radios/checkboxes that hide the native input element may fail Playwright's actionability checks. Click the visible label (`label[for="..."]`) instead.
- Some sites with extreme bot detection (LinkedIn, Reddit) still detect Playwright via `navigator.webdriver` and behavioral fingerprinting. Use cc-browser with native messaging there.

## Verified working

- Luma signin (OTP flow with cc-outlook reading the code from a mindzie inbox)
- Luma event registration with multi-field approval gates, custom dropdowns, multi-select pickers, and Stripe payment intent capture
- Eventbrite multi-day ticket selection + post-checkout attendee form
- Pardot embedded marketing-form iframes
