# A factory's definition: the format

A factory is one **definition** on the Gateway: one versioned set of files that says what the factory is, which seats it
has, how each seat starts, and what each seat may use. Exported, a definition is a folder. On the Gateway the same
content is one version.

This page is the format, for people. The machine-readable contract is the four JSON Schemas the Gateway carries
(`src/CcDirector.Gateway/Factory/Definition/Schemas/`), and the check that enforces them is
`FactoryDefinitionCheck`. A test keeps this page and the schemas together: every key a schema names appears here.

Keys are camelCase. Ids (factory, seat, grant, rule set, skill) are lower-case letters and digits joined by single
hyphens, at most 64 characters. A YAML file may not use anchors, aliases or tags, may not write a key twice, and may
not nest lists and mappings more than 64 deep. A YAML or JSON file and a SKILL.md are saved as UTF-8 without a
byte-order mark.

## The files a definition holds

| Path | Required | What |
|---|---|---|
| `factory.yaml` | yes, exactly one | the factory |
| `seats/<seat>.yaml` | at least one | one seat; `<seat>` equals the file's `seat` key |
| `grants.yaml` | no | the grants |
| `rules/<name>.json` | no | one rule set |
| `files/<any path>` | no | a tool or template, any text |
| `checks/<name>.py` | no | a check; its name may use hyphens or underscores (`new_mail.py`) |
| `skills/<name>/SKILL.md` | no | a factory skill |
| `skills/<name>/<any path>` | no | a file beside a factory skill; its folder must have a `SKILL.md` |

A path uses forward slashes, with no leading slash, no control character (such as a line break), no `.`, `..` or
empty part, and is at most 200 characters. A file
holds at most 256 KB and a version at most 4 MB, counted in UTF-8 bytes, and a version holds at most 1000 files.

## factory.yaml

```yaml
factory: devthrottle              # required; must equal the factory the definition is saved for
title: DevThrottle                # required; the name the owner reads
purpose: Builds DevThrottle       # optional; one line, at most 120 characters
goalText: |                       # optional
  Ship the product.
goalApprovedOn: 2026-10-10        # optional, YYYY-MM-DD; only with goalText
bossSeat: boss                    # optional; that seat must exist and its role must be Boss
defaults:                         # required
  machine: SOREN_NORTH            # required: where a seat runs when it names no machine
  agent: ClaudeCode               # required: the agent a seat runs when it names none
  model: claude-opus-5-5          # optional: the model a seat runs when it names none
repositories: [devthrottle]       # optional: repository names the factory may work in, each once
```

- `factory` - the factory's id.
- `title` - the name the owner reads.
- `purpose` - one line saying what the factory does.
- `goalText` - the factory's goal, in prose.
- `goalApprovedOn` - the date the owner approved the goal.
- `bossSeat` - the id of the boss seat.
- `defaults` - holds `machine`, `agent` and `model`, which a seat takes when it names none of its own.
- `repositories` - the repositories the factory may work in.

The agents a definition may name are `ClaudeCode`, `Pi`, `Codex`, `Gemini`, `OpenCode`, `Cursor`, `Grok` and `Copilot`.

## seats/&lt;seat&gt;.yaml

```yaml
seat: outreach                    # required; equals the file name without .yaml
name: Outreach                    # required; the display name
role: Sales                       # required; the role word (the boss seat's is Boss)
line: Find and sell               # optional; the production line
machine: SOREN_NORTH              # optional; else defaults.machine
agent: ClaudeCode                 # optional; else defaults.agent
model: claude-opus-5-5            # optional; else defaults.model
repository: devthrottle           # optional; one of factory.yaml's repositories. Absent: no repository
brief: |                          # required, not blank: what the seat is told to do
  Find prospects.
starts:                           # optional; how the seat starts
  - every: 10m                    # exactly one of cron or every
    check: checks/new_mail.py     # required with every
    secrets: [outreach-imap-password]
    prompt: "New mail: {count}. {context}"
grants: [send-outreach-email]     # optional; ids in grants.yaml
rules: [outreach]                 # optional; rule set names (rules/<name>.json)
secrets: [outreach-smtp-password] # optional; secret NAMES the seat needs at start
skills:                           # optional
  factory: [mail-voice]           # skills/mail-voice/SKILL.md in this definition
  fleet: [browsers]               # a skill in the account's skill store
files: [files/outreach_send.py]   # optional; paths under files/ in this definition
```

- `seat` - the seat's id.
- `name` - the display name.
- `role` - the role word.
- `line` - the production line.
- `machine`, `agent`, `model` - where and on what the seat runs; each falls back to the factory's `defaults`.
- `repository` - the repository the seat works in, one of the factory's `repositories`.
- `brief` - what the seat is told to do.
- `starts` - the ways the seat starts (below).
- `grants` - the grants the seat uses. The two lists must agree: a seat that names a grant must be in that grant's
  `seats`, and the other way round.
- `rules` - the rule sets the seat takes. Only a seat on an agent that can take rules at launch may name one (see
  the rules section below).
- `secrets` - the secret names the seat needs. A definition holds names, never values.
- `skills` - `factory` lists the factory's own skills, `fleet` lists skills from the account's skill store. One name
  may not be both: a seat would not know which one it got.
