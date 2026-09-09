"""CLI for cc-devthrottle - unified DevThrottle command surface."""

from __future__ import annotations

import json
from typing import List, Optional

import typer
from rich.console import Console
from rich.table import Table

from . import __version__
from . import browser_ops
from . import diag_ops
from . import email_ops
from . import mission_ops
from . import schedule_ops
from . import settings_ops
from . import setup_ops
from . import skill_ops
from . import workflow_ops
from .session_ops import (
    ask_session,
    compact_session,
    hold_session,
    interrupt_session,
    list_my_workers,
    list_sessions,
    mark_done,
    prompt_session,
    raise_hand,
    read_session_buffer,
    rename_session,
    set_session_role,
    selftest as run_selftest,
    send_message,
    spawn_session,
    stop_session,
    undo_done,
    whoami as show_whoami,
)

app = typer.Typer(
    name="cc-devthrottle",
    help="Unified DevThrottle command-line surface.",
    add_completion=False,
    no_args_is_help=True,
)
session_app = typer.Typer(help="Manage running sessions.", add_completion=False)
repo_app = typer.Typer(help="List the fleet's repositories.", add_completion=False)
worktree_app = typer.Typer(help="List the fleet's worktrees and who is in them.", add_completion=False)
machine_app = typer.Typer(
    help="Search and start applications on another computer.",
    add_completion=False,
    no_args_is_help=True,
)
director_app = typer.Typer(
    help="List the Directors this account is running, on every machine.",
    add_completion=False,
    no_args_is_help=True,
)
mission_app = typer.Typer(
    help="Create and list Missions (the unit of work sessions attach to).",
    add_completion=False,
    no_args_is_help=True,
)
message_app = typer.Typer(help="Send messages between sessions.", add_completion=False)
settings_app = typer.Typer(
    help="Read and write CC Director settings.", add_completion=False, no_args_is_help=True
)
schedule_app = typer.Typer(
    help="Manage Gateway schedules.", add_completion=False, no_args_is_help=True
)
workflow_app = typer.Typer(
    help="Read and author fleet Workflows (cross-agent conduct stored on the Gateway).",
    add_completion=False,
    no_args_is_help=True,
)
skill_app = typer.Typer(
    help="Read and author fleet Skills (central capabilities held on the Gateway, fetched on use).",
    add_completion=False,
    no_args_is_help=True,
)
setup_app = typer.Typer(
    help="Install, update, and repair DevThrottle.", add_completion=False, no_args_is_help=True
)
email_app = typer.Typer(
    help="Send email to the account owner.", add_completion=False, no_args_is_help=True
)
diag_app = typer.Typer(
    help="Run network diagnostics (Tailscale direct-vs-relay, speed results).",
    add_completion=False,
    no_args_is_help=True,
)
autostart_app = typer.Typer(
    help="Start the Gateway at login (issue #2022): on | off | status.",
    add_completion=False,
    no_args_is_help=True,
)
browser_app = typer.Typer(
    # The verb stays "browser": it is the resource name agents already hold, in the actions registry
    # and in the attach command baked into the fold. The HELP says "profile", which is what the thing
    # actually is - a dedicated signed-in profile inside Chrome or Edge, not a browser we installed.
    help="Manage DevThrottle's drivable browser profiles (signed in once, driven by an agent; machine-local).",
    add_completion=False,
    no_args_is_help=True,
)
app.add_typer(session_app, name="session")
app.add_typer(repo_app, name="repo")
app.add_typer(worktree_app, name="worktree")
app.add_typer(machine_app, name="machine")
app.add_typer(director_app, name="director")
app.add_typer(mission_app, name="mission")
app.add_typer(message_app, name="message")
app.add_typer(settings_app, name="settings")
app.add_typer(schedule_app, name="schedule")
app.add_typer(workflow_app, name="workflow")
app.add_typer(skill_app, name="skill")
app.add_typer(setup_app, name="setup")
app.add_typer(email_app, name="email")
app.add_typer(diag_app, name="diag")
app.add_typer(autostart_app, name="autostart")
app.add_typer(browser_app, name="browser")
console = Console()

