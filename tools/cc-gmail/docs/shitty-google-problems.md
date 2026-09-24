# Shitty Google Problems: cc-gmail OAuth Lessons Learned

Everything we learned the hard way about Google OAuth so nobody has to suffer through this again.

**Setting an account up?** Follow
[connect-a-google-account.md](connect-a-google-account.md) - that is the canonical,
checked path. This file is the long-form notes behind it: why each step exists and
what it looks like when it goes wrong.

## The Core Problem

Google OAuth for Gmail is absurdly complicated for what should be "I just want to read my email." There are multiple consoles, hidden settings, silent failures, and zero useful error messages. This document captures every gotcha we hit.

---

## Lesson 1: Data Access Scopes MUST Exactly Match The Code

This is the #1 thing that will waste your time.

**cc-gmail requests these scopes in `auth.py`:**
```
https://www.googleapis.com/auth/gmail.readonly
https://www.googleapis.com/auth/gmail.send
https://www.googleapis.com/auth/gmail.compose
https://www.googleapis.com/auth/gmail.modify
https://www.googleapis.com/auth/calendar
https://www.googleapis.com/auth/contacts
```

**The Data Access page in Google Cloud Console must have these EXACT same scopes.**

- `https://mail.google.com/` is NOT the same thing as the four granular gmail.* scopes above
- If you register the wrong scopes, Google does NOT show an error
- Google SILENTLY DROPS the mismatched scopes from the consent screen
- You will see calendar and contacts but no Gmail, and have no idea why
- There is no log, no warning, no error message anywhere

**Rule: If the scope string doesn't match character-for-character, it won't work.**

---

## Lesson 2: Google Shows Incremental Consent

If the Google account previously granted some scopes to cc_gmail (even from a different project or client ID), the consent screen will ONLY show the NEW scopes being requested.

**What this looks like:**
- Account previously granted Gmail scopes on Feb 15
- You re-auth requesting Gmail + Calendar + Contacts
- Consent screen only shows Calendar and Contacts
- You think Gmail is broken -- but it was already granted

**This is normal behavior, not a bug.**

**How to verify:** Go to https://myaccount.google.com/connections, find cc_gmail, and it will show ALL granted permissions including the old ones.

**How to get a clean consent screen:** Remove access at myaccount.google.com/connections first, then re-auth.

---

## Lesson 3: The Localhost Redirect Server Must Stay Alive

Google's OAuth flow works like this:
1. cc-gmail starts a temporary HTTP server on `localhost:random_port`
2. Browser opens Google consent screen
3. User clicks Allow
4. Google redirects browser to `localhost:random_port/?code=...`
5. The local server catches the code and completes auth

**If the terminal/batch file window closes before step 5, you get "localhost refused to connect" and auth fails.**

**If you use `--no-browser` and paste the URL into a different browser, the redirect still goes to localhost -- which is fine as long as the original process is still running.**

**Rule: DO NOT close the terminal window until you see "Done!" or "Authentication complete."**

---

## Lesson 4: Always Revoke Old Access Before a Clean Re-Auth

Old grants persist on the Google account. They survive:
- Deleting and recreating the Google Cloud project
- Creating new OAuth client IDs
- Deleting local token.json files

**For a truly clean start:**
1. Go to https://myaccount.google.com/connections
2. Find `cc_gmail` -> Remove all access
3. Delete local `token.json`
4. Then re-authenticate

---

## Lesson 5: Google Cloud Console Layout (as of Feb 2026)

Google reorganized their console. The old "OAuth consent screen" page is now called "Google Auth Platform" with these sections in the left sidebar:

- **Overview** -- Dashboard, "Get started" button for first-time setup
- **Branding** -- App name, logo (what used to be "OAuth consent screen" settings)
- **Audience** -- Internal vs External user type
- **Clients** -- OAuth client IDs (what used to be under "Credentials")
- **Data Access** -- Where you register scopes (THE CRITICAL PAGE)
- **Verification Center** -- For external apps that need Google review
- **Settings** -- Misc settings

The old "Credentials" page still exists under APIs & Services but "Clients" under Google Auth Platform is the newer path to the same thing.

---

## Lesson 6: Workspace (Internal) Apps Are Simpler

For Google Workspace accounts (like @company.com):
- Select **Internal** as user type (not External)
- No Google verification / security audit needed
- No "Test users" list needed -- all org users can use it
- Scopes STILL must be registered on Data Access page
- The Workspace admin CAN block specific APIs (Gmail, Drive) via Admin Console -> Security -> API Controls, but this is separate from the Cloud Console

---

## Lesson 6b: An External App Left In Testing Dies After 7 Days

The trap that breaks every other guide, including earlier versions of this one.

Google's OAuth documentation: *"A Google Cloud Platform project with an OAuth
consent screen configured for an external user type and a publishing status of
'Testing' is issued a refresh token expiring in 7 days"*.

So on a personal Google Account the usual advice - "add yourself as a test user"
- gets you a setup that works perfectly for a week and then dies, with no
warning and a useless error.

**The fix is one click:** https://console.cloud.google.com/auth/audience ->
**Publish app** -> status reads **In production**.

Publishing submits NOTHING to Google for review. The app stays unverified, which
for a personal client is fine: you click through one "Google hasn't verified this
app" screen, and the 100-new-user cap never matters when you are the only user.

Internal (Workspace) apps have no publishing status at all - their Audience page
offers only "Make external" - so none of this applies to them.

---

## Lesson 7: One Google Cloud Project Per Gmail Account

If you have multiple Gmail accounts (personal + work), create a SEPARATE Google Cloud project for each:
- Keeps credentials isolated
- Avoids cross-org permission issues
- Each project gets its own OAuth client ID and credentials.json
- Each account's credentials.json goes in its own folder under the per-user store;
  ask for it with `cc-gmail accounts status <name>` rather than typing a path