- `files` - the files the seat needs.

### A start

```yaml
starts:
  - cron: "0 8 * * 1-5"           # a time: five fields, as schedules take
    timeZone: Europe/Copenhagen   # required with cron, not allowed with every
    prompt: Morning round.        # required, not blank
  - every: 10m                    # an interval: <n>s, <n>m or <n>h
    check: checks/new_mail.py     # required with every; optional with cron
    secrets: [outreach-imap-password]   # only with a check: the names the check gets
    prompt: "New mail: {count}. {context}"
    preventOverlap: true          # optional, default true; cannot be false with a check
```

- `cron` - a five-field cron expression, read with the same parser schedules use.
- `timeZone` - the zone the cron expression is read in, looked up as schedules look it up.
- `every` - an interval, within the trigger's own bounds (one minute to one day).
- `check` - a path `checks/<name>.py` in this definition that decides whether the seat starts.
- `secrets` - the secret names the check gets.
- `prompt` - what the seat is told when the start fires.
- `preventOverlap` - whether a start waits while the last run is still going.

## grants.yaml

```yaml
grants:
  - id: send-outreach-email       # required, unique
    allows: Send email to prospects   # required, plain words
    limits: At most 20 a day      # optional, plain words
    seats: [outreach]             # required, at least one
```

- `grants` - the list of grants.
- `id` - the grant's id.
- `allows` - what the grant allows, in plain words.
- `limits` - its limits, in plain words.
- `seats` - the seats that hold it; each must name the grant too.

## rules/&lt;name&gt;.json

The permission part of what `.claude/settings.json` holds. A rule set carries limits, never behaviour, so anything
else (hooks, env, model) is refused as an unknown key.

A rule that does not bind is worse than none, because the owner believes it binds. So a seat may name a rule set
only when its agent (its own, else the factory's default) can take rules at launch; otherwise the check refuses the
seat on its `rules:` line with the reason. Phase 0 answered for two agents:

| Agent | Takes rules at launch | Why |
|---|---|---|
| ClaudeCode | yes | the rules are merged into the one settings file it is launched with |
| Codex | no | Codex enforces a rule given at launch only while its hook runs; when the hook is missing, fails or times out the command runs anyway, so a Codex seat cannot carry rules. |
| every other agent | not yet | Phase 0 has not shown that the agent can take rules at launch, so a seat on it cannot carry rules yet. |

```json
{ "permissions": { "allow": ["Bash(git status)"], "deny": ["Bash(git push:*)"], "ask": [] } }
```

- `permissions` - holds the three lists.
- `allow` - rules allowed without asking.
- `deny` - rules refused.
- `ask` - rules asked about each time.

## skills/&lt;name&gt;/SKILL.md

Begins with frontmatter holding `name` (equal to the folder name) and `description` (not blank). Other frontmatter
keys are allowed. The body is any text.

```markdown
---
name: mail-voice
description: How the factory writes mail.
---
Write plainly.
```

A factory skill must reach one session only, never a checkout or the user's own skills folder. A seat may name one
only when its agent (its own, else the factory's default) can load it that way; otherwise the check refuses the
seat on its `factory:` line with the reason. Phase 0 answered for two agents:

| Agent | Loads a factory skill | Why |
|---|---|---|
| ClaudeCode | yes | the kit is passed at launch as a plugin folder; inside the session the skill is named `<plugin name>:<skill name>` |
| Codex | no | Codex reads skills only from the start folder and its parents, the user's home folder and an administrator folder; none of them is for one session alone, so a Codex seat cannot be given a factory skill. |
| every other agent | not yet | Phase 0 has not shown that the agent can load a factory skill, so a seat on it cannot name one yet. |

## What the check reports

Every finding has a severity (`error` or `notice`), the file, the line (0 when it is about the whole file) and one
plain sentence. At most 200 findings come back, then one more saying how many were not shown. Errors refuse a save,
a publish and a restore. Notices never refuse.

Errors:

- A file that is not valid YAML or JSON, a key written twice, an anchor, alias or tag, nesting deeper than 64, a
  byte-order mark.
- An unknown key (with the keys that file takes), a missing required key, a wrong type, a bad value.
- A path that is not one of the places above, a file over 256 KB, a version over 4 MB or over 1000 files.
- A reference that does not resolve, reported on the line that makes it: a seat's grant, rule set, factory skill,
  file, check or repository; a fleet skill not in the account's skill store; the boss seat missing or not role Boss;
  a grant naming a seat that does not exist; the two grant lists disagreeing; a skill file whose folder has no
  `SKILL.md`.
- A factory skill and a fleet skill with one name; a factory skill on an agent that cannot load one; a rule set on
  an agent that cannot take rules at launch.
- A start with both or neither of `cron` and `every`; `every` with no check; a check with `preventOverlap: false`;
  `secrets` on a start with no check.

Notices:

- Every secret name: whether the machine holds it is not checked yet.
- A machine no Director of the account reports now.
- A seat with a repository: whether the machine has it registered is not checked yet.
- A rule set, file, check or factory skill that nothing names.
