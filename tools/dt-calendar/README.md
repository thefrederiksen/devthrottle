# dt-calendar

Calendar over standard CalDAV, for any provider that offers it (Zoho, Fastmail, iCloud, Nextcloud, any
CalDAV host). It holds only what the standard protocol can do; anything that exists only in one
provider's own API belongs in that provider's tool (dt-zoho, cc-gmail, cc-outlook).

Microsoft 365 has no CalDAV, so an Outlook calendar uses cc-outlook, never this.

## The password comes from cc-secrets, never a file

```
dt-calendar account add soren --email soren@devthrottle.com --url https://calendar.zohocloud.ca \
    --secret-entry zoho-mail-admin --password-env ZOHO_ADMIN_PASSWORD --default
cc-secrets run zoho-mail-admin -- dt-calendar account check
cc-secrets run zoho-mail-admin -- dt-calendar events
```

The settings file (`<cc-director config>/dt-calendar/config.json`) names the cc-secrets entry and
variable and holds no secret. A missing variable fails with the exact `cc-secrets run ...` command.

## Test mode: allowed recipients

`dt-calendar allowed-recipients set a@x b@y` - while a list is set, creating or changing an event that
invites anyone else is refused before any request is made.

## Commands

| Command | What it does |
|---|---|
| `calendars` | The account's calendars |
| `events [--calendar C] [--from DAY] [--to DAY]` | Events in a range (default: today and the next 30 days), earliest first |
| `event ID` | One event, with everyone invited and their answers |
| `create --summary --start [--end\|--duration] [--all-day] [--location] [--description] [--invite A]` | Create; with --invite this account organises it and the server sends the invitations |
| `change ID [--summary --start --end --duration --location --description --invite --uninvite]` | Change only what is given; everything else, including what the server added, is kept. Only the organizer changes an event; an attendee uses respond |
| `delete ID` | Delete; for an event this account organised, the server sends the cancellations |
| `respond ID --as accepted\|declined\|tentative` | Answer an invitation in this account's calendar; the server passes it to the organizer |

Times are typed in this machine's time zone (`2026-11-10 14:00`) or with an offset, and written to the
server in UTC. An all-day event's `end` is its last day, the same way `--end` takes it. Every command takes
`--account` and `--json`.

## Things learned on a real server (Zoho)

- Discovery is PROPFIND on `/.well-known/caldav`; OPTIONS answers 400 there.
- A time-range query returns ETags only; the bodies come from calendar-multiget.
- A wrong If-Match answers 409 (not 412); a change is refused rather than overwriting someone else's.
- A deleted event answers GET with 200 and an empty body; that is read as gone.
- A PUT answer carries no ETag; every write is read back.
- Zoho drops attendees outside its organisation over CalDAV without an error, and sends its invitations
  from noreply@zohocalendar.ca with Reply-To the organizer. dt-calendar reads every write back and fails,
  naming who was dropped. For outside invitations on Zoho use dt-zoho.
- Zoho also keeps an attendee the organizer removes over CalDAV. `change --uninvite` reads the event back
  and fails, naming who is still invited.

## Limits

- A recurring event is listed once, at its first start; changing one with changed occurrences is refused.
  A series' time is changed only when it is in UTC or a time zone this machine knows, and it keeps that zone.
- No calendar is created or deleted, and tasks (VTODO) are not handled.

## Tests

`python -m pytest tools/dt-calendar/tests` - an in-memory CalDAV server answering as Zoho did
(`tests/fakes.py`). No test touches the network.