_ACTIONS = [
    {
        "id": "session-list",
        "description": "List every session in the fleet.",
        "command": "cc-devthrottle session list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "autostart-status",
        "description": "Show whether the Gateway starts at login, and the per-OS mechanism.",
        "command": "cc-devthrottle autostart status",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "autostart-on",
        "description": "Start the Gateway when you log in (Windows Run key / macOS launch agent / Linux systemd --user).",
        "command": "cc-devthrottle autostart on",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "autostart-off",
        "description": "Do not start the Gateway at login.",
        "command": "cc-devthrottle autostart off",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "session-whoami",
        "description": "Show this session's id, name, machine, and repository.",
        "command": "cc-devthrottle session whoami",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "session-rename-current",
        "description": "Rename the current session using CC_SESSION_ID.",
        "command": 'cc-devthrottle session rename "<new name>"',
        "mutatesState": True,
        "args": [{"name": "new_name", "required": True}],
    },
    {
        "id": "session-rename-target",
        "description": "Rename a session selected by full id, id prefix, or exact name.",
        "command": 'cc-devthrottle session rename <target> "<new name>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "new_name", "required": True},
        ],
    },
    {
        "id": "session-spawn",
        "description": (
            "Open a new session. By default on the local Director; --machine <name> starts it on "
            "another computer, and --director <id-or-name> starts it on ONE named Director (a machine "
            "runs several, and only this says which)."
        ),
        "command": "cc-devthrottle session spawn <repo> [--machine <name>] [--director <id-or-name>]",
        "mutatesState": True,
        "args": [
            {"name": "repo", "required": True},
            {"name": "machine", "required": False},
            {"name": "director", "required": False},
        ],
    },
    {
        "id": "director-list",
        "description": (
            "List every Director this account is running, on every machine, with the id and name that "
            "'session spawn --director' accepts. A machine appears once per named Director instance."
        ),
        "command": "cc-devthrottle director list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "mission-list",
        "description": (
            "List the ACTIVE Missions on the Gateway - the named bodies of work sessions attach to. "
            "Add --all (or --state complete|removed) to include ones that have been ended."
        ),
        "command": "cc-devthrottle mission list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "mission-create",
        "description": "Create a Mission record on the Gateway and print its id.",
        "command": 'cc-devthrottle mission create "<name>"',
        "mutatesState": True,
        "args": [{"name": "name", "required": True}],
    },
    {
        # Discoverable on purpose. The gap this closed (issue #2387) was that a mission could only be
        # joined in the instant a session was spawned, so a body of work that GREW - which is most of
        # them - could never be shown as one. An agent that cannot find this verb is back in that
        # position, so it belongs in the list an agent reads, not only in the help text.
        "id": "mission-attach",
        "description": (
            "Attach a session that already exists to a Mission, moving it if it already had one. "
            "Add --with-children to bring everything that session controls."
        ),
        "command": "cc-devthrottle mission attach <session> <mission>",
        "mutatesState": True,
        "args": [
            {"name": "session", "required": True},
            {"name": "mission", "required": True},
        ],
    },
    {
        "id": "mission-detach",
        "description": "Detach a session from its Mission, leaving it attached to nothing.",
        "command": "cc-devthrottle mission detach <session>",
        "mutatesState": True,
        "args": [{"name": "session", "required": True}],
    },
    {
        # Discoverable for the same reason attach is. An agent that finishes a body of work and cannot
        # find the verb to END it leaves the mission list growing forever, which is exactly the state
        # the owner found it in: eleven missions, several finished days earlier, with no way out.
        "id": "mission-rename",
        "description": (
            "Rename a Mission. Its id does not change, so every attached session stays attached and "
            "its WHY is kept."
        ),
        "command": 'cc-devthrottle mission rename <mission> "<new name>"',
        "mutatesState": True,
        "args": [
            {"name": "mission", "required": True},
            {"name": "name", "required": True},
        ],
    },
    {
        "id": "mission-complete",
        "description": (
            "Mark a Mission as FINISHED. It leaves the default list and is kept as a record - this is "
            "the ending to use when the work is done."
        ),
        "command": "cc-devthrottle mission complete <mission>",
        "mutatesState": True,
        "args": [{"name": "mission", "required": True}],
    },
    {
        "id": "mission-remove",
        "description": (
            "Remove a Mission that should not exist - a duplicate, a mistake, an abandoned idea. NOT "
            "an outcome: use complete for finished work. Soft, so the record is kept and can be reopened."
        ),
        "command": "cc-devthrottle mission remove <mission>",
        "mutatesState": True,
        "args": [{"name": "mission", "required": True}],
    },
    {
        "id": "mission-reopen",
        "description": "Return a completed or removed Mission to active.",
        "command": "cc-devthrottle mission reopen <mission>",
        "mutatesState": True,
        "args": [{"name": "mission", "required": True}],
    },
    {
        "id": "machine-list",
        "description": (
            "List the computers this account can search and start applications on. A computer appears "
            "once cc-launcher is running on it and has registered with the Gateway."
        ),
        "command": "cc-devthrottle machine list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "machine-apps",
        "description": (
            "List the applications installed on another computer. Omit the query to list everything. "
            "The names it returns are what 'machine launch --app' accepts."
        ),
        "command": "cc-devthrottle machine apps <machine> [query] [--count <n>]",
        "mutatesState": False,
        "args": [
            {"name": "machine", "required": True},
            {"name": "query", "required": False},
            {"name": "count", "required": False},
        ],
    },
    {
        "id": "machine-files",
        "description": (
            "Find files by name across every drive on another computer. Use * and ? to match patterns; "
            "a query containing a directory separator is matched against the whole path. The search is "
            "bounded by a result count AND a time limit, and reports which one stopped it when it ends "
            "early - so check the truncation before treating the answer as complete."
        ),
        "command": "cc-devthrottle machine files <machine> <query> [--count <n>] [--seconds <s>]",
        "mutatesState": False,
        "args": [
            {"name": "machine", "required": True},
            {"name": "query", "required": True},
            {"name": "count", "required": False},
            {"name": "seconds", "required": False},
        ],
    },
    {
        "id": "machine-restart-capability",
        "description": (
            "Ask whether one computer can complete a Director restart, BEFORE draining it. Changes "
            "nothing: no command is sent, no connection opened and no signal raised. It answers with a "
            "verdict and the reason - and separately with whether that machine's launcher would refuse a "
            "restart while sessions are still live, which is not the same question. A drain that cannot "
            "end in a restart is a fleet-wide close with paperwork, so ask first."
        ),
        "command": "cc-devthrottle machine restart-capability <machine>",
        "mutatesState": False,
        "args": [
            {"name": "machine", "required": True},
        ],
    },
    {
        "id": "machine-restart-request",
        "description": (
            "ASK for a Director restart on one computer, in your own words. This restarts nothing: it "
            "creates a request the owner accepts once, after the Gateway has checked that the machine "
            "can actually be restarted (refused on the spot otherwise, naming why). While one request "
            "is pending for a machine a second is refused, and a request nobody accepts expires after "
            "thirty minutes. After the accept the Director drains itself, restarts and restores alone."
        ),
        "command": "cc-devthrottle machine restart-request <machine> --reason \"<why>\" [--director <id>]",
        "mutatesState": True,
        "args": [
            {"name": "machine", "required": True},
            {"name": "reason", "required": True},
            {"name": "director", "required": False},
        ],
    },
    {
        "id": "machine-restart-request-status",
        "description": "Where one restart request stands, with the owner's or the Director's reason.",
        "command": "cc-devthrottle machine restart-request-status <machine> <request-id>",
        "mutatesState": False,
        "args": [
            {"name": "machine", "required": True},
            {"name": "request-id", "required": True},
        ],
    },
    {
        "id": "machine-launch",
        "description": (
            "Start an application on another computer, by catalogue name (--app) or by absolute path "
            "(--path). A name that matches several applications is refused rather than guessed at, so "
            "nothing unintended starts on a machine nobody is sitting at."
        ),
        "command": "cc-devthrottle machine launch <machine> --app \"<name>\" | --path <path>",
        "mutatesState": True,
        "args": [
            {"name": "machine", "required": True},
            {"name": "app", "required": False},
            {"name": "path", "required": False},
            {"name": "args", "required": False},
            {"name": "cwd", "required": False},
            {"name": "headless", "required": False},
        ],
    },
    {
        "id": "session-hold",
        "description": (
            "Park a session so it stops asking for attention, for a set number of minutes. Defaults "
            "to THIS session. A session holding ITSELF is always mid-turn, so the hold is deferred "
            "automatically and lands when the turn ends - there is no separate verb for that, and the "
            "reply says 'pending' when it deferred. Only the owner lifts a hold: releasing it, typing "
            "or speaking into the session, or the timer expiring. Another agent's message does not."
        ),
        "command": "cc-devthrottle session hold [target] --minutes <n>",
        "mutatesState": True,
        "args": [
            {"name": "target", "required": False},
            {"name": "minutes", "required": False},
        ],
    },
    {
        "id": "session-compact",
        "description": (
            "Compact a session's context and send it NOTHING afterwards. Compaction SUMMARIZES the "
            "conversation, so the session keeps what it has learned - unlike clearing, which throws it "
            "away. This is the housekeeping verb: use it on a session whose context is filling up but "
            "which is still working fine. It frees room and leaves the session where it was. For a "
            "session that is STUCK, use session-compact-continue instead. Waits for the compaction to "
            "finish, so it can take a minute or two."
        ),
        "command": "cc-devthrottle session compact [target]",
        "mutatesState": True,
        "args": [{"name": "target", "required": False}],
    },
    {
        "id": "session-compact-continue",
        "description": (
            "Compact a session's context and THEN send it a message - the rescue for a stuck session. A "
            "session whose context window is full cannot read anything sent to it: every message is "
            "swallowed and the tool reprints its context-limit line. This unblocks it and gets it moving "
            "again, so a supervising agent can rescue a worker with nobody at its keyboard. The message "
            "(default 'continue') is sent only once the compaction has actually FINISHED, never on a "
            "timer. Tools that cannot report finishing are refused rather than guessed at - compact those "
            "with session-compact and send the message yourself."
        ),
        "command": 'cc-devthrottle session compact-continue [target] ["<message>"]',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": False},
            {"name": "message", "required": False},
        ],
    },
    {
        "id": "session-stop",
        "description": (
            "End a session NOW and print what actually happened to it. Ends the agent process on the "
            "machine that owns the session and removes its row. Use session done instead when it is "
            "fine for the session to finish what it is doing first. A reason is REQUIRED and is "
            "recorded with the stop: any session may stop any other in the account, and the recorded "
            "reason is what makes that safe to allow. It does NOT touch files - uncommitted changes in "
            "the session's worktree are left exactly as they were, and the answer says where they are. "
            "Stopping something already stopped SUCCEEDS, and the answer says whether no process was "
            "running or nothing in the account carries that identifier at all."
        ),
        "command": 'cc-devthrottle session stop <target> --reason "<why>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "reason", "required": True},
        ],
    },
    {
        "id": "session-done-undo",
        "description": (
            "Take a pending deletion back off a session - the cure for having flagged the wrong one. "
            "Defaults to THIS session. Needs no reason: a stop carries one because it is destructive, "
            "and this is the safe direction."
        ),
        "command": "cc-devthrottle session done [target] --undo",
        "mutatesState": True,
        "args": [{"name": "target", "required": False}],
    },
    {
        "id": "session-hold-release",
        "description": "Release a hold, bringing a parked session back into the normal roster.",
        "command": "cc-devthrottle session hold [target] --release",
        "mutatesState": True,
        "args": [{"name": "target", "required": False}],
    },
    {
        "id": "message-send",
        "description": "Send a one-way message to a session, or broadcast to all sessions.",
        "command": 'cc-devthrottle message send <target|all> "<message>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "message", "required": True},
        ],
    },
    {
        "id": "message-ask",
        "description": "Ask one session a question and print its answer.",
        "command": 'cc-devthrottle message ask <target> "<question>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "question", "required": True},
        ],
    },
    {
        "id": "fleet-selftest",
        "description": "Run an end-to-end fleet messaging smoke test.",
        "command": "cc-devthrottle selftest",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "settings-show",
        "description": "Display current CC Director settings.",
        "command": "cc-devthrottle settings show",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "settings-get",
        "description": "Get a CC Director setting by dotted key.",
        "command": "cc-devthrottle settings get <key>",
        "mutatesState": False,
        "args": [{"name": "key", "required": True}],
    },
    {
        "id": "settings-set",
        "description": "Set a CC Director setting by dotted key.",
        "command": "cc-devthrottle settings set <key> <value>",
        "mutatesState": True,
        "args": [
            {"name": "key", "required": True},
            {"name": "value", "required": True},
        ],
    },
    {
        "id": "settings-list",
        "description": "List all available CC Director setting keys.",
        "command": "cc-devthrottle settings list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "settings-path",
        "description": "Show the local CC Director config file path.",
        "command": "cc-devthrottle settings path",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "schedule-list",
        "description": "List Gateway schedules.",
        "command": "cc-devthrottle schedule list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "schedule-get",
        "description": "Show one Gateway schedule in full.",
        "command": "cc-devthrottle schedule get <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-runs",
        "description": "Show run history for a Gateway schedule.",
        "command": "cc-devthrottle schedule runs <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-create",
        "description": "Create a Gateway schedule.",
        "command": "cc-devthrottle schedule create --name <name> --machine <machine> --repo <repo> --cron <expr> --tz <tz> --seed <prompt>",
        "mutatesState": True,
        "args": [
            {"name": "name", "required": True},
            {"name": "machine", "required": True},
            {"name": "repo", "required": True},
            {"name": "cron_or_at", "required": True},
            {"name": "tz", "required": True},
            {"name": "seed_or_worklist", "required": True},
        ],
    },
    {
        "id": "schedule-run",
        "description": "Fire a Gateway schedule immediately.",
        "command": "cc-devthrottle schedule run <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-enable",
        "description": "Enable a Gateway schedule.",
        "command": "cc-devthrottle schedule enable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-disable",
        "description": "Disable a Gateway schedule.",
        "command": "cc-devthrottle schedule disable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-delete",
        "description": "Delete a Gateway schedule.",
        "command": "cc-devthrottle schedule delete <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-endpoint",
        "description": "Show the Gateway endpoint used by schedule commands.",
        "command": "cc-devthrottle schedule endpoint",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "skill-list",
        "description": "List the fleet's Skills - central capabilities held on the Gateway, one line each.",
        "command": "cc-devthrottle skill list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "skill-get",
        "description": "Print a Skill's full instructions - run this when you are ABOUT TO USE it, and follow what it says.",
        "command": "cc-devthrottle skill get <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "skill-show",
        "description": "Show one Skill's metadata without its body.",
        "command": "cc-devthrottle skill show <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "skill-versions",
        "description": "Show a Skill's version history.",
        "command": "cc-devthrottle skill versions <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "skill-pull",
        "description": "Pull a Skill into a directory (skill.json + SKILL.md + its files at their own paths) for editing.",
        "command": 'cc-devthrottle skill pull <id> --dir "<dir>"',
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "dir", "required": True}],
    },
    {
        "id": "skill-push",
        "description": "Push a directory as the Skill's DRAFT. No agent sees it until you publish.",
        "command": 'cc-devthrottle skill push <id> --dir "<dir>" [--note "<what changed>"]',
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "dir", "required": True}],
    },
    {
        "id": "skill-publish",
        "description": "Publish a Skill's draft - live for every agent on every machine, immediately.",
        "command": "cc-devthrottle skill publish <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "skill-clone",
        "description": "Clone a Skill into one of your own - how a read-only built-in is customized.",
        "command": "cc-devthrottle skill clone <id> <new-id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "new-id", "required": True}],
    },
    {
        "id": "skill-enable",
        "description": "Make a Skill available again - back in every agent's briefing.",
        "command": "cc-devthrottle skill enable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "skill-disable",
        "description": "Switch a Skill off - left out of every briefing, fetch refused, nothing deleted.",
        "command": "cc-devthrottle skill disable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-list",
        "description": "List the fleet's Workflows (cross-agent conduct stored on the Gateway).",
        "command": "cc-devthrottle workflow list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "workflow-instructions",
        "description": "Print a Workflow's raw instruction markdown - fetch this and FOLLOW it as your conduct.",
        "command": "cc-devthrottle workflow instructions <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "workflow-show",
        "description": "Show one Workflow's metadata, steps, and outcome criteria.",
        "command": "cc-devthrottle workflow show <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "workflow-versions",
        "description": "Show a Workflow's version history.",
        "command": "cc-devthrottle workflow versions <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-pull",
        "description": "Pull a Workflow into a directory (workflow.json + instructions.md + helpers/) for editing.",
        "command": 'cc-devthrottle workflow pull <id> --dir "<dir>"',
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "dir", "required": True}],
    },
    {
        "id": "workflow-push",
        "description": "Push an edited Workflow directory to the Gateway as a draft (creates the Workflow if new).",
        "command": 'cc-devthrottle workflow push <id> --dir "<dir>" [--note "<what changed>"]',
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "dir", "required": True}],
    },
    {
        "id": "workflow-publish",
        "description": "Publish a Workflow's draft - it becomes the version every machine and agent reads.",
        "command": "cc-devthrottle workflow publish <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-materialize",
        "description": "Write a Workflow's instructions and helper files to this machine's cache and print the paths.",
        "command": "cc-devthrottle workflow materialize <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "workflow-runs",
        "description": "List workflow runs (one row per execution; the governance outcome spine).",
        "command": "cc-devthrottle workflow runs",
        "mutatesState": False,
        "args": [{"name": "workflow", "required": False}, {"name": "status", "required": False}],
    },
    {
        "id": "workflow-run-show",
        "description": "Show one workflow run: pinned version, lifecycle, acceptance, criteria, participants.",
        "command": "cc-devthrottle workflow run <run id>",
        "mutatesState": False,
        "args": [{"name": "run_id", "required": True}],
    },
    {
        "id": "workflow-enable",
        "description": "Turn a Workflow back ON (returns to agents' briefings; runs and seats resume).",
        "command": "cc-devthrottle workflow enable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-disable",
        "description": "Turn a Workflow OFF - hidden from agents' briefings, no new runs or seats; nothing deleted.",
        "command": "cc-devthrottle workflow disable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-clone",
        "description": "Clone a Workflow's published content into a new editable Workflow you own (the way to customize a built-in).",
        "command": "cc-devthrottle workflow clone <id> <new-id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "new-id", "required": True}],
    },
    {
        "id": "workflow-delete",
        "description": "Archive a custom Workflow (built-ins can never be deleted; history remains).",
        "command": "cc-devthrottle workflow delete <id> --yes",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "email-owner",
        "description": "Email the account owner (single recipient); optional file attachments. The escalation channel for unattended runs.",
        "command": 'cc-devthrottle email owner --subject "<subject>" --body "<text>" [--attach <file>]',
        "mutatesState": True,
        "args": [
            {"name": "subject", "required": True},
            {"name": "body", "required": False},
            {"name": "html", "required": False},
            {"name": "attach", "required": False},
        ],
    },
    {
        "id": "setup-status",
        "description": "Show local DevThrottle setup status.",
        "command": "cc-devthrottle setup status",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "setup-install",
        "description": "Install or repair DevThrottle from the latest GitHub release.",
        "command": "cc-devthrottle setup install",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "setup-update",
        "description": "Update DevThrottle through the setup engine.",
        "command": "cc-devthrottle setup update",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "setup-repair",
        "description": "Repair DevThrottle through the setup engine.",
        "command": "cc-devthrottle setup repair",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "setup-doctor",
        "description": "Show local DevThrottle setup diagnostics and repair guidance.",
        "command": "cc-devthrottle setup doctor --json",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "browser-list",
        "description": "List this machine's drivable browser profiles (name, browser, status, account).",
        "command": "cc-devthrottle browser list --json",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "browser-create",
        "description": "Register a new drivable browser profile (does not launch it).",
        "command": 'cc-devthrottle browser create --name "Center Consulting" --browser chrome',
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "browser-signin",
        "description": "Open the account page for a one-time human sign-in; add --done to mark it complete.",
        "command": 'cc-devthrottle browser signin "Center Consulting"',
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "browser-start",
        "description": "Launch a profile if it is down, then print how to attach the harness.",
        "command": 'cc-devthrottle browser start "Center Consulting"',
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "browser-attach",
        "description": "Print the BU_NAME/BU_CDP_URL export lines to attach browser-harness to a profile.",
        "command": 'eval "$(cc-devthrottle browser attach \'Center Consulting\')"',
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "browser-stop",
        "description": "Close a running browser profile cleanly (its login is kept).",
        "command": 'cc-devthrottle browser stop "Center Consulting"',
        "mutatesState": True,
        "args": [],
    },
]


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-devthrottle v{__version__}")
        raise typer.Exit()