---

## The Complete Setup Playbook (Step by Step)

### Prerequisites
- You are the Google Workspace admin (or have admin help)
- You have a Gmail account you want to connect
- You have cc-gmail installed

### Step 1: Create Google Cloud Project
1. Go to https://console.cloud.google.com/
2. Make sure you are signed in as the Gmail account you want to connect
3. Click project dropdown at top -> New Project
4. Name: `cc-gmail`
5. Organization: your org (if Workspace)
6. Click Create, wait for it to finish

### Step 2: Enable APIs
Click each link, make sure your project is selected, click Enable:
1. Gmail API: https://console.cloud.google.com/apis/library/gmail.googleapis.com
2. Calendar API: https://console.cloud.google.com/apis/library/calendar-json.googleapis.com
3. People API: https://console.cloud.google.com/apis/library/people.googleapis.com

### Step 3: Configure OAuth Consent Screen
1. Go to: https://console.cloud.google.com/auth/overview
2. Click "Get started"
3. App Information:
   - App name: `cc-gmail`
   - User support email: your email
   - Click Next
4. Audience:
   - Select **Internal** (for Workspace) or **External** (for personal Gmail)
   - Click Next
5. Contact Information:
   - Your email
   - Click Next
6. Click Create

### Step 4: Register Scopes on Data Access (CRITICAL STEP)
1. Click **"Data Access"** in the left sidebar (under Google Auth Platform)
   - Direct link: https://console.cloud.google.com/auth/scopes
   - The older /auth/data-access link now returns "URL not found" (checked 2026-09-24)
2. Click **"Add or remove scopes"**
3. A scope picker panel opens on the right side
4. Scroll down to the **"Manually add scopes"** box at the bottom of the panel
5. Add each scope ONE AT A TIME. For each scope:
   - Type (or paste) the scope into the text box
   - Check the checkbox when it appears
   - Click **Update**
   - Repeat for the next scope

   Add these 6 scopes in order:
   ```
   gmail.send
   gmail.readonly
   gmail.compose
   gmail.modify
   auth/calendar
   auth/contacts
   ```
   For calendar and contacts, pick the one with the SHORTEST scope
   name (no suffix like .readonly or .events after it).

   **DO NOT add `mail.google.com`** -- that is a different scope and will NOT work

6. After all 6 are added, click **Save**

**You should see 3 sections after saving:**
- Non-sensitive scopes: (none)
- Sensitive scopes: calendar, contacts
- Restricted scopes (Gmail): gmail.readonly, gmail.send, gmail.compose, gmail.modify

### Step 4b: Publish The App (External / personal Gmail only)

1. Go to: https://console.cloud.google.com/auth/audience
2. Click **Publish app** and confirm
3. The status must read **In production**

Skip this and your token dies in 7 days (Lesson 6b). Internal apps have no
Publish button and need nothing here.

### Step 5: Create OAuth Credentials
1. Go to: https://console.cloud.google.com/auth/clients
2. Click **Create Client**
3. Application type: **Desktop app**
4. Name: `cc-gmail`
5. Click Create
6. Click **Download JSON** (the download icon)
7. Save the file -- you'll need it next

### Step 6: Place Credentials File
Save the downloaded JSON as `credentials.json` in the cc-gmail account folder:

| | Path |
|---|---|
| macOS | `~/Library/Application Support/cc-director/config/gmail/accounts/<name>/credentials.json` |
| Windows | `%LOCALAPPDATA%\cc-director\config\gmail\accounts\<name>\credentials.json` |
| Linux | `~/.cc-director/config/gmail/accounts/<name>/credentials.json` |

The store is per operating-system user, not per Director instance. Ask the tool
rather than typing a path: `cc-gmail accounts status <name>` prints the
Account Directory.

If you haven't created the account yet:
```bash
cc-gmail accounts add <name>
# Enter email, type 'oauth' when prompted for password
# It will show you the exact path where to put credentials.json
```

### Step 7: Authenticate
Double-click `cc-gmail_auth.bat` (in the cc-director bin folder) OR run:
```bash
cc-gmail -a <name> auth
```

- Browser opens with Google consent screen
- Make sure you're signed into the correct Google account
- You should see Gmail, Calendar, and Contacts permissions listed
- Click **Allow**
- **DO NOT close the terminal window** -- wait until it says "Done!"

### Step 8: Verify Everything Works
```bash
cc-gmail -a <name> profile         # Shows correct email
cc-gmail -a <name> list            # Inbox messages appear
cc-gmail -a <name> calendar events # Calendar events appear
cc-gmail -a <name> contacts list   # Contacts appear
```

---

## Troubleshooting

### Consent screen missing Gmail scopes
- Check Data Access page has the exact 4 gmail.* scopes (not mail.google.com)
- Revoke old access at myaccount.google.com/connections and re-auth
- Read Lesson 2 above -- it might be incremental consent (Gmail already granted)

### "localhost refused to connect" after clicking Allow
- The terminal/batch file window was closed before auth completed
- Re-run the auth command and DO NOT close the window

### "Insufficient Permission" / 403 errors
- Token was granted without some scopes
- Delete token.json, revoke at myaccount.google.com/connections, re-auth
- Verify Data Access scopes match exactly

### "Calendar API is not enabled" / "People API is not enabled"
- Go to APIs & Services -> Library in Google Cloud Console
- Enable the missing API for your project

### Consent screen says "App not verified"
- For Internal (Workspace) apps: this shouldn't happen
- For External apps: add yourself as a test user under Audience settings

### Auth opens wrong browser
- Use `cc-gmail -a <name> auth --no-browser` to get a URL you can paste into any browser
- The terminal must stay open for the localhost redirect to work
