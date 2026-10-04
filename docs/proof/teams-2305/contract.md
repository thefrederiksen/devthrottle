# The Mentor's weekly block - the read contract (devthrottle_internal#2305)

Written before the code, for the Cockpit Developer. It changes only by telling the Phase 3 Mentor Tech Lead.

## The route

```
GET /teams/{teamId}/mentor?week=YYYY-Www
```

- Mapped only when Teams is released (`CC_GATEWAY_TEAMS=1`). Off, the route does not exist: 404 like any unknown
  route.
- Called from a person's own account, with the team in the route (`TeamFrom.RouteTeamId`), exactly as
  `GET /teams/{teamId}/members` is.
- `week` is an ISO week, for example `2026-W40`. The week is Monday to Sunday in the TEAM's own time zone (the
  team tenant's time zone setting). When `week` is left out, the answer is for the most recent week that has
  closed in the team's time zone.
- There is no write route. Blocks are written by the Gateway's weekly Mentor run only.

## Who gets what

| Caller | Answer |
|---|---|
| Owner or Manager of the team | 200, every block of that week (`ReadMentorPageAboutEachPerson`, quotes under `ReadPromptsQuotedOnMentorPage`) |
| Developer of the team | 200, ONLY their own block of that week, or no block (`ReadOwnMentorPage`) |
| Collaborator of the team | 403, `code: "team_action_refused"` - a Collaborator runs no sessions, so the Mentor has no page about them |
| Not a member, or no such team | 404, one answer for both (the gate's `NoSuchTeam`) |
| `week` not a valid ISO week | 400, `{ "error": "..." }` |

The block a Developer reads about themselves and the block their Manager reads about them are the SAME stored row,
serialized by the same code: byte for byte the same JSON object.

## The answer (200)

```json
{
  "teamId": "6f0c...",
  "week": "2026-W40",
  "weekStart": "2026-09-28",
  "weekEnd": "2026-10-04",
  "timeZone": "Europe/Copenhagen",
  "scope": "everyone",
  "written": true,
  "readers": [
    { "email": "olivia@example.com", "role": "Owner" },
    { "email": "priya@example.com", "role": "Manager" }
  ],
  "blocks": [
    {
      "personSubject": "a1b2...",
      "personEmail": "rob@example.com",
      "role": "Developer",
      "tone": "hard",
      "toneLabel": "a hard week",
      "workedOn": "The new signup page and two bug fixes in the installer.",
      "howItWent": null,
      "wentBadlyAndWhy": "On Tuesday they restarted the same task four times. Their first instruction didn't say which file to change, so the agent guessed differently each time.",
      "quotes": [
        { "promptId": "p_3f9a...", "at": "2026-09-29T09:14:03Z", "text": "fix the signup thing so it doesnt break on mobile" }
      ],
      "oneThingToTry": "Name the file and the result you expect in the first line, before asking for the change.",
      "writtenAtUtc": "2026-10-05T00:20:11Z"
    }
  ]
}
```

Field rules - every one of them decided on the Gateway; a client renders, it does not decide (rule 7):

- `scope` - `"everyone"` for an Owner or Manager, `"own"` for a Developer. The heading a client shows is its own
  layout choice; who is in `blocks` is already decided.
- `written` - whether the Mentor run for this team and week has happened. `false` with empty `blocks` means "not
  written yet"; `true` with empty `blocks` means "written, and nobody (or not you) ran sessions that week".
- `readers` - who reads every block of this team: the Owner, then the Managers, each by email and role, ordered by
  the Gateway (role, then email). The same list for every caller - it is what S7's "your Manager reads this same
  page" shows. A member with no email on record is listed with `email: null`.
- `blocks` - one per person who ran sessions that week and had prompts. A person with no sessions, or sessions but
  no prompts, has NO block - never an empty or invented one. Ordered by `personEmail`.
- `personEmail` - who the block is about, as the Team page shows people; head the block with it. The block's own
  words never name anyone: they speak of the person as "they" (owner ruling via the Tech Lead, 4 Oct 2026 - a name
  derived from an email would be a guess). `null` when the person has no email on record.
- `role` - the person's role in the team NOW, as the Team page names it.
- `tone` - one of `good`, `mixed`, `hard`. `toneLabel` is the words to show: `a good week`, `a mixed week`,
  `a hard week`.
- `workedOn` - always present.
- `howItWent` - present or null.
- `wentBadlyAndWhy` - present or null. When present, `quotes` holds one or two prompts; when null, `quotes` is empty.
- `quotes[].text` - the person's own prompt, copied verbatim by the Gateway from the prompt log when the block was
  written. Never written by a model. `promptId` is a stable reference to that prompt record. `at` is when it was
  sent (UTC).
- `oneThingToTry` - always present.
- No number in the answer compares people: no score, no rank, no counts of lines of code.

## What does not exist, on purpose

- No route returns a person's prompts beyond the ones quoted on their block. `GET /prompts` inside a team's tenant
  stays refused; reading another person's prompts is a row of the role table no role has.
- No route lets anyone browse everything a person typed.
- No route writes, edits or deletes a block.
