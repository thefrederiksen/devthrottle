# Tools Overview

The DevThrottle installer puts ten command-line tools on your PATH. They are the shipped set: every tool with `"ship": true` in `tools/registry.json`, and the same ten listed in `src/CcDirector.Core/Tools/tools-manifest.json`. The Director checks their health on the Home screen and in the Tools tab of Settings, and repairs a broken install.

The repository holds more `cc-*` tools than these. They build from source for development but are not installed, so they are not documented here.

Every tool answers `--help` with its full command list. A longer reference with selected help for each tool is in [docs/cli-reference.md](../../cli-reference.md), and the website documentation is at https://devthrottle.com/docs/cli/overview.

| Tool | What it does | What it needs |
|------|--------------|---------------|
| cc-pdf | Markdown to PDF, and PDF back to Markdown, with themes | Chrome or Chromium |
| cc-html | Markdown to HTML, and HTML back to Markdown, with themes | Nothing |
| cc-word | Markdown to Word, and Word back to Markdown, with themes | Nothing |
| cc-gmail | Gmail: read, search, send, labels, calendar, contacts | A one-time `auth` |
| cc-outlook | Outlook: read, search, send, attachments, folders, calendar | A one-time `auth` |
| cc-image | Describe an image, read the text in it, resize and convert | `DEVTHROTTLE_API_KEY` for describe and ocr on the default engine |
| cc-vault | Personal vault: contacts, tasks, goals, ideas, documents, with search | A model key for search and ask |
| cc-secrets | Use a stored password without the model ever seeing it | Entries you add by hand |
| cc-worktrees | Pooled git worktrees, only reset once their work has provably landed | git |
| cc-devthrottle | The fleet: sessions, messages, missions, workflows, skills, schedules, setup | A DevThrottle session for the fleet commands |

---

## Documents: cc-pdf, cc-html, cc-word

All three take the same two commands, `from-markdown` and `to-markdown`, and share one set of themes.

```bash
cc-pdf from-markdown report.md -o report.pdf --theme boardroom
cc-html from-markdown report.md -o report.html --theme paper
cc-word from-markdown report.md -o report.docx --theme boardroom
cc-pdf from-markdown report.md -o report.pdf --page-size letter --margin 1in
cc-pdf to-markdown report.pdf -o report.md
```

- `-o` is required on `from-markdown`; on `to-markdown` it defaults to the input name with `.md`.
- `--theme`: boardroom, paper (the default), terminal, blueprint, thesis, spark, obsidian. `cc-pdf --themes` lists them.
- `--page-size` (a4 by default, or letter) and `--margin` (1in by default): cc-pdf only.
- `--css` a custom style sheet and `--strict-assets` fail when a local image cannot be embedded: cc-pdf and cc-html.
- `--force` overwrites an existing output, `--no-clobber` skips it, `--quiet` hides progress.

---

## Email: cc-gmail and cc-outlook

Sign in once with `auth`; after that both work without prompts. `--account` picks between signed-in accounts and `accounts` manages them.

```bash
cc-gmail list
cc-gmail search "from:someone@example.com"
cc-gmail send --to someone@example.com --subject "Hi" --body "..."
cc-outlook list
cc-outlook reply <message_id> --body "..." --send
cc-outlook calendar today
```

- Both: `auth`, `list`, `read`, `send`, `draft`, `reply`, `search`, `delete`, `archive`, `move`, `recipients`, `profile`, `calendar`.
- `reply` saves a draft unless you pass `--send`.
- cc-gmail also has `drafts`, `count`, `untrash`, `archive-before`, `labels`, `label-stats`, `label-create`, `stats` and `contacts`.
- cc-outlook also has `forward`, `flag`, `categorize`, `unarchive`, `attachments`, `download-attachment`, `folders` and `create-folder`.

---

## Images: cc-image

```bash
cc-image describe photo.png
cc-image describe ./screenshots --recursive
cc-image ocr scan.png
cc-image resize big.png -o small.png --width 800
cc-image convert image.png -o image.webp
cc-image info photo.png
```

By default `describe` and `ocr` run through the DevThrottle API and need `DEVTHROTTLE_API_KEY`; `--engine` picks another engine. `describe` on a folder catalogs every image to JSON and CSV. `resize`, `convert` and `info` run locally with no key.

---

## Vault: cc-vault

```bash
cc-vault init
cc-vault tasks add "Follow up with the pilot customer"
cc-vault contacts search "name"
cc-vault search "kickoff meeting notes" --hybrid
cc-vault ask "what did we decide about pricing?"
cc-vault backup
```

