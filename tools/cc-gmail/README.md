# cc-gmail

Gmail CLI: read, send, search, and manage emails from the command line.

Supports **multiple Gmail accounts** with easy switching between them.

## Quick Start (App Password -- 2 minutes)

Most users should use this method. No Google Cloud project needed.

```bash
# 1. Add your account
cc-gmail accounts add personal

# 2. Enter your email and app password (interactive)
# 3. Done! Start using Gmail
cc-gmail list
```

The interactive setup will guide you through:
1. Entering your Gmail address
2. Creating an App Password (link provided)
3. Testing IMAP/SMTP connectivity

---

## App Password Setup (Quick Setup)

### Prerequisites

- A Gmail account with **2-Step Verification** enabled
- That's it. No Google Cloud project, no API keys.

### Step 1: Enable 2-Step Verification

If you haven't already:
1. Go to https://myaccount.google.com/security
2. Under "How you sign in to Google", click **2-Step Verification**
3. Follow the prompts to enable it

### Step 2: Create an App Password

1. Go to https://myaccount.google.com/apppasswords
2. Enter a name: `cc-gmail`
3. Click **Create**
4. Copy the 16-character password shown (e.g., `abcd efgh ijkl mnop`)

### Step 3: Add the Account

```bash
cc-gmail accounts add personal
```

When prompted:
- Enter your email address
- Paste the app password (spaces are stripped automatically)
- cc-gmail tests the connection and confirms it works

```
Email address: you@gmail.com

-- Quick Setup (App Password) --
App Password (or 'oauth' for advanced setup): ****************

Testing connection...
  [OK] IMAP login successful (imap.gmail.com)
  [OK] SMTP login successful (smtp.gmail.com)

Account 'personal' ready! Auth method: app_password
```

### Google Workspace Accounts

App Passwords work with Workspace accounts too, but your admin must enable:
- **2-Step Verification** for users
- **IMAP access** (Admin Console -> Apps -> Google Workspace -> Gmail -> User access)
- **Less secure app access** or **App passwords** option

