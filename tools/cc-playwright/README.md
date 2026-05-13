# cc-playwright

Browser automation CLI - trusted-event sibling to cc-browser.

## Why this exists

`cc-browser` uses Chrome's extension `chrome.debugger` API for CDP, which produces events with `isTrusted: false`. React forms that validate `event.isTrusted === true` (Luma signin/registration, most React-Hook-Form sites, Stripe checkout) silently reject every fill, click, and submit. The result: cc-browser navigates and reads fine, but cannot complete real form workflows.

`cc-playwright` launches Brave with `--remote-debugging-port` and connects via Playwright's `connect_over_cdp()`. CDP events sent this way ARE marked `isTrusted: true` by Chrome. Verified working on Luma signin OTP flow, multi-field registration forms with approval gates, and standard React forms.

## Install

```bash
uv tool install --reinstall D:/ReposFred/cc-playwright
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

# Read state
cc-playwright info
cc-playwright snapshot     # all interactive elements + coordinates

# Run JS
cc-playwright evaluate --fn "() => document.title"

# Clean up
cc-playwright stop
```

## Profile

cc-playwright launches its own Brave with profile dir at `%LOCALAPPDATA%\cc-playwright\profile`. Separate from your real Brave so you can use both at once. Sign in once per site - sessions persist across runs.

## Commands

| Command | Purpose |
|---|---|
| `start` | Launch Brave with debug port |
| `stop` | Kill the Brave instance |
| `status` | Show running state |
| `navigate --url URL` | Navigate (waits for domcontentloaded) |
| `click --selector \| --text \| --role` | Trusted click |
| `fill --selector --value` | Set input value (trusted) |
| `type --selector --text` | Real keystrokes with delay (for autocomplete-style fields) |
| `press --key KEY [--selector]` | Keyboard press (Enter, Tab, Control+a) |
| `select --selector --value\|--label\|--index` | Dropdown option select |
| `check --selector [--uncheck]` | Checkbox/radio |
| `evaluate --fn FN` | Run JS, return result |
| `screenshot [--output FILE] [--full-page]` | PNG capture |
| `info` | Current URL, title, viewport |
| `snapshot` | All visible buttons/inputs/links with selectors |
| `wait --selector\|--text\|--networkidle` | Wait for condition |
| `tabs` | List tabs |
| `new-tab [--url]` | Open tab |

## When to use cc-browser vs cc-playwright

| Use cc-browser when | Use cc-playwright when |
|---|---|
| Reading page state | Filling form fields |
| Navigating, clicking simple links | Submitting React forms (Luma, Stripe, etc.) |
| Reusing your real Brave session (cookies, history) | Need trusted-event input |
| Sites that detect Playwright (rare on real React apps) | Auth flows (signin, OTP) |

You can run both simultaneously - they use different Brave instances.

## Known limitations

- Each command spawns a fresh Playwright connection (~100-200ms overhead). For tight loops, write a Python script that holds the connection.
- Profile is separate from your real Brave; you sign in once per site. No automated cookie import yet.
- Some sites with extreme bot detection (LinkedIn, Reddit) still detect Playwright via `navigator.webdriver` and behavioral fingerprinting. Use cc-browser with native messaging there.

## Verified working

- Luma signin (OTP flow)
- Luma event registration (9-field form with approval gate) - tested 2026-05-13 on `luma.com/gearup`, result: "Pending Approval"