Groups: `contacts`, `tasks`, `goals`, `ideas`, `docs`, `health`, `posts`, `lists`, `tags`, `library`, `catalog`, `graph`, `config`. Also `search`, `ask`, `link`, `unlink`, `links`, `context`, `stats`, `backup`, `restore`, `repair-vectors`. The vault's data is stored on your machine; `search` and `ask` need `OPENAI_API_KEY`, and `cc-vault config show` says whether it is set.

---

## Passwords: cc-secrets

Lets a session use a password without the model ever seeing it. It protects against accidental exposure (transcripts, logs, output, screenshots), not against a hostile program running as the same user.

```bash
cc-secrets add devlinux                      # you, in PowerShell or cmd
cc-secrets list
cc-secrets run devlinux -- sudo -S apt-get update
cc-secrets login github-work --browser center-consulting
cc-secrets log
```

- `add`, `remove` and `list --all` are for you and are refused inside a DevThrottle session. `add` reads the password from a hidden prompt or from a pipe, never from an argument. Git Bash cannot hide typing, so a typed password is refused there; piping works.
- `run` supplies the password on standard input (the default), in one environment variable (`--via env`), or through an askpass helper (`--via askpass`), and returns the output with the password removed.
- `login` fills and submits the login form in a Director-owned browser profile, only on an address the entry allows, and only inside a DevThrottle session.
- `log` shows the audit log: time, entry, session, command, outcome and detail (`--json` adds the machine). Never a password.
- The store is a plain JSON file private to your user: `%LOCALAPPDATA%\cc-director\secrets` on Windows, `~/.cc-director/secrets` on Linux. On macOS the store is not supported yet, so no entry can be added or used there.

The full guide, including the limits, is at https://devthrottle.com/docs/cli/secrets.

---

## Worktrees: cc-worktrees

A pool of git worktrees your sessions share. `get` hands one out and `return` gives it back. A returned worktree is reset and reused, so build output stays warm - but it is only reset once its work has provably landed on the remote. Anything unproven is held, with the reason, and nothing in it is touched.

```bash
cc-worktrees get --repo D:\Repos\myrepo --holder "my session"
cc-worktrees list
cc-worktrees return wt01 --lease <id> --repo D:\Repos\myrepo
cc-worktrees release wt01 --confirm-abandon --repo D:\Repos\myrepo
```

- `get` resets a free slot to the freshly fetched default branch, or creates a new slot beside the repository while the pool is under its size (four by default).
- `return` needs the lease `get` handed you. Landed work is reset and freed; unproven work is held and left exactly as it is.
- `release` is the only way out of held. It pins every commit it cannot prove landed under `refs/cc-worktrees/<slot>/`, where `git gc` can never take it, and then removes the slot. It never deletes a commit.
- `destroy` re-runs the full check at that moment, whatever the recorded state says, and refuses unless it passes. It is a dry run unless you pass `--yes`.
- Every command takes `--json` and never prompts. Exit codes: 0 success, 1 error, 2 usage error, 3 held, 4 pool full.
- The Director uses this tool for its own pooled worktrees, and `cc-devthrottle worktree` passes straight through to it.

---

## The fleet: cc-devthrottle

```bash
cc-devthrottle session list
cc-devthrottle session whoami
cc-devthrottle message inbox
cc-devthrottle message send <session> "Main is red - hold your rebase." --reply-wanted
cc-devthrottle message reply <correlation-id> "Holding."
cc-devthrottle session spawn D:\path\to\repo --prompt "Run the tests." --standalone --why "..."
cc-devthrottle schedule list
cc-devthrottle setup status
```

Groups: `session`, `message`, `mission`, `director`, `machine`, `repo`, `worktree`, `workflow`, `skill`, `schedule`, `browser`, `diag`, `autostart`, `email`, `settings`, `setup`, plus the top-level `actions` and `selftest`.

The fleet commands call the Gateway with the session's own key (`CC_GATEWAY_URL` and `CC_GATEWAY_SESSION_KEY`), which a Director attached to a Gateway puts into every session it launches. Local commands such as `setup status` and `actions` work in any terminal. When you spawn from inside a session, say who owns the new one: `--controlled-by self` or `--standalone` with `--why`.

Messages are rare and they queue. A session may message only the session that started it and the sessions it started, at most six an hour; anything else is refused with "put it in your report". Nothing is typed into a working session: when the recipient is free, one doorbell line tells it to run `cc-devthrottle message inbox`. Nobody waits for an answer - ask with `--reply-wanted`, and the reply arrives in your inbox. See `docs/FleetMessaging.md`.
