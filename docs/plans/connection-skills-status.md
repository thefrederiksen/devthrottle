# Connection Skills: Implementation Status

## Goal

Replace Python wrapper tools (cc-linkedin ~3000 lines, cc-reddit ~1500 lines) with markdown instruction files ("skills") that Claude reads at invocation time. Claude's intelligence + cc-browser v2 ARIA snapshots replace the Python extraction code.

## How It Works

1. Each connection (linkedin, reddit, etc.) gets a **skill file** -- a markdown document containing site-specific knowledge (delays, URL patterns, ARIA patterns, bot detection rules)
2. A **Claude Code skill wrapper** (e.g. `/linkedin`) injects the skill file into Claude's prompt via `!cc-browser skills show --connection linkedin`
3. Claude reads the instructions and uses `cc-browser` commands directly -- no Python wrapper needed

## What's Done

### Skill files (built-in)
- `tools/cc-browser/skills/linkedin.skill.md` -- LinkedIn navigation knowledge
- `tools/cc-browser/skills/reddit.skill.md` -- Reddit navigation knowledge
- `tools/cc-browser/skills/spotify.skill.md` -- Spotify navigation knowledge
- `tools/cc-browser/skills/manifest.json` -- Skill metadata

### CLI subcommand (`cc-browser skills`)
Fully implemented in `tools/cc-browser/src/cli.mjs`:
- `cc-browser skills list` -- List all skills (managed + custom)
- `cc-browser skills show <connection>` -- Print resolved skill to stdout
- `cc-browser skills show <name> --managed` -- Show a managed skill by name
- `cc-browser skills fork <connection>` -- Fork managed skill to custom for editing
- `cc-browser skills reset <connection>` -- Reset to managed skill
- `cc-browser skills learn <connection> "text"` -- Append learned pattern
- `cc-browser skills learned <connection>` -- Show learned patterns
- `cc-browser skills clear-learned <connection>` -- Clear learned patterns

### Skill resolution in connections.mjs
- `getManagedSkillsDir()` -- Returns path to managed skills
- `managedSkillExists(name)` -- Checks if a managed skill exists for a connection name
- Auto-detects skill type on connection creation

### Build & deploy (`build.ps1`)
- Copies `skills/` directory into deploy target (`_cc-browser/skills/`)
- Copies `*.skill.md` files to shared managed skills dir (`%LOCALAPPDATA%/cc-director/skills/managed/`)

### Deployed layout
```
%LOCALAPPDATA%/cc-director/
  bin/_cc-browser/
    skills/linkedin.skill.md
    skills/reddit.skill.md
    skills/spotify.skill.md
    skills/manifest.json
  skills/managed/
    linkedin.skill.md
    reddit.skill.md
    spotify.skill.md
    manifest.json
```

## What's NOT Done

### 1. Claude Code skill wrappers (`.claude/skills/`)
**No `/linkedin` or `/reddit` slash commands exist yet.** These are the files that make the skills invocable from Claude Code.

Create `.claude/skills/linkedin.md`:
```markdown
---
name: linkedin
description: Interact with LinkedIn via browser connection
---
# LinkedIn Connection

Connection skill instructions:
!cc-browser skills show --connection linkedin

Use cc-browser commands to carry out the user's request.
The connection name is "linkedin".
$ARGUMENTS
```

Create `.claude/skills/reddit.md` with the same pattern (connection name = "reddit").

### 2. `skill` field on connections.json
Currently, skill resolution only works when the connection name matches the skill name exactly (e.g. connection "linkedin" finds "linkedin.skill.md").

If someone names their connection "my-work-linkedin", it won't find the skill. Need to add an optional `skill` field to connections.json entries:
```json
{ "name": "my-work-linkedin", "skill": "linkedin", ... }
```

Update `connections.mjs`:
- Add `skill` to the `allowed` list in `updateConnection()`
- Add `skill` as a parameter in `createConnection()`
- Create a `getSkillForConnection(name)` resolver that checks: connection's `skill` field first, then falls back to connection name

### 3. CLAUDE.md updates
The global CLAUDE.md (`C:\Users\alice\.claude\CLAUDE.md`) still has the old bot detection warning blocks for LinkedIn and Reddit. These should be replaced with:

```markdown
## LinkedIn and Reddit

When interacting with browser connections, use the connection skill:
- /linkedin for LinkedIn operations
- /reddit for Reddit operations

These skills load site-specific instructions automatically (delays, bot detection, URL patterns).
```

### 4. User override path
The plan called for users to override skills by placing a `skill.md` in their connection directory:
```
%LOCALAPPDATA%/cc-director/connections/linkedin/skill.md
```
The skill resolver should check this path first before falling back to the managed skill. This may already be partially implemented in the daemon's `/skills/show` handler -- needs verification.

## Files to Modify

| File | Change |
|------|--------|
| `tools/cc-browser/src/connections.mjs` | Add `skill` field support + resolver |
| `C:\Users\alice\.claude\CLAUDE.md` | Replace bot detection blocks |
| `D:\ReposFred\devthrottle\CLAUDE.md` | Add skill usage instructions |

## Files to Create

| File | Purpose |
|------|---------|
| `.claude/skills/linkedin.md` | `/linkedin` slash command wrapper |
| `.claude/skills/reddit.md` | `/reddit` slash command wrapper |

## Verification Checklist

- [ ] `cc-browser skills list` shows linkedin, reddit, spotify
- [ ] `cc-browser skills show --connection linkedin` prints the skill markdown
- [ ] `/linkedin` slash command works in Claude Code from any directory
- [ ] `/reddit` slash command works in Claude Code from any directory
- [ ] Connection with custom `skill` field resolves correctly
- [ ] User override in `connections/{name}/skill.md` takes priority over managed
