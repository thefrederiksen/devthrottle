# Connect a Google account to cc-gmail

This is the whole path from "nothing set up" to a working mailbox, calendar and
contacts on the command line. Follow it top to bottom and you will not need to
ask anyone a question.

**There is no DevThrottle Google app, and there never will be.** You create your
own Google Cloud project and your own OAuth client, in your own Google account.
Nobody else holds a credential to your mail. The cost of that choice is the
manual steps below; the benefit is that your mailbox is reachable only by you.

If you would rather be walked through it a page at a time, the command does the
same walk and proves the result:

```bash
cc-gmail setup personal
```

---

## First: do you even need this?

cc-gmail has two ways in. Pick before you spend an hour in the Google Cloud
console.

| | **App Password** | **OAuth** |
|---|---|---|
| Time to set up | 2 minutes | 30-45 minutes, once |
| Google Cloud project | not needed | required |
| Read, send, search, labels, drafts | yes (IMAP/SMTP) | yes |
| **Calendar** | **no** | yes |
| **Contacts** | **no** | yes |
| `--json` output for drafts and messages | no | yes |
| Works if your admin blocked IMAP | no | yes |

**Use an App Password** if you only want email and IMAP is available to you.
It is documented in the [README](../README.md#app-password-setup-quick-setup)
and it really does take two minutes.

**Use OAuth** — this document — if you want Calendar, Contacts, machine-readable
output, or your organisation has switched IMAP off.

---

## Second: which fork are you on?

Everything below has two shapes, and the difference decides whether your setup
keeps working next week.

| | **Google Workspace** (you sign in as you@yourcompany.com) | **Personal Google Account** (@gmail.com, or any address that is not Google-hosted) |
|---|---|---|
| Consent screen user type | **Internal** | **External** |
| "Google hasn't verified this app" screen | never shown | shown once, you click through it |
| 100-user cap | does not apply | applies, and you are user 1 of 100 |
| **"Publish app" step** | **does not exist** | **required, and skipping it breaks you in 7 days** |
| Google verification / CASA assessment | not needed | not needed for your own use |

Not sure which you are? If your email address's domain has its mail hosted by
Google you are on Workspace. `cc-gmail setup` works this out for you and says so
before it does anything.

Both forks work. Workspace is simply fewer steps.

---

## The two halves

Keep these apart in your head, because they repeat at different rates.

**Once per Google account** (steps 1-6): the Google Cloud project, the APIs, the
consent screen, publishing, and the OAuth client. Do this once, ever, for each
mailbox you want to reach.

**Once per computer** (steps 7-8): put the downloaded client JSON in place,
authorise, verify. Do this again on every machine you want to use.

**The downloaded client JSON is the only file that ever moves between machines.**
Copy it to the second computer, run step 7 there, and you are done. Do not copy
`token.json` — it is bound to the machine's authorisation and a fresh one costs
you one browser click.

> **Google shows you the client secret exactly once.** The "OAuth client created"
> dialog says so in as many words: *"You will no longer be able to view or
> download the client secret once you close this dialog."* Download the JSON
> while that dialog is open. If you lose it, you do not recover it — you delete
> the client and create another one.

---

# Once per Google account

## Step 1 — Create a Google Cloud project

https://console.cloud.google.com/projectcreate

Sign in as the Google account whose mail you want to read. Name the project
`cc-gmail`. On Workspace, leave the organisation as your company.

**One project per Google account.** If you have a personal mailbox and a work
mailbox, that is two projects. Mixing them causes cross-organisation permission
problems that are very hard to read backwards from the error.

## Step 2 — Enable the three APIs

cc-gmail calls three Google APIs and each must be switched on for your project.
Open each link, check your new project is selected in the bar at the top, and
click **Enable**.

- Gmail API — https://console.cloud.google.com/apis/library/gmail.googleapis.com
- Google Calendar API — https://console.cloud.google.com/apis/library/calendar-json.googleapis.com
- Google People API — https://console.cloud.google.com/apis/library/people.googleapis.com

Skipping Calendar or People does not break email; it makes `cc-gmail calendar`
and `cc-gmail contacts` fail with "API is not enabled", and you come back here.

## Step 3 — Configure the consent screen

https://console.cloud.google.com/auth/overview

Click **Get started**, then:

- **App name:** `cc-gmail`
- **User support email:** your own address
- **Audience:** **Internal** if you are on Workspace, **External** if you are on
  a personal Google Account. A personal account has no Google Cloud
  organisation, so the console will only offer you External.
- **Contact information:** your own address

This page used to be called "OAuth consent screen" and lived under APIs &
Services. It is now **Google Auth Platform**, and the left sidebar reads:
Overview, Branding, Audience, Clients, Data Access, Verification Center,
Settings.

## Step 4 — Register the scopes on Data Access

**Direct link: https://console.cloud.google.com/auth/scopes**

(The sidebar calls this page "Data Access". Older guides link
`/auth/data-access`, which now returns "URL not found".)

Click **Add or remove scopes**. A panel opens on the right. Scroll to the bottom
of it, to the box headed **"Manually add scopes"**. Add these six, one at a
time — type it, tick the checkbox that appears, click **Update**:

```
https://www.googleapis.com/auth/gmail.readonly
https://www.googleapis.com/auth/gmail.send
https://www.googleapis.com/auth/gmail.compose
https://www.googleapis.com/auth/gmail.modify
https://www.googleapis.com/auth/calendar
https://www.googleapis.com/auth/contacts
```

When all six are listed, click **Save**.

Afterwards the page groups them for you, and this is what a correct setup looks
like:

- **Non-sensitive scopes:** none
- **Sensitive scopes:** `.../auth/calendar`, `.../auth/gmail.send`, `.../auth/contacts`
- **Restricted scopes (Gmail):** `.../auth/gmail.modify`, `.../auth/gmail.compose`, `.../auth/gmail.readonly`

Two things that will cost you an hour if you get them wrong:

- **Do NOT add `https://mail.google.com/`.** It is a broader, different scope.
  cc-gmail asks for the four granular `gmail.*` scopes and they must match
  character for character.
- **Google drops scopes it does not recognise, silently.** No error, no warning,
  no log. The consent screen simply shows fewer permissions than you asked for,
  your token comes back short, and every Gmail call fails with "Insufficient
  Permission" for no visible reason.

## Step 5 — Publish the app

### If you are on Workspace (Internal): skip this. There is nothing to do.

An Internal app has no publishing status at all. Its Audience page shows only
"User type: Internal" and a **Make external** button — no Testing, no Publish,
no test-user list. Go to step 6.

### If you are on a personal Google Account (External): **DO NOT SKIP THIS.**

> ## Click "Publish app". If you don't, everything you set up today stops working in 7 days.
>
> https://console.cloud.google.com/auth/audience → **Publish app** → confirm.
> The status must then read **In production**.

Here is what happens if you skip it, because it is worth understanding rather
than just obeying. Google's OAuth documentation says:

> "A Google Cloud Platform project with an OAuth consent screen configured for
> an external user type and a publishing status of 'Testing' is issued a refresh
> token expiring in 7 days"

So an unpublished app works perfectly today. A week later, with nothing changed
and no warning, every cc-gmail command starts failing on a dead refresh token.
That is the single most common way this setup goes wrong.

**Publishing does not submit anything to Google for review.** It does not start
verification, it does not cost anything, and it does not involve a security
assessment. Your app stays *unverified and in production*, which is a perfectly
ordinary state for a personal client. What you get is:

- one **"Google hasn't verified this app"** screen, the first time you authorise.
  Click **Advanced**, then **"Go to cc-gmail (unsafe)"**. It is your own app,
  built by you, reaching your own mailbox.
- a cap of 100 new users for the lifetime of the project. You are user number
  one, so this will never affect you.

Google's own "when is verification not needed" guidance covers exactly this
case: *"If the app is for your personal use (fewer than 100 users), you and your
limited number of users can continue using the app without going through
verification."*

## Step 6 — Create the OAuth client and download it

https://console.cloud.google.com/auth/clients

**Create client** → Application type: **Desktop app** → name it `cc-gmail` →
**Create**.

A dialog appears showing the client ID and secret. Click **Download JSON** while
it is open. Keep the file safe for a moment — it is about to move somewhere
better.

**It must be a Desktop app client.** If you pick "Web application", the file you
download has a `web` key instead of an `installed` key, and authorising fails
with `redirect_uri_mismatch`. `cc-gmail setup` spots this and tells you; on the
manual path you find out at step 8.

---

# Once per computer

## Step 7 — Put the client JSON where cc-gmail reads it

Create the account, which also tells you the exact path:

```bash
cc-gmail accounts add work
# Email address: you@yourcompany.com
# App Password (or 'oauth' for advanced setup): oauth
```

Then move the downloaded file there, renaming it to `credentials.json`:

| | Path |
|---|---|
| macOS | `~/Library/Application Support/cc-director/config/gmail/accounts/<name>/credentials.json` |
| Windows | `%LOCALAPPDATA%\cc-director\config\gmail\accounts\<name>\credentials.json` |
| Linux | `~/.cc-director/config/gmail/accounts/<name>/credentials.json` |

Never type these out from memory — ask the tool, which resolves them in code:

```bash
cc-gmail accounts status work     # the "Account Directory" row is the answer
```

That store is **per operating-system user, not per Director instance**. The same
file serves a plain terminal and a Director session, which is why authenticating
in one fixes the other.

Or let the command do it — it watches your downloads folder, recognises the file
by its contents rather than its name, and installs it with owner-only
permissions:

```bash
cc-gmail setup work
```

Once the file is in the account directory, **delete the copy in your downloads
folder**. It holds the client secret. (`cc-gmail setup` offers to do this.)

## Step 8 — Authorise, and check it actually works

```bash
cc-gmail --account work auth
```

A browser opens on Google's consent screen.

- Sign in as the right account. If you are signed into several, pick carefully.
- On a personal account you will see "Google hasn't verified this app" first →
  **Advanced** → **Go to cc-gmail (unsafe)**.
- You should see all six permissions listed. If Gmail is missing, go back to
  step 4.
- Click **Allow**.
- **Do not close the terminal** until it reports success. cc-gmail is running a
  one-shot web server on `localhost` to catch Google's redirect; close the window
  early and you get "localhost refused to connect".

Now prove it. An absence of errors is not proof — make Google answer:

```bash
cc-gmail --account work profile          # your address, your message count
cc-gmail --account work labels           # INBOX, SENT, and your own labels
cc-gmail --account work calendar events  # upcoming events
cc-gmail --account work contacts list    # contacts
```

`cc-gmail setup` runs these for you and refuses to report success until Gmail
returns a real profile and a non-empty label list.

---

## What Google actually promises about token lifetime

Two claims in this document decide whether it is worth following. Both were
checked against Google's own documentation on 2026-09-24, and one of them is
weaker than you would like.

### An unverified but published External app keeps a long-lived refresh token — confirmed

Google's [OAuth 2.0 for Mobile & Desktop Apps](https://developers.google.com/identity/protocols/oauth2)
page lists every reason a refresh token stops working. The only one about
publishing status reads:

> "A Google Cloud Platform project with an OAuth consent screen configured for
> an **external user type** and a **publishing status of 'Testing'** is issued a
> refresh token expiring in 7 days"

Both conditions must hold. Publish the app and the second one stops being true,
and nothing else in that list mentions verification or production status.

Two things on the same page that have nothing to do with publishing and will
still bite you:

- **"The user changed passwords and the refresh token contains Gmail scopes."**
  Change your Google password and you must run `cc-gmail auth --force` again.
  This is unavoidable and applies to every fork.
- There is a limit of **100 refresh tokens per Google Account per OAuth client
  ID**. Re-authorising the same account on the same client a hundred times
  quietly invalidates the oldest. You will not reach this by accident.

**Where the claim is weaker than it looks:** Google's
[OAuth 2.0 policy page](https://developers.google.com/identity/protocols/oauth2/production-readiness/policy-compliance)
says restricted scopes "can't be used in production apps without review", and
the verification FAQ warns that an unverified app can exhaust its 100-user cap
and have sign-in disabled. Read strictly, that is in tension with running an
unverified production app on Gmail scopes at all. Read against Google's
operational guidance — which names personal use under 100 users as a case where
verification is not needed — it is fine, and it is what thousands of personal
clients do. **We have not run a published External personal app for more than
seven days to watch the token survive.** If yours dies anyway, that is a finding
worth reporting back, not something you did wrong.

### A Workspace Internal app skips verification, the cap, and the 7-day expiry — confirmed

Google's [when is verification not needed](https://support.google.com/cloud/answer/13464323)
page states that where *"the app is only used by people in your Google Workspace
or Cloud Identity organization. The project must be owned by the organization,
and its OAuth Consent Screen must be configured for internal use"*, the app is
not subject to the unverified app screen or the 100-user cap. The
[unverified apps](https://support.google.com/cloud/answer/7454865) page says the
same: an internal app in a Cloud Organization does not go through verification.

The 7-day expiry is the part Google never states directly, so here is the
reasoning in full: the rule is written against "an external user type and a
publishing status of Testing". An Internal app is not the External user type,
and it has no publishing status — its Audience page offers only "Make external".
The rule therefore cannot apply to it. That is an argument from the wording, not
a sentence in Google's documentation saying "Internal apps are exempt".

**A caveat we could not resolve.** Google's
[restricted scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification)
page can be read as saying that restricted or sensitive scopes require app
verification *even for internal use*, which contradicts the Cloud Console Help
pages above. In practice the Console Help pages describe what the console
actually does, and an Internal app authorises Gmail scopes with no verification
and no warning screen — we did exactly that while writing this. Either way it
does not reach DevThrottle: DevThrottle publishes no Google app, so no
DevThrottle project is ever submitted for verification or for a CASA security
assessment.

---

## What we proved, and what we didn't

This document was walked end to end on macOS on 2026-09-24, on the **Workspace
Internal** fork, with `cc-gmail setup`: a real Desktop client created in the
console, downloaded, installed, authorised, and verified with live Gmail,
Calendar and People calls.

**The personal-Gmail (External) fork was not walked.** No throwaway personal
Google account was available, so steps 5 and 8 on that side — the Publish app
click and the "Google hasn't verified this app" screen — are written from
Google's documentation and from the console's own behaviour, not from a run.
If you are the first to walk that fork and it differs, please say so.

---

## When it goes wrong

### "Insufficient Permission" on every Gmail call

The scopes on Data Access do not match what cc-gmail asks for. Google dropped
the mismatched ones silently. Redo step 4, checking character for character,
then `cc-gmail --account <name> auth --force`.

### The consent screen is missing Gmail

Either step 4 again, or **incremental consent**: if this Google account already
granted Gmail to a client called `cc-gmail`, Google only shows you the *new*
permissions. Check https://myaccount.google.com/connections — it lists everything
actually granted. To force a clean consent screen, remove the app's access there
first.

### "localhost refused to connect" after clicking Allow

The terminal was closed before Google redirected back. Re-run
`cc-gmail --account <name> auth` and leave the window alone.

### "redirect_uri_mismatch"

The OAuth client is a Web application, not a Desktop app. Create a new Desktop
app client (step 6). You can tell from the file: a Desktop client's JSON has an
`installed` key, a web one has `web`.

### Everything worked and then stopped about a week later

You are on a personal Google Account and the app is still in **Testing**. This
is step 5. Publish it, then `cc-gmail --account <name> auth --force`.

### Everything worked and then stopped right after you changed your Google password

Expected: a refresh token carrying Gmail scopes is invalidated by a password
change. `cc-gmail --account <name> auth --force`.

### "Gmail API has not been used in project ... before or it is disabled"

Step 2. The same message shape appears for Calendar and People.

### Workspace: your admin has blocked the API

Separate from everything above. Admin Console → Security → API Controls can
block Gmail or Drive access for third-party apps regardless of what your Cloud
project says. Ask your admin.

---

## See also

- [README](../README.md) — the App Password path, and every command
- [shitty-google-problems.md](shitty-google-problems.md) — the long-form notes
  on each trap, written while hitting them
