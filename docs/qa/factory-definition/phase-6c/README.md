# Factory Definition, phase 6c: agents create and publish factories

Date: 2026-10-16. Mission: `docs/missions/factory-definition-2026-10-14/MISSION.md` (5.3 "Who writes", as amended in
this pull request).

## The ruling

The owner, 16 October 2026: "Factories are created by agents as long as they have user approval." He creates a
factory by telling a session what to make, and his word in that session is the approval; the Gateway asks for nothing
more. What stays forbidden is a factory's OWN session doing it - the teams-and-agents design, R7: "Factory agents can
be written by large language models, but it's the user driving it."

Phases 1 to 6 shipped "only a person publishes" as "the caller is a browser or a phone". That rule is gone:

| Caller | Read | Save a draft | Create a factory | Publish, restore |
|---|---|---|---|---|
| A person (browser, phone) | any factory | any factory | yes | yes |
| A session in no factory | any factory | any factory | yes | yes |
| A session of factory X | X only | X only | refused in words | refused in words |
| The machine token, a Director | any factory | no | no | no |

Every publish records who did it: the person, or the session's id and its name at that moment (two new columns on
`factory_definition_versions`, SQLite and Postgres migrations). The Definition tab's history, `factory definition get`
and `factory definition history` say it in words: "Published by session <id> (<name>)".

## The command line

`cc-devthrottle factory definition create <folder-or-zip>` is new: a NEW factory's first draft, through the same
checked import as `factory import`, refused when the id is already a factory or already has a draft (with the command
to run instead). With `diff`, `publish` and `restore`, an agent does the whole flow from a folder. A publish with
findings is refused with every finding, file and line, as before.

## Proof, on a throwaway Gateway

`rig/run_qa.py`, run in the foreground from the repository root after building the Gateway (Debug):

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" python docs/qa/factory-definition/phase-6c/rig/run_qa.py <eric folder> docs/qa/factory-definition/phase-6c/eric
```

It reuses the phase 5b rig: a Gateway of this tree on port 7989 with its own empty storage root, Tailscale off,
authentication on. No Director. Three sessions are written into its database as a Director registers them: the
BUILDER, a session in no factory named "QA - builder, in no factory"; a session of `eric`; and a session of another
factory. A person only reads, at the end. Eric's folder is the one from phase 7 (3 files, 2,291 bytes; kept with the
mission's private briefs, its hashes in the transcript). The whole run is `eric/transcript.txt`.

| Step | As | Result |
|---|---|---|
| `create <eric folder>` | a session of another factory | exit 1: "A session of factory 'qa-other' may not create a new factory, and 'eric' has no definition yet. ..." |
| `create <eric folder>` | the builder | exit 0: version 1, a draft, `"created": true` |
| `diff eric --to 1` | the builder | "First publish: ..."; would register eric and create "Eric - Mail Desk", daily 08:00 America/Toronto |
| `publish eric --version 1` | a session of eric | exit 1: "A session of factory 'eric' may not publish a factory's definition. ..." |
| `publish eric --version 1 --replaces 0` | the builder | exit 0: "Registry: eric registered from its definition", "Schedule cj_... (Eric - Mail Desk) created for seat mail-desk: Daily 08:00 America/Toronto."; recorded as published by the builder, by id and name |
| `history eric`, `get eric` | the builder | "Published by session <id> (QA - builder, in no factory)" |
| `create <eric folder>` again | the builder | exit 1: "... that factory already exists: Eric. Nothing was created. To make the folder its next draft, run: cc-devthrottle factory import <folder>" |
| `save eric ...` then `publish eric --version 2` | a session of eric | the draft is saved (exit 0); its publish is refused (exit 1) |
| `factory list`, `schedule list` | the builder | eric, 1 seat; "Eric - Mail Desk", enabled, next run 08:00 Toronto |
| The Factories list (`/gateway/factories`) | the person | eric, Eric, RUNNING |
| The stored versions | the database | v1 published, by `session` `<builder id>` `QA - builder, in no factory`; v2 a draft of eric's own session |

Every check in the transcript is a PASS, each with what it saw.

## Not changed

A seat's brief of a factory WITHOUT a definition is still published by a person only, as is a factory memory restore
and a check-state reset: the ruling is about factories' definitions, and those routes do not reach a session in no
factory at all. A defined factory's brief publish goes through the definition's publish and keeps refusing the
factory's own session, now in the definition's new sentence.
