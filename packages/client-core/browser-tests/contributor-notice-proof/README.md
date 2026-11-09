# Contributor email choice proof (Team implementation piece 4.4)

Drives the REAL `ContributorNoticeCard` - the one card both the Cockpit and the phone show on their Notifications tab -
in a real browser against a fake Gateway, and screenshots it at desktop and phone width. Run it from this directory:

```
PW_CHANNEL=msedge node run-proof.mjs
PW_CHANNEL=msedge node shoot-email.mjs
```

`PW_CHANNEL` uses an installed browser when Playwright's own Chromium is not downloaded; leave it out otherwise.

## What is real and what is not

REAL: the shipping card and its client (`contributorNoticeClient`), the shared `settings.css`, and for the phone the
phone's own `styles.css` with the card inside a `.screen` frame, which is where the phone re-tunes the shared cards.

SIMULATED: the Gateway. A fetch shim answers `GET /teams/notifications` with the card worded exactly as the Gateway's
`ContributorNoticeCard` words it, and applies each `PUT` to its state. The Gateway's fold, storage, refusals, the sweep
and the email words are proven in C# (`ContributorNoticeTests`, `PersonEmailSenderTests`).

## The claims

- **A** - the card shows the Gateway's words, right away chosen, no hour.
- **B** - choosing once a day sends one `PUT { mode: "daily", dailyHour: 8 }`; the hour and the time zone line then show,
  with the Gateway's saved line.
- **C** - choosing 17:00 sends one more `PUT { mode: "daily", dailyHour: 17 }`.

## The sample emails

`sample-emails.html` holds the two emails exactly as the Gateway's `ContributorNoticeEmail` composes them (subject, HTML
and plain text), written from the real code. `evidence-sample-emails.png` is its screenshot. Neither email names what was
sent, who sent it, or anyone else: only that something is waiting, in which team, with one link to the Inbox.