@browser_app.command("list")
def browser_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List the drivable browser profiles on this machine."""
    browser_ops.list_browsers(json_output)


@browser_app.command("create")
def browser_create(
    name: str = typer.Option(..., "--name", help='Human-facing name, e.g. "Center Consulting".'),
    browser: str = typer.Option(
        "chrome", "--browser", help="Which browser: chrome, edge, brave, or opera."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Register a new drivable browser profile (does not launch it)."""
    browser_ops.create_browser(name, browser, json_output)


@browser_app.command("signin")
def browser_signin(
    name: str = typer.Argument(..., help="Browser name or id."),
    done: bool = typer.Option(False, "--done", help="Record that the human finished signing in."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Open the account page for a one-time hand sign-in, or (with --done) mark it complete."""
    browser_ops.signin_browser(name, done, json_output)


@browser_app.command("start")
def browser_start(
    name: str = typer.Argument(..., help="Browser name or id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Launch the browser if it is down, then print how to attach to it."""
    browser_ops.start_browser(name, json_output)


@browser_app.command("stop")
def browser_stop(
    name: str = typer.Argument(..., help="Browser name or id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Close a running browser cleanly (its login is kept; start it again any time)."""
    browser_ops.stop_browser(name, json_output)


@browser_app.command("attach")
def browser_attach(
    name: str = typer.Argument(..., help="Browser name or id."),
) -> None:
    """Print ONLY the export lines, so: eval "$(cc-devthrottle browser attach 'Name')\"."""
    browser_ops.attach_browser(name)


@browser_app.command("rename")
def browser_rename(
    name: str = typer.Argument(..., help="Current browser name or id."),
    to: str = typer.Option(..., "--to", help="New name."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Rename a browser's label (id, port, and folder are unchanged)."""
    browser_ops.rename_browser(name, to, json_output)


@browser_app.command("remove")
def browser_remove(
    name: str = typer.Argument(..., help="Browser name or id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Stop the browser, delete its folder, and drop it from the registry."""
    browser_ops.remove_browser(name, json_output)


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Unified DevThrottle command-line surface."""


@app.command()
def actions(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List agent-discoverable actions."""
    if json_output:
        print(json.dumps({"actions": _ACTIONS}, indent=2))
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("ACTION")
    table.add_column("COMMAND")
    for action in _ACTIONS:
        table.add_row(str(action["id"]), str(action["command"]))
    console.print(table)


@session_app.command("list")
def session_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List every session running across the fleet."""
    list_sessions(json_output)


@repo_app.command("list")
def repo_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
    dirty: bool = typer.Option(False, "--dirty", help="Only repositories with uncommitted work."),
) -> None:
    """List the fleet's repositories with their state and worktree summary."""
    from .repo_ops import list_repositories

    list_repositories(json_output, dirty_only=dirty)


@worktree_app.command("list")
def worktree_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
    repo: str = typer.Option(None, "--repo", help="Only worktrees of this repository."),
    state: str = typer.Option(None, "--state", help="Filter: safe-to-reap, in-use, or needs-attention."),
) -> None:
    """List the fleet's worktrees: verdicts, sizes, and which session is in each."""
    from .repo_ops import list_worktrees

    list_worktrees(json_output, repo=repo, state=state)


@machine_app.command("list")
def machine_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List the computers you can search and start applications on."""
    from .machine_ops import list_machines

    list_machines(json_output)


@director_app.command("list")
def director_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List every Director this account is running, with the id to pass to 'session spawn --director'."""
    from .machine_ops import list_directors

    list_directors(json_output)


@machine_app.command("apps")
def machine_apps(
    machine: str = typer.Argument(..., help="The computer to look on."),
    query: str = typer.Argument(None, help="Filter by name. Omit to list everything installed."),
    count: int = typer.Option(100, "--count", "-n", help="Largest number of results to return."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List the applications installed on another computer."""
    from .machine_ops import list_apps

    list_apps(machine, query, count, json_output)


@machine_app.command("files")
def machine_files(
    machine: str = typer.Argument(..., help="The computer to search."),
    query: str = typer.Argument(..., help="Filename to find. Use * and ? to match patterns."),
    count: int = typer.Option(200, "--count", "-n", help="Largest number of results to return."),
    seconds: int = typer.Option(20, "--seconds", "-s", help="How long the search may run before it reports what it found."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Find files by name across every drive on another computer.

    The search is bounded by both a result count and a time limit, and says which one stopped it when
    it returns early, so a partial answer is never mistaken for the whole one.
    """
    from .machine_ops import search_files

    search_files(machine, query, count, seconds, json_output)


@machine_app.command("restart-capability")
def machine_restart_capability(
    machine: str = typer.Argument(..., help="The computer to ask about."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Can this computer complete a Director restart? Ask this BEFORE draining it.

    Changes nothing: no command is sent, no connection opened and no signal raised. A drain that
    cannot end in a restart is a fleet-wide close with paperwork, so the machine says so first.
    """
    from .machine_ops import restart_capability

    restart_capability(machine, json_output)


@machine_app.command("restart-request")
def machine_restart_request(
    machine: str = typer.Argument(..., help="The computer whose Director should be restarted."),
    reason: str = typer.Option(..., "--reason", "-r", help="Why, in your own words. The owner decides on this sentence."),
    director: Optional[str] = typer.Option(None, "--director", help="Which Director on that computer, when it runs several."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Ask for a Director restart. The machine scrutinises; the owner accepts once; then it runs alone.

    A request, not a restart: it creates a pending record, refused on the spot when the machine cannot
    be restarted or another request is already pending. The direct restart stays refused to a session.
    """
    from .machine_ops import restart_request

    restart_request(machine, reason, director, json_output)


@machine_app.command("restart-request-status")
def machine_restart_request_status(
    machine: str = typer.Argument(..., help="The computer the request was for."),
    request_id: str = typer.Argument(..., help="The request id the ask printed."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Where one restart request stands."""
    from .machine_ops import restart_request_status

    restart_request_status(machine, request_id, json_output)


@machine_app.command("launch")
def machine_launch(
    machine: str = typer.Argument(..., help="The computer to start it on."),
    app: str = typer.Option(None, "--app", "-a", help="Application name, as shown by 'machine apps'."),
    path: str = typer.Option(None, "--path", "-p", help="Absolute path to start instead of a name."),
    args: str = typer.Option(None, "--args", help="Command-line arguments to pass to it."),
    cwd: str = typer.Option(None, "--cwd", help="Working directory to start it in."),
    headless: bool = typer.Option(False, "--headless", help="Run with no window."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Start an application on another computer, by name or by absolute path."""
    from .machine_ops import launch

    launch(machine, app, path, args, cwd, headless, json_output)


@session_app.command()
def whoami() -> None:
    """Show this session's own fleet identity."""
    show_whoami()


@session_app.command()
def rename(
    target_or_name: str = typer.Argument(
        ..., help="New name for this session, or a target when NEW_NAME is also provided."
    ),
    new_name: Optional[str] = typer.Argument(
        None, help="New name when an explicit target is provided."
    ),
) -> None:
    """Rename a session, defaulting to the current session."""
    if new_name is None:
        rename_session(None, target_or_name)
    else:
        rename_session(target_or_name, new_name)


@session_app.command()
def prompt(
    target: str = typer.Argument(..., help="Session to prompt (id prefix, number, or exact name)."),
    text: str = typer.Argument(..., help="The text to send."),
    no_submit: bool = typer.Option(
        False, "--no-submit", help="Type the text but do not press Enter - leave it in the composer."
    ),
) -> None:
    """Send raw text into a session, as if you had typed it.

    Unlike `message send`, the text is NOT framed with a sender - the session sees exactly what
    you passed. Use `message send` for agent-to-agent messages, and this to drive a session.
    """
    prompt_session(target, text, no_submit=no_submit)


@session_app.command()
def interrupt(
    target: Optional[str] = typer.Argument(
        None, help="Session to interrupt. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Stop what a session is currently doing."""
    interrupt_session(target)


@session_app.command(name="raise")
def raise_(
    reason: Optional[str] = typer.Argument(
        None, help="What you are blocked on, in your own words. Required unless --clear."
    ),
    target: Optional[str] = typer.Option(
        None, "--target", help="Session to raise for. Defaults to THIS session (CC_SESSION_ID)."
    ),
    clear: bool = typer.Option(False, "--clear", help="Take the hand back down - the decision was answered."),
) -> None:
    """Put your hand up to the session driving you, when you cannot go on without an answer.

    A supervised session - a worker with a live supervisor, or a scheduled run - is quiet toward the
    owner by construction: it parks on every screen when it stops and it has no channel to him. This
    is the channel it has instead. An ARCHITECT is not one of these: it is the seat the owner talks
    to, so it reaches him directly and has no need of this.

    Raise it only when you are STILL WORKING and have hit something you cannot decide inside your
    mandate: an ambiguous requirement, an irreversible step, a real design fork, an authorisation you
    do not hold. Not for progress, and not for "I finished" - stopping already says that.

    Your hand lowers itself when your turn ends.
    """
    raise_hand(reason, target, clear)


@session_app.command()
def workers(
    target: Optional[str] = typer.Option(
        None, "--target", help="Whose workers to list. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """List the sessions you are driving, and which of them have their hand up.

    A manager learns what its workers are doing by READING them, not by being messaged "notice me".
    This is that read in one line - who you are driving, what state each is in, and what any of them
    is blocked on.
    """
    list_my_workers(target)


@session_app.command()
def hold(
    target: Optional[str] = typer.Argument(
        None, help="Session to hold. Defaults to THIS session (CC_SESSION_ID)."
    ),
    release: bool = typer.Option(False, "--release", help="Release the hold instead of applying one."),
    minutes: Optional[int] = typer.Option(
        None, "--minutes", help="Hold for this many minutes, then surface it again."
    ),
) -> None:
    """Park a session so it stops asking for you, or release it.

    Hold THIS session when you have nothing left to report and do not want to keep asking for
    attention: `cc-devthrottle session hold --minutes 720`. You do not need a special verb for
    that. A hold asked for while the session is still working - which is always the case when a
    session holds ITSELF, since it is mid-turn - is DEFERRED automatically: it applies the moment
    the turn finishes, and the reply tells you so with `pending`.

    ONLY THE OWNER LIFTS A HOLD - by releasing it, by typing or speaking into the session, or by
    the --minutes timer running out. Another agent messaging the session, or the terminal simply
    repainting, no longer un-holds it, so a hold you set actually lasts as long as you asked for.
    """
    hold_session(target, release=release, minutes=minutes)


@session_app.command()
def compact(
    target: Optional[str] = typer.Argument(
        None, help="Session to compact. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Compact a session's context. Sends it nothing afterwards.

    Compaction SUMMARIZES the conversation, so the session keeps what it has learned. That is the
    difference from clearing, which throws the conversation away.

    Use this for housekeeping - a session whose context is filling up but which is still working
    fine. It frees room and leaves the session exactly where it was.

    If the session is STUCK - full, and swallowing everything you send it - use
    `cc-devthrottle session compact-continue` instead, which also gets it moving again.

    This waits for the compaction to actually finish, so it can take a minute or two.
    """
    compact_session(target, None)


@session_app.command("compact-continue")
def compact_continue(
    target: Optional[str] = typer.Argument(
        None, help="Session to compact. Defaults to THIS session (CC_SESSION_ID)."
    ),
    message: str = typer.Argument(
        "continue", help="What to send once the compaction finishes. Defaults to 'continue'."
    ),
) -> None:
    """Compact a session's context, then send it a message - the rescue for a STUCK session.

    A session whose context window is full cannot read anything you send it: every message is
    swallowed and the tool just reprints its context-limit line. Compaction is the only thing that
    unblocks it, and this verb also gets it moving again afterwards, so a supervising agent can
    rescue a worker with nobody at its keyboard.

    The message is sent only once the compaction has actually FINISHED - never on a timer. A prompt
    fired while the tool is still summarizing gets swallowed exactly like the ones that were lost
    before it.

    Some tools can be compacted but cannot report when they finished (codex, pi, gemini, grok,
    opencode today). This verb refuses them rather than guessing a moment: compact those with
    `cc-devthrottle session compact` and send the message yourself once the session is idle.
    """
    compact_session(target, message)


@session_app.command()
def buffer(
    target: Optional[str] = typer.Argument(
        None, help="Session to read. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Print what a session's terminal is showing - how you see what a session is actually doing."""
    read_session_buffer(target)


@session_app.command()
def role(
    role_or_target: str = typer.Argument(
        ...,
        help="Role for this session (Standalone, Manager, Worker, Architect), or a target when ROLE is "
             "also provided. Pass 'none' to clear the explicit role.",
    ),
    role_value: Optional[str] = typer.Argument(
        None, help="Role when an explicit target is provided."
    ),
) -> None:
    """Declare a session's explicit role, defaulting to the current session.

    Valid roles: Standalone, Manager, Worker, Architect (case-insensitive). Pass 'none' to clear
    the explicit role and revert to automatic derivation.

    Worker and Manager are normally derived from the fleet: a controlled session with a live
    controller is a Worker; a session controlling a live session is a Manager. Architect cannot be
    inferred from the spawn graph, so declaring it here is the only way to make one after birth.
    An explicit role is sticky and wins over derivation.
    """
    if role_value is None:
        target, wanted = None, role_or_target
    else:
        target, wanted = role_or_target, role_value
    # "none" is the CLI's way to say "clear it" - the endpoint clears on an empty role.
    set_session_role(target, "" if wanted.strip().lower() == "none" else wanted)


@session_app.command()
def stop(
    target: str = typer.Argument(
        ..., help="Session to stop (id prefix, number, or exact name)."
    ),
    reason: Optional[str] = typer.Option(
        None,
        "--reason",
        "-r",
        help="Why you are stopping it, in your own words. Required, and recorded with the stop.",
    ),
    json_output: bool = typer.Option(
        False,
        "--json",
        help="Print the Gateway's answer as JSON instead of the sentences, for a caller that parses it.",
    ),
) -> None:
    """End a session NOW, and print what actually happened to it.

    This is the immediate stop. It ends the agent process on the machine that owns the session and
    removes the session's row. Use `cc-devthrottle session done` instead when it is fine for the
    session to finish what it is doing first - that is the polite path and it always was.

    A reason is REQUIRED, and it is recorded with the stop. Any session may stop any other session
    in the account, and the reason is what makes that safe to allow: it is not a courtesy, it is the
    record of who ended what and why.

    It does NOT touch files. Uncommitted changes in the session's worktree are left exactly as they
    were, and the answer says so and says where they are.

    Stopping something that is already stopped SUCCEEDS. There is nothing left to stop, so there is
    nothing to fail at - and the answer says which of the two it found: no process was running (a
    machine was asked and it looked), or nothing in this account carries that identifier at all (no
    machine was asked). Those are different facts and they are never folded into one word.
    """
    stop_session(target, reason, json_output=json_output)


@session_app.command()
def done(
    target: Optional[str] = typer.Argument(
        None, help="Session to mark for deletion. Defaults to THIS session (CC_SESSION_ID)."
    ),
    reason: Optional[str] = typer.Option(
        None, "--reason", help="Short reason, shown while the session winds down."
    ),
    undo: bool = typer.Option(
        False,
        "--undo",
        help="Clear a pending deletion instead of setting one. Needs no reason.",
    ),
) -> None:
    """Flag a session for deletion (defaults to the current session).

    Does NOT kill the session now - it is flagged, and the owning Director's reaper removes it
    within about a minute once the grace window passes and it is no longer working. Use this at
    the end of an unattended run that has nothing left for the user, so the session tears itself
    down instead of lingering in the fleet.

    Flagged the wrong session? `--undo` takes the flag back off. Clearing a flag is the safe
    direction, so it needs no reason - and it is the right cure for a mistyped target, where
    stopping the session outright would be the most destructive answer to a typo.
    """
    if undo:
        undo_done(target, reason)
    else:
        mark_done(target, reason)


@session_app.command()
def spawn(
    repo: str = typer.Argument(..., help="Absolute path to the repository / working directory."),
    agent: str = typer.Option(
        "ClaudeCode",
        "--agent",
        help="Agent CLI: ClaudeCode, Pi, Codex, Gemini, OpenCode, Grok, Copilot, RawCli.",
    ),
    prompt: Optional[str] = typer.Option(
        None, "--prompt", help="First prompt to send once the session is ready."
    ),
    name: Optional[str] = typer.Option(None, "--name", help="Custom display name for the session."),
    purpose: Optional[str] = typer.Option(
        None,
        "--purpose",
        help="Short description of what the session is FOR (e.g. 'implement #799'); used to "
        "build the session name when no --name is given.",
    ),
    command: Optional[str] = typer.Option(
        None, "--command", help="For --agent RawCli: the executable to run (e.g. cmd, pwsh)."
    ),
    command_args: Optional[str] = typer.Option(
        None, "--command-args", help="For --agent RawCli: arguments for the command."
    ),
    args: Optional[str] = typer.Option(
        None,
        "--args",
        help="Override the agent's command-line arguments for this session (issue #1017). "
        "When omitted, the session inherits the same default agent settings (permission mode, "
        "default model) the desktop New Session dialog applies.",
    ),
    controlled_by: Optional[str] = typer.Option(
        None,
        "--controlled-by",
        help="Controlling session for the new session (issue #815 / automatic roles). By DEFAULT a "
        "session-initiated spawn (CC_SESSION_ID set) becomes a Worker controlled by the spawner, so it "
        "stays quiet and reports to its manager. Pass an explicit session id to be controlled by a "
        "different session, 'self' for this session, or 'none' (same as --standalone) to spawn a "
        "human-facing PEER with no controller.",
    ),
    standalone: bool = typer.Option(
        False,
        "--standalone",
        help="Spawn a human-facing PEER, not a subordinate Worker: force NO controller even when run "
        "from inside a session. The opt-out for the automatic-worker default.",
    ),
    role: Optional[str] = typer.Option(
        None,
        "--role",
        help="Explicit session role (automatic session roles): Standalone, Manager, Worker, or Architect "
        "(case-insensitive). Sticky, and wins over auto-derivation - the way to declare an Architect. An "
        "unknown value is rejected by the Director.",
    ),
    machine: Optional[str] = typer.Option(
        None,
        "--machine",
        help="Start the session on ANOTHER computer. Omit (or name this machine) to spawn locally, "
        "unchanged. A remote machine name routes the spawn through the Gateway to a Director on that "
        "machine (first available, auto-launched if none is running); an off/unreachable machine fails "
        "loudly with no local fallback.",
    ),
    director: Optional[str] = typer.Option(
        None,
        "--director",
        help="Start the session on ONE named Director, by its Director id or its display name. One "
        "machine runs several Directors, so --machine alone lands on whichever is first; this lands on "
        "the one you named, wherever it runs (no --machine needed - though giving one narrows which "
        "Directors the name may match). A Director that is not running, or a name that matches two, "
        "fails loudly - it never falls back to another Director. List them with "
        "'cc-devthrottle director list'; a Director's own toolbar Copy button hands you its id.",
    ),
    mission: Optional[str] = typer.Option(
        None,
        "--mission",
        help="Attach the new session to a Mission by its id at spawn (mission-as-first-class-unit-of-work). "
        "The Mission must already exist (create one with 'cc-devthrottle mission create'); an unknown "
        "Mission is rejected by the Director. A mission spawn also auto-seats the session on the "
        "mission's workflow run. Omitted, a session spawned with a controlling session INHERITS that "
        "session's mission (and says so); pass 'none' to opt out and start attached to nothing.",
    ),
    workflow_run: Optional[str] = typer.Option(
        None,
        "--workflow-run",
        help="Seat the new session on a workflow RUN by its id (Workflows phase 5b). The Gateway "
        "validates the run and the session's preamble tells the agent to fetch the run's conduct at "
        "its PINNED version. Unknown run ids are rejected.",
    ),
) -> None:
    """Open a new session - here, on another computer with --machine, or on one Director with --director."""
    spawn_session(
        repo, agent, prompt, name, purpose, command, command_args, controlled_by, args, standalone, role,
        machine, mission, workflow_run, director,
    )


@mission_app.command("create")
def mission_create(
    name: str = typer.Argument(..., help="Human-friendly name for the Mission."),
) -> None:
    """Create a Mission record on the Gateway and print its id."""
    mission_ops.create_mission(name)


@mission_app.command("list")
def mission_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
    show_all: bool = typer.Option(
        False,
        "--all",
        "-a",
        help="Include missions that have been completed or removed. Off by default: the question "
        "this command answers is 'what am I working on', and finished work is the wrong answer to it.",
    ),
    state: Optional[str] = typer.Option(
        None,
        "--state",
        help="Show only this state: active, complete, or removed. Overrides --all.",
    ),
) -> None:
    """List the Missions on the Gateway (active ones by default)."""
    mission_ops.list_missions(json_output, state=state or ("all" if show_all else None))


@mission_app.command("rename")
def mission_rename(
    mission: str = typer.Argument(
        ..., help="The Mission to rename: its id, an id prefix, or part of its name."
    ),
    name: str = typer.Argument(..., help="The new display name."),
) -> None:
    """Rename a Mission. Its id does not change, so every attached session stays attached."""
    mission_ops.rename_mission(mission, name)


@mission_app.command("complete")
def mission_complete(
    mission: str = typer.Argument(
        ..., help="The Mission to complete: its id, an id prefix, or part of its name."
    ),
) -> None:
    """Mark a Mission as finished. It leaves the default list but is kept - this is the outcome."""
    mission_ops.end_mission(mission, "complete")


@mission_app.command("remove")
def mission_remove(
    mission: str = typer.Argument(
        ..., help="The Mission to remove: its id, an id prefix, or part of its name."
    ),
) -> None:
    """Remove a Mission that should not exist (a duplicate, a mistake). Soft: the record is kept."""
    mission_ops.end_mission(mission, "removed")


@mission_app.command("reopen")
def mission_reopen(
    mission: str = typer.Argument(
        ..., help="The Mission to reopen: its id, an id prefix, or part of its name."
    ),
) -> None:
    """Return a completed or removed Mission to active."""
    mission_ops.reopen_mission(mission)


@mission_app.command("attach")
def mission_attach(
    session: str = typer.Argument(
        ..., help="The session to attach: its number, an id prefix, or part of its name."
    ),
    mission: str = typer.Argument(
        ..., help="The Mission to attach it to: its id, an id prefix, or part of its name."
    ),
    with_children: bool = typer.Option(
        False,
        "--with-children",
        help="Also attach every session this one controls, all the way down. Off by default: a "
        "controlling session routinely commissions work that is NOT part of its own mission, and a "
        "bulk re-parent cannot be undone in one step.",
    ),
) -> None:
    """Attach a session that already exists to a Mission (moving it if it already had one)."""
    mission_ops.attach_session(session, mission, with_children)


@mission_app.command("detach")
def mission_detach(
    session: str = typer.Argument(
        ..., help="The session to detach: its number, an id prefix, or part of its name."
    ),
) -> None:
    """Detach a session from its Mission, leaving it attached to nothing."""
    mission_ops.detach_session(session)


@diag_app.command("network")
def diag_network(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Server-side network check: per connected device, direct-vs-DERP-relay + latency, plus UDP/NAT.

    Runs on the Gateway with no phone and no app open - the check an agent uses to tell "warming up on
    the relay" apart from "genuinely slow".
    """
    diag_ops.show_network(json_output)


@diag_app.command("results")
def diag_results(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Recent speed-test results users submitted from the app or Cockpit (newest first)."""
    diag_ops.show_results(json_output)


@message_app.command("send")
def message_send(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name - or 'all' for your team."),
    message: str = typer.Argument(..., help="The message text to send."),
    everyone: bool = typer.Option(
        False,
        "--everyone",
        help="Broadcast to the WHOLE fleet, not just your own team. Every message interrupts the "
        "receiving agent, so this is gated by the Gateway Hub: it needs a human-issued grant (--grant) "
        "and a --reason, and is refused otherwise (issue #1229). Without this flag, 'all' reaches only "
        "your team - the sessions in your Mission, or (solo) the same repository on the same machine.",
    ),
    reason: Optional[str] = typer.Option(
        None,
        "--reason",
        help="Why a fleet-wide broadcast is warranted. Required with --everyone; logged by the Hub.",
    ),
    grant: Optional[str] = typer.Option(
        None,
        "--grant",
        help="A human-issued broadcast grant id authorizing a fleet-wide broadcast (--everyone).",
    ),
) -> None:
    """Send a message to one session, or to your team when TARGET is 'all' (add --everyone for the whole fleet)."""
    send_message(target, message, everyone=everyone, reason=reason, grant=grant)


@message_app.command("ask")
def message_ask(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name (single session)."),
    question: str = typer.Argument(..., help="The question to ask."),
    timeout_ms: int = typer.Option(
        120000, "--timeout-ms", help="How long to wait for the answer, in milliseconds."
    ),
) -> None:
    """Ask TARGET the QUESTION and print the answer."""
    ask_session(target, question, timeout_ms)


@app.command()
def selftest(
    timeout_ms: int = typer.Option(
        25000, "--timeout-ms", help="How long the ask step waits for the responder."
    ),
) -> None:
    """Run the fleet messaging self-test against the local Director."""
    run_selftest(timeout_ms)


@settings_app.command("show")
def settings_show(
    section: Optional[str] = typer.Argument(
        None, help="Section name to show, e.g. screenshots, vault, or llm."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Display current settings."""
    settings_ops.show(section, json_output)


@settings_app.command("get")
def settings_get(
    key: str = typer.Argument(..., help="Setting key, e.g. screenshots.source_directory."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Get a specific setting value."""
    settings_ops.get(key, json_output)


@settings_app.command("set")
def settings_set(
    key: str = typer.Argument(..., help="Setting key, e.g. screenshots.source_directory."),
    value: str = typer.Argument(..., help="New value."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Set a configuration value."""
    settings_ops.set_config_value(key, value, json_output)


@settings_app.command("list")
def settings_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List all setting keys."""
    settings_ops.list_settings(json_output)


@settings_app.command("path")
def settings_path(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show the config file location."""
    settings_ops.path(json_output)


@schedule_app.callback()
def schedule_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Manage Gateway schedules."""
    schedule_ops.set_gateway_override(gateway)


@skill_app.callback()
def skill_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Read and author fleet Skills (central capabilities held on the Gateway, fetched on use)."""
    skill_ops.set_gateway_override(gateway)


@skill_app.command("list")
def skill_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List every Skill the fleet holds - one line each, no bodies."""
    skill_ops.list_skills(json_output)


@skill_app.command("get")
def skill_get(
    skill_id: str = typer.Argument(..., help="The skill id (e.g. move-session)."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific published version instead of the current one."
    ),
) -> None:
    """Print a Skill's full instructions - run this when you are ABOUT TO USE the skill, and follow
    what it says. Supporting files are written to this machine and their paths printed after the
    body. Fails loudly if the Gateway cannot be reached; never proceed from memory."""
    skill_ops.get_skill(skill_id, version)


@skill_app.command("show")
def skill_show(
    skill_id: str = typer.Argument(..., help="The skill id."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific version instead of the published one."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one Skill's metadata without its body."""
    skill_ops.show_skill(skill_id, version, json_output)


@skill_app.command("versions")
def skill_versions(
    skill_id: str = typer.Argument(..., help="The skill id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show a Skill's version history, newest first."""
    skill_ops.list_versions(skill_id, json_output)


@skill_app.command("pull")
def skill_pull(
    skill_id: str = typer.Argument(..., help="The skill id."),
    directory: str = typer.Option(..., "--dir", "-d", help="Directory to write the skill into."),
    version: Optional[int] = typer.Option(
        None,
        "--version",
        "-v",
        help="A specific version (default: the draft if one exists, else the published version).",
    ),
) -> None:
    """Pull a Skill into a directory (skill.json + SKILL.md + its files at their own paths) for editing."""
    skill_ops.pull_skill(skill_id, directory, version)


@skill_app.command("push")
def skill_push(
    skill_id: str = typer.Argument(..., help="The skill id."),
    directory: str = typer.Option(..., "--dir", "-d", help="Directory holding the skill files."),
    note: Optional[str] = typer.Option(None, "--note", "-n", help="One line on what changed."),
    force: bool = typer.Option(
        False, "--force", help="Push without a hash sidecar, overwriting deliberately."
    ),
) -> None:
    """Push a directory as the Skill's DRAFT. No agent sees it until you publish."""
    skill_ops.push_skill(skill_id, directory, note, force)


@skill_app.command("publish")
def skill_publish(
    skill_id: str = typer.Argument(..., help="The skill id."),
) -> None:
    """Publish the Skill's draft - live for every agent on every machine on its next fetch."""
    skill_ops.publish_skill(skill_id)


@skill_app.command("clone")
def skill_clone(
    skill_id: str = typer.Argument(..., help="The skill to copy."),
    new_id: str = typer.Argument(..., help="The new skill id."),
) -> None:
    """Clone a Skill into one of your own - how a read-only built-in is customized."""
    skill_ops.clone_skill(skill_id, new_id)


@skill_app.command("enable")
def skill_enable(
    skill_id: str = typer.Argument(..., help="The skill id."),
) -> None:
    """Make a Skill available again - back in every agent's briefing."""
    skill_ops.set_skill_enabled(skill_id, True)


@skill_app.command("disable")
def skill_disable(
    skill_id: str = typer.Argument(..., help="The skill id."),
) -> None:
    """Switch a Skill off - left out of every briefing, fetch refused, nothing deleted."""
    skill_ops.set_skill_enabled(skill_id, False)


@skill_app.command("delete")
def skill_delete(
    skill_id: str = typer.Argument(..., help="The skill id."),
    yes: bool = typer.Option(False, "--yes", "-y", help="Do not ask for confirmation."),
) -> None:
    """Archive a Skill (never a built-in). Its versions remain readable by explicit version."""
    skill_ops.delete_skill(skill_id, yes)


@workflow_app.callback()
def workflow_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Read and author fleet Workflows (cross-agent conduct stored on the Gateway)."""
    workflow_ops.set_gateway_override(gateway)


@workflow_app.command("list")
def workflow_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List every Workflow the fleet can run."""
    workflow_ops.list_workflows(json_output)


@workflow_app.command("show")
def workflow_show(
    workflow_id: str = typer.Argument(..., help="The workflow id (e.g. mission)."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific version instead of the published one."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one Workflow's metadata, steps, and outcome criteria."""
    workflow_ops.show_workflow(workflow_id, version, json_output)


@workflow_app.command("instructions")
def workflow_instructions(
    workflow_id: str = typer.Argument(..., help="The workflow id (e.g. mission)."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific pinned version instead of the published one."
    ),
) -> None:
    """Print the Workflow's raw instruction markdown - fetch this and FOLLOW it as your conduct."""
    workflow_ops.print_instructions(workflow_id, version)


@workflow_app.command("versions")
def workflow_versions(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show a Workflow's version history, newest first."""
    workflow_ops.list_versions(workflow_id, json_output)


@workflow_app.command("pull")
def workflow_pull(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    directory: str = typer.Option(..., "--dir", "-d", help="Directory to write the workflow into."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific version (default: the draft if one exists, else the published version)."
    ),
) -> None:
    """Pull a Workflow into a directory (workflow.json + instructions.md + helpers/) for editing."""
    workflow_ops.pull_workflow(workflow_id, directory, version)


@workflow_app.command("push")
def workflow_push(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    directory: str = typer.Option(..., "--dir", "-d", help="Directory holding the workflow files."),
    note: Optional[str] = typer.Option(None, "--note", help="One line describing what changed."),
    force: bool = typer.Option(
        False,
        "--force",
        help="Push an update WITHOUT the .workflow-hash sidecar (skips the stale-copy check; "
        "may overwrite another author's edit).",
    ),
) -> None:
    """Push a Workflow directory to the Gateway as a draft (creates the Workflow if new)."""
    workflow_ops.push_workflow(workflow_id, directory, note, force)


@workflow_app.command("publish")
def workflow_publish(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
) -> None:
    """Publish the draft - it becomes the version every machine and agent reads."""
    workflow_ops.publish_workflow(workflow_id)


@workflow_app.command("materialize")
def workflow_materialize(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific published version (default: the current one)."
    ),
) -> None:
    """Write the Workflow's instructions and helper files to this machine's cache and print the paths."""
    workflow_ops.materialize_workflow(workflow_id, version)


@workflow_app.command("runs")
def workflow_runs(
    workflow: Optional[str] = typer.Option(
        None, "--workflow", "-w", help="Only runs of this workflow id."
    ),
    status: Optional[str] = typer.Option(
        None, "--status", "-s",
        help="Only runs in this lifecycle status (created, active, awaiting-human, succeeded, failed, abandoned).",
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List workflow runs (one row per execution of a workflow), newest first."""
    workflow_ops.list_runs(workflow, status, json_output)


@workflow_app.command("run")
def workflow_run(
    run_id: str = typer.Argument(..., help="The run id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one workflow run: pinned version, lifecycle, acceptance, criteria, participants, proof."""
    workflow_ops.show_run(run_id, json_output)


@workflow_app.command("enable")
def workflow_enable(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
) -> None:
    """Turn a Workflow back ON - it returns to every agent's briefing; runs and seats resume."""
    workflow_ops.set_workflow_enabled(workflow_id, True)


@workflow_app.command("disable")
def workflow_disable(
    workflow_id: str = typer.Argument(..., help="The workflow id (built-ins included)."),
) -> None:
    """Turn a Workflow OFF - hidden from agents' briefings, no new runs or seats; nothing deleted."""
    workflow_ops.set_workflow_enabled(workflow_id, False)


# "workflow reset" was retired with the Shared Workflow Library phase 3: built-ins are read-only,
# can never diverge from the shipped content, and have nothing to reset.


@workflow_app.command("clone")
def workflow_clone(
    workflow_id: str = typer.Argument(..., help="The source workflow id (e.g. mission)."),
    new_id: str = typer.Argument(..., help="The id for the clone (a fresh slug, never a built-in id)."),
) -> None:
    """Clone a Workflow's published content into a new editable Workflow you own.

    The sanctioned way to customize a built-in: the clone copies the steps, instructions, and
    helper files into version 1 of the new id, immediately published and fully editable, with
    where-it-came-from recorded. The built-in itself stays exactly as DevThrottle ships it.
    """
    workflow_ops.clone_workflow(workflow_id, new_id)


@workflow_app.command("delete")
def workflow_delete(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip the confirmation prompt."),
) -> None:
    """Archive a custom Workflow (built-ins can never be deleted; version history remains)."""
    workflow_ops.delete_workflow(workflow_id, yes)


@schedule_app.command("list")
def schedule_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List every schedule on the Gateway."""
    schedule_ops.list_jobs(json_output)


@schedule_app.command("get")
def schedule_get(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one schedule in full."""
    schedule_ops.get_job(job_id, json_output)


@schedule_app.command("runs")
def schedule_runs(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show run history for a schedule."""
    schedule_ops.list_runs(job_id, json_output)


@schedule_app.command("create")
def schedule_create(
    name: str = typer.Option(..., "--name", help="Human-readable label for the schedule."),
    machine: str = typer.Option(..., "--machine", help="Target machine name."),
    repo: str = typer.Option(..., "--repo", help="Working directory the fired session runs in."),
    at: Optional[str] = typer.Option(None, "--at", help="One-off local timestamp."),
    cron: Optional[str] = typer.Option(None, "--cron", help="Recurring 5-field cron expression."),
    tz: str = typer.Option(..., "--tz", help="IANA/Windows time zone id."),
    seed: Optional[str] = typer.Option(None, "--seed", help="Skill or prompt the session runs."),
    worklist: Optional[str] = typer.Option(None, "--worklist", help="Named work list to drain."),
    notify_on: str = typer.Option(
        schedule_ops.NOTIFY_NONE,
        "--notify-on",
        help="Run-complete notification: none, always, or failure.",
    ),
    notify_webhook: Optional[str] = typer.Option(
        None,
        "--notify-webhook",
        help="Optional outbound webhook URL.",
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the created schedule as JSON."),
) -> None:
    """Create a schedule, one-off with --at or recurring with --cron."""
    schedule_ops.create_job(
        name,
        machine,
        repo,
        at,
        cron,
        tz,
        seed,
        worklist,
        notify_on,
        notify_webhook,
        json_output,
    )


@schedule_app.command("run")
def schedule_run(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the run record as JSON."),
) -> None:
    """Fire a schedule immediately."""
    schedule_ops.run_now(job_id, json_output)


@schedule_app.command("enable")
def schedule_enable(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Enable a schedule so it fires on schedule again."""
    schedule_ops.enable_job(job_id)


@schedule_app.command("disable")
def schedule_disable(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Disable a schedule while keeping its definition."""
    schedule_ops.disable_job(job_id)


@schedule_app.command("delete")
def schedule_delete(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Delete a schedule from the Gateway."""
    schedule_ops.delete_job(job_id)


@schedule_app.command("endpoint")
def schedule_endpoint(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show the Gateway base URL used by schedule commands."""
    schedule_ops.endpoint(json_output)


@setup_app.command("status")
def setup_status(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show local DevThrottle setup status."""
    setup_ops.status(json_output)


@setup_app.command("install")
def setup_install(
    role: str = typer.Option(
        "workstation", "--role", help="Install role: workstation or gateway."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Plan only; do not apply changes."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Install DevThrottle from the latest GitHub release."""
    setup_ops.install(role, dry_run, json_output)


@setup_app.command("update")
def setup_update(
    role: str = typer.Option(
        "workstation", "--role", help="Install role: workstation or gateway."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Plan only; do not apply changes."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Update DevThrottle from the latest GitHub release."""
    setup_ops.update(role, dry_run, json_output)


@setup_app.command("repair")
def setup_repair(
    role: str = typer.Option(
        "workstation", "--role", help="Install role: workstation or gateway."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Plan only; do not apply changes."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Repair the local DevThrottle install."""
    setup_ops.repair(role, dry_run, json_output)


@setup_app.command("doctor")
def setup_doctor(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show setup diagnostics."""
    setup_ops.doctor(json_output)


@autostart_app.command("on")
def autostart_on(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Start the Gateway when you log in (issue #2022)."""
    setup_ops.run_autostart("on", json_output)


@autostart_app.command("off")
def autostart_off(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Do not start the Gateway at login."""
    setup_ops.run_autostart("off", json_output)


@autostart_app.command("status")
def autostart_status(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show whether the Gateway starts at login, and the per-OS mechanism."""
    setup_ops.run_autostart("status", json_output)


@email_app.callback()
def email_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Send email to the account owner."""
    email_ops.set_gateway_override(gateway)


@email_app.command("owner")
def email_owner(
    subject: str = typer.Option(..., "--subject", help="The email subject line."),
    body: Optional[str] = typer.Option(
        None, "--body", help="Plain-text body. Provide --body, --html, and/or --attach."
    ),
    html: Optional[str] = typer.Option(
        None, "--html", help="HTML body. Provide --body, --html, and/or --attach."
    ),
    attach: Optional[List[str]] = typer.Option(
        None, "--attach", help="Attach a file (repeatable), e.g. an HTML report to read offline."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the send result as JSON."),
) -> None:
    """Send ONE email to the account owner (single recipient - no way to address anyone else).

    Passes only a subject, body, and any attachments to the Gateway, which relays it to the cloud
    with your account token; the cloud resolves the owner and sends. Use it to escalate from an
    unattended or scheduled run, or to send yourself a report to read offline.
    """
    email_ops.send_owner(subject, body, html, attach, json_output)


if __name__ == "__main__":
    app()