If your admin has disabled IMAP, use the [OAuth Setup](#oauth-setup-advanced) instead.

---

## OAuth Setup (Advanced)

Use this if:
- Your organization blocks IMAP/App Passwords
- You want Calendar or Contacts access

### When to Use OAuth

When you run `cc-gmail accounts add`, if App Password authentication fails
with a connection error, it usually means IMAP is blocked. The error message
will guide you to OAuth setup.

### Multi-Account Guidance

**Create a separate Google Cloud project for each Gmail account.** This keeps
credentials isolated and avoids cross-org permission issues. For example, if
you have both a personal Gmail and a Workspace account, each should have its
own Google Cloud project with its own OAuth credentials.

### OAuth Setup Steps

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. **Create a new project** (one project per Gmail account is recommended)
   - Name it `cc-gmail` or similar
   - If using a Workspace account, create it under your organization
3. Enable these APIs (click each link, select your project, click Enable):
   - [Gmail API](https://console.cloud.google.com/apis/library/gmail.googleapis.com) (required for email)
   - [Google Calendar API](https://console.cloud.google.com/apis/library/calendar-json.googleapis.com) (for calendar commands)
   - [People API](https://console.cloud.google.com/apis/library/people.googleapis.com) (for contacts commands)
4. Set up OAuth consent screen:
   - Go to [OAuth consent screen](https://console.cloud.google.com/apis/credentials/consent)
   - Select **External** user type (or **Internal** for Workspace)
   - App name: `cc-gmail`, add your email
   - Under **Test users**, add your Gmail address
5. **Register scopes on the Data Access page** (critical step):
   - Click **Data Access** in the left sidebar
   - Click **Add or remove scopes**
   - Scroll to **"Manually add scopes"** at the bottom of the panel
   - Add each scope one at a time: type it, check the box, click **Update**
   - Add these 6 scopes:
     - `gmail.send`
     - `gmail.readonly`
     - `gmail.compose`
     - `gmail.modify`
     - `auth/calendar` (pick the shortest match, no suffix)
     - `auth/contacts` (pick the shortest match, no suffix)
   - After all 6 are added, click **Save**
   - Without this step, Google silently drops unregistered scopes from the
     consent screen, leading to "Insufficient Permission" errors
   - IMPORTANT: Do NOT add `mail.google.com` -- that is a different scope
6. Create OAuth credentials:
   - Go to [Credentials](https://console.cloud.google.com/apis/credentials)
   - **Create Credentials** -> **OAuth client ID** -> **Desktop app**
   - Download the JSON file
7. Set up the account:

```bash
# During account add, type 'oauth' when prompted for app password
cc-gmail accounts add work
# Email: you@company.com
# App Password: oauth

# Place credentials.json in the account folder
# (path shown during setup)

# Authenticate
cc-gmail -a work auth

# If authenticating on a machine without a browser (e.g., remote server),
# or if you need to use a specific browser profile:
cc-gmail -a work auth --no-browser
```

Or switch an existing account to OAuth:

```bash
cc-gmail -a work auth --method oauth
```

---

## What Works With Each Auth Method

| Command | App Password | OAuth |
|---------|-------------|-------|
| send | YES (SMTP) | YES |
| list, read | YES (IMAP) | YES |
| search | YES (GIMAP) | YES |
| labels | YES (IMAP) | YES |
| draft | YES (IMAP) | YES |
| reply | YES (IMAP+SMTP) | YES |
| delete, archive | YES (IMAP) | YES |
| attachments | YES | YES |
| stats | YES | YES |
| calendar | NO | YES |
| contacts | NO | YES |

---

## Credential Storage

**App Passwords** are stored in your OS credential manager:
- Windows: Windows Credential Manager
- macOS: Keychain
- Linux: Secret Service (or encrypted file fallback)

App passwords are **never** stored in plain text config files.

**OAuth tokens** are stored as `token.json` in the account directory, which lives
under `%LOCALAPPDATA%\cc-director\config\gmail\accounts\<account>\` - one store per
operating-system user, whether cc-gmail runs in a terminal or inside a Director
session (see Configuration below).

When Google refuses to renew a token, the refusal is written down beside it as
`token-refresh-error.json`, so `accounts list` reports `Re-auth needed` with the
reason instead of calling the account `Ready` because a file exists.

**Account config** (`config.json` per account) stores only:
- Email address
- Auth method (`app_password` or `oauth`)
- No secrets

---

## Managing Multiple Accounts

```bash
# Add accounts
cc-gmail accounts add personal --default
cc-gmail accounts add work

# List accounts
cc-gmail accounts list

# Switch default
cc-gmail accounts default work

# Use specific account
cc-gmail -a personal list
cc-gmail -a work search "from:boss"

# Check status
cc-gmail accounts status personal

# Remove account
cc-gmail accounts remove old-account
```

---

## Commands

### Authentication

```bash
# Authenticate current/default account
cc-gmail auth

# Force re-authentication
cc-gmail auth --force

# Revoke token / delete app password
cc-gmail auth --revoke

# Switch auth method
cc-gmail auth --method oauth
cc-gmail auth --method app_password
```

### List Emails

```bash
cc-gmail list                     # List inbox (default)
cc-gmail list -n 20               # Show 20 messages
cc-gmail list --unread            # Unread only
cc-gmail list -l SENT             # Sent messages
cc-gmail -a work list             # From specific account
```

### Read Email

```bash
cc-gmail read <message_id>
cc-gmail read <message_id> --raw  # Raw message data
```

### Send Email

```bash
cc-gmail send -t "to@example.com" -s "Subject" -b "Body text"
cc-gmail send -t "to@example.com" -s "Subject" -f body.txt
cc-gmail send -t "to@example.com" -s "Subject" -b "Body" --cc "cc@example.com"
cc-gmail send -t "to@example.com" -s "Subject" -f email.html --html
cc-gmail send -t "to@example.com" -s "Subject" -b "See attached" --attach file.pdf
```

### Drafts

```bash
cc-gmail draft -t "to@example.com" -s "Subject" -b "Draft body"
cc-gmail reply <message_id> -b "Reply text"
cc-gmail reply <message_id> --all -b "Reply all text"
cc-gmail drafts                   # List drafts
```

### Search

```bash
cc-gmail search "from:someone@example.com"
cc-gmail search "subject:important" -n 20
cc-gmail search "is:unread"
cc-gmail search "has:attachment"
cc-gmail search "after:2024/01/01 before:2024/02/01"
```

### JSON Output (for scripts)

`--json` prints machine-readable JSON instead of the table-like text. The plain
output without `--json` is unchanged.

```bash
cc-gmail search "from:someone@example.com" --json   # [{id, thread_id, from, to, subject, date, labels}, ...]
cc-gmail list --json                                # same array shape as search
cc-gmail read <message_id> --json                   # one object: the same fields plus body
cc-gmail draft -t "to@example.com" -s "Subject" -b "Body" --json   # {draft_id, message_id, thread_id}
cc-gmail reply <message_id> -b "Reply text" --json               # {draft_id, message_id, thread_id}
```

- Header values are the raw headers (not truncated); a missing header is `""`, never `null`.
  Non-ASCII is escaped (`\u00e9`), so the output is ASCII.
- An empty result is `[]`.
- On failure the exit code is non-zero, the message is on stderr, and stdout is empty.
- `read` marks the message as read, with or without `--json`.
- `draft --json` and `reply --json` need an OAuth account: an App Password (IMAP)
  account cannot return Gmail draft, message and thread ids, so the command
  refuses before creating anything.
- `reply --json` is drafts only: combined with `--send` it is refused and nothing is sent.

### Labels

```bash
cc-gmail labels                   # List all labels
cc-gmail label-stats INBOX        # Label statistics
cc-gmail label-create "My Label"  # Create label
cc-gmail move <id> -l "My Label"  # Move to label
```

### Delete / Archive

```bash
cc-gmail delete <message_id>              # Move to trash
cc-gmail delete <message_id> --permanent  # Permanently delete
cc-gmail archive <message_id>             # Archive (remove from inbox)
cc-gmail archive-before 2024-01-01        # Archive old messages
cc-gmail archive-before 2024-01-01 --dry-run  # Preview what would be archived
```

### Statistics

```bash
cc-gmail profile                  # User profile
cc-gmail stats                    # Mailbox dashboard
cc-gmail stats -l                 # Include user labels
cc-gmail count "is:unread"        # Count messages
```

### Calendar (OAuth only)

```bash
cc-gmail calendar list                             # List all calendars
cc-gmail calendar events                           # Upcoming events (7 days)
cc-gmail calendar events -d 14                     # Next 14 days
cc-gmail calendar events -c "work@group.v.calendar.google.com"  # Specific calendar
cc-gmail calendar today                            # Today's events
cc-gmail calendar get <event_id>                   # Event details
cc-gmail calendar create -s "Team Meeting" -d 2026-03-15 -t 14:00 --duration 60
cc-gmail calendar create -s "All Hands" -d 2026-03-20 --all-day
cc-gmail calendar create -s "Review" -d 2026-03-15 -t 10:00 --attendees "a@co.com,b@co.com"
cc-gmail calendar delete <event_id>                # Delete (with confirmation)
cc-gmail calendar delete <event_id> -y             # Delete (skip confirmation)
```

Requires OAuth authentication. App Password users will see guidance on how to switch.

### Contacts (OAuth only)

```bash
cc-gmail contacts list                             # List contacts (25 default)
cc-gmail contacts list -n 50                       # List 50 contacts
cc-gmail contacts search "John"                    # Search by name or email
cc-gmail contacts get people/c1234567890           # Full contact details
cc-gmail contacts create --name "John Doe" --email "john@example.com" --phone "+1-555-0100"
cc-gmail contacts create --name "Jane Smith" --org "Acme Corp"
cc-gmail contacts delete people/c1234567890        # Delete (with confirmation)
cc-gmail contacts delete people/c1234567890 -y     # Delete (skip confirmation)
```

Requires OAuth authentication. App Password users will see guidance on how to switch.

---

## Gmail Search Syntax

| Query | Description |
|-------|-------------|
| `from:email` | Messages from sender |
| `to:email` | Messages to recipient |
| `subject:word` | Subject contains word |
| `is:unread` | Unread messages |
| `is:starred` | Starred messages |
| `has:attachment` | Messages with attachments |
| `after:YYYY/MM/DD` | Messages after date |
| `before:YYYY/MM/DD` | Messages before date |
| `label:name` | Messages with label |
| `in:inbox` | Messages in inbox |
| `in:sent` | Sent messages |
| `category:updates` | Category filter |

Combine queries: `from:boss@company.com subject:report after:2024/01/01`

---

## Troubleshooting

### App Password: "Authentication failed"

- Make sure 2-Step Verification is enabled
- Create a fresh app password at https://myaccount.google.com/apppasswords
- Enter the 16-character password without spaces
- If the app passwords page is not available, your Workspace admin may have disabled it

### App Password: "Connection refused" / IMAP blocked

Your organization likely blocks IMAP access. Options:
1. Ask your Workspace admin to enable IMAP
2. Use OAuth instead: `cc-gmail auth --method oauth`

### OAuth: "Gmail API has not been used in project"

Enable the Gmail API:
1. Go to https://console.cloud.google.com/apis/library/gmail.googleapis.com
2. Select your project
3. Click **Enable**

### OAuth: "redirect_uri_mismatch"

Wrong OAuth client type. Create a new one with type **Desktop app** (not Web application).

### OAuth: "App not verified"

Add yourself as a test user:
1. Go to https://console.cloud.google.com/apis/credentials/consent
2. Under "Test users", add your Gmail address

### Calendar: "Calendar API is not enabled"

Enable the Google Calendar API:
1. Go to https://console.cloud.google.com/apis/library/calendar-json.googleapis.com
2. Select your project
3. Click **Enable**

### Contacts: "People API is not enabled"

Enable the People API:
1. Go to https://console.cloud.google.com/apis/library/people.googleapis.com
2. Select your project
3. Click **Enable**

### OAuth: "Insufficient Permission" / Consent screen missing scopes

Google silently drops scopes that are not registered on the project's Data
Access page. The consent screen appears to work, but the token is granted
fewer permissions than requested.

Fix:
1. Go to [Data Access](https://console.cloud.google.com/auth/data-access)
2. Click **Add or remove scopes**
3. Add exactly these scopes (they must match what cc-gmail requests):
   - `https://www.googleapis.com/auth/gmail.readonly`
   - `https://www.googleapis.com/auth/gmail.send`
   - `https://www.googleapis.com/auth/gmail.compose`
   - `https://www.googleapis.com/auth/gmail.modify`
   - `https://www.googleapis.com/auth/calendar`
   - `https://www.googleapis.com/auth/contacts`
4. Click **Update** then **Save**
5. Re-authenticate: `cc-gmail auth --force`

IMPORTANT: Do NOT register `https://mail.google.com/` -- that is a different
(broader) scope. cc-gmail uses the granular `gmail.*` scopes and they must
match exactly, or Google silently drops them from the consent screen.

### Calendar/Contacts: "Token missing permissions"

Your OAuth token was created before calendar/contacts support was added.
Re-authorize to get the new permissions:
```bash
cc-gmail auth --force
```

---

## Configuration

```
%LOCALAPPDATA%\cc-director\config\gmail\
    config.json              # Default account setting
    accounts/
        personal/
            config.json      # Email + auth method (no secrets)
            credentials.json # OAuth only: client credentials
            token.json       # OAuth only: access token
            token-refresh-error.json  # Written when Google refuses a refresh
        work/
            config.json
```

On Windows, this is typically: `C:\Users\<you>\AppData\Local\cc-director\config\gmail\`

**One store per operating-system user.** This path does NOT move with
`CC_DIRECTOR_ROOT`, which a Director sets to its instance home for every session it
runs. So `cc-gmail auth` in a plain terminal and `cc-gmail` inside a Director session
read and write the same token: authenticating once fixes both. It was not always so -
until issue #3011 there were two stores, and the tool told the user to run an `auth`
that could not reach the store it was reading.

An account whose setup exists only in an older per-instance store is copied into this
one the first time cc-gmail runs, and the copy is announced. The older files are left
where they are.

App passwords are stored separately in your OS credential manager (not in files).

---

## Building Executable

```powershell
.\build.ps1
```

Creates `dist/cc-gmail.exe`.

---

## Installation (Development)

```bash
cd src/cc-gmail
pip install -e .
```

---

## License

MIT
