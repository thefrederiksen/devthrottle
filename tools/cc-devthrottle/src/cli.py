"""CLI for cc-devthrottle - unified DevThrottle command surface."""

from __future__ import annotations

import json
import sys
from typing import List, Optional

import typer
from rich.console import Console

from . import __version__
from . import browser_ops
from . import diag_ops
from . import email_ops
from . import fleet_manager_ops
from . import fleet_ops
from . import mission_ops
from . import schedule_ops
from . import settings_ops
from . import setup_ops
from . import skill_ops
from . import workflow_ops
from .usage_errors import AxiGroup
# cc_shared is importable here: the ops modules above put tools/ on the path when run from source.
from cc_shared import axi_output  # noqa: E402
from .session_ops import (
    compact_session,
    hold_session,
    interrupt_session,
    list_my_workers,
    list_sessions,
    mark_done,
    prompt_session,
    raise_hand,
    read_inbox,
    report_to_parent,
    read_session_buffer,
    rename_session,
    set_session_role,
    selftest as run_selftest,
    show_live_state,
    send_message,
    send_reply,
    spawn_session,
    stop_session,
    undo_done,
    whoami as show_whoami,
)

app = typer.Typer(
    cls=AxiGroup,
    name="cc-devthrottle",
    help="Unified DevThrottle command-line surface.",
    add_completion=False,
    # With no arguments the tool shows live state, not the help (docs/axi-standard.md, principle 8).
    invoke_without_command=True,
)
session_app = typer.Typer(cls=AxiGroup, help="Manage running sessions.", add_completion=False)
repo_app = typer.Typer(cls=AxiGroup, help="List the fleet's repositories.", add_completion=False)
worktree_app = typer.Typer(
    cls=AxiGroup,
    # Two different things share this word, so the help says which is which. "list" is the FLEET
    # view, served by the Gateway, every machine. The other commands are this machine's pool of
    # reusable worktrees, and every one of them runs the cc-worktrees tool - see worktree_pool_ops.
    help="The fleet's worktrees, and this machine's pool of reusable ones.",
    add_completion=False,
)
pool_app = typer.Typer(
    cls=AxiGroup,
    # The SETTING, not the pool itself. `worktree get/return/lease/destroy` act on this machine's
    # pool through cc-worktrees; these three say whether a repository uses one at all, and they are
    # the only way a person turns it on.
    help="Whether a repository uses a pooled worktree, and how many slots it may have.",
    add_completion=False,
    no_args_is_help=True,
)
machine_app = typer.Typer(
    cls=AxiGroup,
    help="List machines, search them, start applications and ask for restarts.",
    add_completion=False,
    no_args_is_help=True,
)
director_app = typer.Typer(
    cls=AxiGroup,
    help="List the Directors this account is running, on every machine.",
    add_completion=False,
    no_args_is_help=True,
)
mission_app = typer.Typer(
    cls=AxiGroup,
    help="Create, list, attach and end Missions - the bodies of work sessions join.",
    add_completion=False,
    no_args_is_help=True,
)
message_app = typer.Typer(cls=AxiGroup, help="Send messages between sessions.", add_completion=False)
fleet_manager_app = typer.Typer(
    cls=AxiGroup,
    help="Show, set, or clear which session is this account's one Fleet Manager.",
    add_completion=False,
    no_args_is_help=True,
)
settings_app = typer.Typer(
    cls=AxiGroup,
    help="Read and write CC Director settings.", add_completion=False, no_args_is_help=True
)
schedule_app = typer.Typer(
    cls=AxiGroup,
    help="Manage Gateway schedules.", add_completion=False, no_args_is_help=True
)
workflow_app = typer.Typer(
    cls=AxiGroup,
    help="Read and author the fleet's shared Workflows on the Gateway.",
    add_completion=False,
    no_args_is_help=True,
)
skill_app = typer.Typer(
    cls=AxiGroup,
    help="Read and author fleet Skills, held on the Gateway.",
    add_completion=False,
    no_args_is_help=True,
)
setup_app = typer.Typer(
    cls=AxiGroup,
    help="Install, update, and repair DevThrottle.", add_completion=False, no_args_is_help=True
)
email_app = typer.Typer(
    cls=AxiGroup,
    help="Send email to the account owner.", add_completion=False, no_args_is_help=True
)
diag_app = typer.Typer(
    cls=AxiGroup,
    help="Run network diagnostics: direct or relayed, and speed.",
    add_completion=False,
    no_args_is_help=True,
)
autostart_app = typer.Typer(
    cls=AxiGroup,
    help="Start the Gateway at login: on, off, or status.",
    add_completion=False,
    no_args_is_help=True,
)
browser_app = typer.Typer(
    cls=AxiGroup,
    # The verb stays "browser": it is the resource name agents already hold, in the actions registry
    # and in the attach command baked into the fold. The HELP says "profile", which is what the thing
    # actually is - a dedicated signed-in profile inside Chrome or Edge, not a browser we installed.
    help="Manage this machine's browser profiles for agents to drive.",
    add_completion=False,
    no_args_is_help=True,
)
fleet_app = typer.Typer(
    cls=AxiGroup,
    help=(
        "The Fleet Manager's news, events, standing preferences and digest.\n\n"
        "Stored news is a ready, finding or decision record; events are the stops and deaths of the "
        "sessions it owns; the digest is what it reads at the start of a conversation."
    ),
    add_completion=False,
    no_args_is_help=True,
)
app.add_typer(session_app, name="session")
app.add_typer(repo_app, name="repo")
app.add_typer(worktree_app, name="worktree")
worktree_app.add_typer(pool_app, name="pool")
app.add_typer(machine_app, name="machine")
app.add_typer(director_app, name="director")
app.add_typer(mission_app, name="mission")
app.add_typer(message_app, name="message")
app.add_typer(fleet_manager_app, name="fleet-manager")
app.add_typer(settings_app, name="settings")
app.add_typer(schedule_app, name="schedule")
app.add_typer(workflow_app, name="workflow")
app.add_typer(skill_app, name="skill")
app.add_typer(setup_app, name="setup")
app.add_typer(email_app, name="email")
app.add_typer(diag_app, name="diag")
app.add_typer(autostart_app, name="autostart")
app.add_typer(browser_app, name="browser")
app.add_typer(fleet_app, name="fleet")
console = Console()

_ACTIONS = [
    {
        "id": "fleet-digest",
        "description": (
            "Everything the Fleet Manager reads at the start of a conversation: open outcome records, the "
            "sessions it owns with the Wingman's latest reading of each, and the standing preferences."
        ),
        "command": "cc-devthrottle fleet digest [--session <id>] [--json]",
        "mutatesState": False,
        "args": [{"name": "session", "required": False}],
    },
    {
        "id": "fleet-events",
        "description": (
            "The events about sessions the Fleet Manager owns - each stop (with the Wingman's reading) or death - "
            "kept until acknowledged, one page at a time with a cursor. A stop still waiting for its reading says so "
            "and cannot be acknowledged yet. The Gateway also delivers them, at least once, as one prompt while the "
            "Fleet Manager is waiting for a prompt."
        ),
        "command": "cc-devthrottle fleet events [--all] [--count N] [--cursor <nextCursor>] [--every-page] [--json]",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "fleet-ack",
        "description": (
            "Acknowledge Fleet Manager events by id once acted on (only the marked Fleet Manager may); --all closes "
            "only the events delivered to this session; all or nothing when an id is unknown or is a stop still "
            "waiting for its reading."
        ),
        "command": "cc-devthrottle fleet ack <id> [<id> ...] | cc-devthrottle fleet ack --all",
        "mutatesState": True,
        "args": [{"name": "id", "required": False}],
    },
    {
        "id": "fleet-ready",
        "description": "File a READY record: work ready for the owner, kept open until they answer it.",
        "command": (
            'cc-devthrottle fleet ready "<title>" --pr <link> --risk low|medium|high '
            '--checks passed|failed|none --tested "<how>" --reviewed-by "<who>" '
            '--change "<one sentence for a user>" [--session <id> [--verdict <verdictId>]] [--advice "<one line>" [--pick "<option key>"]]'
        ),
        "mutatesState": True,
        "args": [{"name": "title", "required": True}, {"name": "pr", "required": True},
                 {"name": "risk", "required": True}, {"name": "checks", "required": True},
                 {"name": "tested", "required": True}, {"name": "reviewed_by", "required": True},
                 {"name": "change", "required": True}, {"name": "session", "required": False},
                 {"name": "verdict", "required": False}, {"name": "advice", "required": False}, {"name": "pick", "required": False}],
    },
    {
        "id": "fleet-finding",
        "description": "File a FINDING record: a finished report or investigation, answer first.",
        "command": (
            'cc-devthrottle fleet finding "<title>" --answer "<answer>" [--reason "<why>"] '
            '[--link <url> ...] [--session <id> [--verdict <verdictId>]] [--advice "<one line>" [--pick "<option key>"]]'
        ),
        "mutatesState": True,
        "args": [{"name": "title", "required": True}, {"name": "answer", "required": True},
                 {"name": "reason", "required": False}, {"name": "link", "required": False},
                 {"name": "session", "required": False}, {"name": "verdict", "required": False},
                 {"name": "advice", "required": False}, {"name": "pick", "required": False}],
    },
    {
        "id": "fleet-decision",
        "description": "File a DECISION record: a question only the owner can settle, with two or more options.",
        "command": (
            'cc-devthrottle fleet decision "<title>" --question "<q>" --option "<a>" --option "<b>" '
            '[--recommend "<a>"] [--why "<why>"] [--session <id> [--verdict <verdictId>]] [--advice "<one line>" [--pick "<option key>"]]'
        ),
        "mutatesState": True,
        "args": [{"name": "title", "required": True}, {"name": "question", "required": True},
                 {"name": "option", "required": True}, {"name": "recommend", "required": False},
                 {"name": "why", "required": False}, {"name": "session", "required": False},
                 {"name": "verdict", "required": False}, {"name": "advice", "required": False},
                 {"name": "pick", "required": False}],
    },
    {
        "id": "fleet-outcomes",
        "description": "List the account's outcome records, newest first (default: open).",
        "command": "cc-devthrottle fleet outcomes [--status open|answered|all] [--kind ready|finding|decision] [--json]",
        "mutatesState": False,
        "args": [{"name": "status", "required": False}, {"name": "kind", "required": False}],
    },
    {
        "id": "fleet-show",
        "description": "Show one outcome record in full.",
        "command": "cc-devthrottle fleet show <id> [--json]",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "fleet-answer",
        "description": "Close an outcome record with the owner's words, exactly. An answer is final.",
        "command": 'cc-devthrottle fleet answer <id> "<the owner\'s words, exactly>"',
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "answer", "required": True}],
    },
    {
        "id": "fleet-advise",
        "description": (
            "Write the Fleet Manager's one line of advice on an open outcome record, and optionally the Wingman option "
            "it would pick; shown to the owner in the walkthrough. Only the marked Fleet Manager may. One line, at most "
            "300 characters; a pick must be one of the options of the session's current Wingman reading."
        ),
        "command": 'cc-devthrottle fleet advise <id> "<one line of advice>" [--pick "<option key>"] [--json]',
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "advice", "required": True},
                 {"name": "pick", "required": False}],
    },
    {
        "id": "fleet-prefer",
        "description": "Keep one of the owner's standing preferences, in their own words.",
        "command": 'cc-devthrottle fleet prefer "<preference, verbatim>"',
        "mutatesState": True,
        "args": [{"name": "text", "required": True}],
    },
    {
        "id": "fleet-preferences",
        "description": "List the owner's standing preferences, oldest first.",
        "command": "cc-devthrottle fleet preferences [--json]",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "fleet-forget",
        "description": "Remove one standing preference.",
        "command": "cc-devthrottle fleet forget <preference id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
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
        "id": "director-restore",
        "description": (
            "Bring a drained fleet back onto a Director (after a restart, the NEW one). The DIRECTOR starts "
            "every seat on its own credential, under the owner the seat had when it was captured - an owner "
            "restarted in the same drain comes back first and is named by its new id. You name no owner and "
            "cannot. Each seat that fails is reported on that seat and the rest carry on; a seat that came "
            "back is never started twice. This is the restore step of the director-restart skill."
        ),
        "command": "cc-devthrottle director restore <workspace> --director <new director id> [--seat <id>] [--seed <id>=<path>] [--force-seat <id>] [--wait-seconds <n>]",
        "mutatesState": True,
        "args": [
            {"name": "workspace", "required": True},
            {"name": "director", "required": True},
            {"name": "seat", "required": False},
            {"name": "seed", "required": False},
            {"name": "force-seat", "required": False},
            {"name": "wait-seconds", "required": False},
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
            "reply says 'pending' when it deferred. A hold ends when it is released, when the owner "
            "types or speaks into the session, when the timer expires, or when the session starts work "
            "nothing explains. Another agent's message does not end it."
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
            "Compact a session's context and THEN type a prompt into it - the owner's rescue for a stuck "
            "session. The Gateway REFUSES this to every agent: only the owner types into a session. An "
            "agent compacts with session-compact and then queues a message with message-send."
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
        "description": (
            "Queue a message for a session. You may message only the session that started you and the "
            "sessions you started; the Gateway refuses anyone else, and limits you to 6 messages an hour "
            "and 1 per recipient every 10 minutes. Nothing is typed into the recipient: it reads the full "
            "text from its inbox when it is free, so the answer is 'queued', never 'delivered'. Target "
            "'all' queues one copy for each of your workers. Messages are rare - put what you would have "
            "said in your report instead. --reply-wanted (one session only) asks for a reply without "
            "waiting: it prints a correlation id, the reply arrives in your inbox, and if none arrives by "
            "the deadline (--reply-by minutes, 60 by default) a no-reply notice arrives instead."
        ),
        "command": 'cc-devthrottle message send <target|all> "<message>" [--reply-wanted] [--reply-by <minutes>]',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "message", "required": True},
            {"name": "reply-wanted", "required": False},
            {"name": "reply-by", "required": False},
        ],
    },
    {
        "id": "message-reply",
        "description": (
            "Answer a message that asked for a reply. The id is the correlation id (or message id) "
            "'message inbox' showed. The reply goes to whoever asked, whatever your relationship to it; "
            "only the session the question was sent to may answer, once. It is not held to the message "
            "limits, and a reply after the deadline still arrives."
        ),
        "command": 'cc-devthrottle message reply <id> "<answer>"',
        "mutatesState": True,
        "args": [
            {"name": "id", "required": True},
            {"name": "answer", "required": True},
        ],
    },
    {
        "id": "message-inbox",
        "description": (
            "Read THIS session's inbox: every unread message in full, each marked read by this call. "
            "Reading is the acknowledgement - a message stays open until its recipient runs this. "
            "A reply is shown with the question it answers, and a no-reply notice with the question that "
            "went unanswered. "
            "--all adds the newest 200 messages read in the last 24 hours, so a read whose answer was lost "
            "can be recovered - for 24 hours, and only by asking."
        ),
        "command": "cc-devthrottle message inbox [--all] [--json]",
        "mutatesState": True,
        "args": [
            {"name": "all", "required": False},
            {"name": "json", "required": False},
        ],
    },
    {
        "id": "fleet-selftest",
        "description": "Windows only: check that a message to a throwaway worker is queued.",
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
        "id": "fleet-manager-show",
        "description": "Show which session this account has marked as its one Fleet Manager, or none.",
        "command": "cc-devthrottle fleet-manager show",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "fleet-manager-set",
        "description": (
            "Mark a session as this account's one Fleet Manager, replacing any earlier mark. "
            "With no session, marks the session running the command."
        ),
        "command": "cc-devthrottle fleet-manager set [<session>]",
        "mutatesState": True,
        "args": [{"name": "session", "required": False}],
    },
    {
        "id": "fleet-manager-clear",
        "description": "Remove this account's Fleet Manager mark.",
        "command": "cc-devthrottle fleet-manager clear",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "session-hand-over",
        "description": (
            "Hand a running session to the Fleet Manager, or back to the owner. The owner's change: the Gateway "
            "allows it only from the owner's own phone or browser and refuses every session key, the Fleet "
            "Manager's included."
        ),
        "command": "cc-devthrottle session hand-over <session> --to fleet-manager|owner [--json]",
        "mutatesState": True,
        "args": [{"name": "session", "required": True}, {"name": "to", "required": True}],
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
    {
        "id": "worktree-list",
        "description": (
            "List the fleet's worktrees, on every machine, with the Gateway's verdict, the size, and "
            "which session is in each."
        ),
        "command": "cc-devthrottle worktree list [--repo <name>] [--state <verdict>]",
        "mutatesState": False,
        "args": [
            {"name": "repo", "required": False},
            {"name": "state", "required": False},
        ],
    },
    {
        "id": "worktree-list-pool",
        "description": (
            "List this machine's pool of reusable worktrees (cc-worktrees): slot, state (free, "
            "in-use, held), holder, and the reason a held one is held."
        ),
        "command": "cc-devthrottle worktree list --pool [--repo <path>] [--fields <a,b>]",
        "mutatesState": False,
        "args": [
            {"name": "repo", "required": False},
            {"name": "fields", "required": False},
        ],
    },
    {
        "id": "worktree-get",
        "description": (
            "Take a pooled worktree on this machine to work in. It comes back reset to the remote "
            "default branch with its build output kept, and with the lease that returns it."
        ),
        "command": "cc-devthrottle worktree get --repo <path> --holder <text> [--pool-size N]",
        "mutatesState": True,
        "args": [
            {"name": "repo", "required": True},
            {"name": "holder", "required": True},
            {"name": "pool_size", "required": False},
        ],
    },
    {
        "id": "worktree-return",
        "description": (
            "Give a pooled worktree back. It is reset and freed only when its work is proven to have "
            "landed on the remote; anything unproven is held with the reason and left untouched."
        ),
        "command": "cc-devthrottle worktree return <path-or-slot> --lease <id> [--repo <path>]",
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "lease", "required": True},
            {"name": "repo", "required": False},
        ],
    },
    {
        "id": "worktree-lease",
        "description": "Take one named pooled worktree rather than whichever one is free.",
        "command": (
            "cc-devthrottle worktree lease <path-or-slot> --holder <text> [--reclaim-held] "
            "[--repo <path>]"
        ),
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "holder", "required": True},
            {"name": "reclaim_held", "required": False},
            {"name": "repo", "required": False},
        ],
    },
    {
        "id": "worktree-destroy",
        "description": (
            "Remove one pooled worktree. A dry run that says what it would remove unless --yes, and "
            "refused unless the work in it is proven landed at that moment."
        ),
        "command": (
            "cc-devthrottle worktree destroy <path-or-slot> [--yes] [--allow-held] [--allow-in-use] "
            "[--repo <path>]"
        ),
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "yes", "required": False},
            {"name": "allow_held", "required": False},
            {"name": "allow_in_use", "required": False},
            {"name": "repo", "required": False},
        ],
    },
    {
        "id": "worktree-pool-status",
        "description": (
            "Say whether a repository runs its sessions in a pooled worktree, with how many slots, "
            "and when a change to that takes effect."
        ),
        "command": "cc-devthrottle worktree pool status [--repo <path>]",
        "mutatesState": False,
        "args": [
            {"name": "repo", "required": False},
        ],
    },
    {
        "id": "worktree-pool-on",
        "description": (
            "Turn pooled worktrees ON for a repository: every session opened in it from then on runs "
            "in a slot of its own instead of the shared checkout. Pool size defaults to 4."
        ),
        "command": "cc-devthrottle worktree pool on --repo <path> [--size N]",
        "mutatesState": True,
        "args": [
            {"name": "repo", "required": True},
            {"name": "size", "required": False},
        ],
    },
    {
        "id": "worktree-pool-off",
        "description": (
            "Turn pooled worktrees OFF for a repository. Sessions already running in a slot are "
            "untouched and still give their slot back when they close."
        ),
        "command": "cc-devthrottle worktree pool off --repo <path>",
        "mutatesState": True,
        "args": [
            {"name": "repo", "required": True},
        ],
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
    """Open a profile's sign-in page, or mark the sign-in done with --done."""
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
    """Close a running browser cleanly; its login is kept."""
    browser_ops.stop_browser(name, json_output)


@browser_app.command("attach")
def browser_attach(
    name: str = typer.Argument(..., help="Browser name or id."),
) -> None:
    """Print the export lines that attach the harness to a browser.

    Only those lines, so: eval "$(cc-devthrottle browser attach 'Name')"
    """
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
    ctx: typer.Context,
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Unified DevThrottle command-line surface."""
    if ctx.invoked_subcommand is None:
        show_live_state()


@app.command()
def actions(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List the actions an agent can discover, with their commands."""
    if json_output:
        print(json.dumps({"actions": _ACTIONS}, indent=2))
        return

    # The AXI list shape (docs/axi-standard.md): every id and command in full, one row each. The old
    # table wrapped long commands across rows at 80 columns and drew its borders in non-ASCII.
    records = [
        {
            "id": action["id"],
            "command": action["command"],
            "changes-state": "yes" if action["mutatesState"] else "no",
        }
        for action in _ACTIONS
    ]
    changing = sum(1 for action in _ACTIONS if action["mutatesState"])
    axi_output.write_blocks(
        sys.stdout,
        axi_output.format_count(
            len(records),
            breakdown=[("changes-state", changing), ("read-only", len(records) - changing)],
        ),
        axi_output.render_list("actions", ["id", "command", "changes-state"], records),
        axi_output.format_help(["cc-devthrottle actions --json", "cc-devthrottle <group> <command> --help"]),
    )


@session_app.command("list")
def session_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
    state: str = typer.Option(
        None, "--state", help="Only these states, comma separated: needs-you, working, ready, snoozed, crashed."
    ),
    repo: str = typer.Option(None, "--repo", help="Only this repository: its folder name or full path."),
    machine: str = typer.Option(None, "--machine", help="Only sessions on this machine."),
    fields: str = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: id,name,state,repo. "
        "Valid: id, name, state, repo, machine, number, model, agent, mission, path.",
    ),
) -> None:
    """List every session in the fleet: id, name, state and repository."""
    list_sessions(json_output, state=state, repo=repo, machine=machine, fields=fields)


@repo_app.command("list")
def repo_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
    dirty: bool = typer.Option(False, "--dirty", help="Only repositories with uncommitted work (same as --state dirty)."),
    state: str = typer.Option(None, "--state", help="Only these states, comma separated: dirty, clean."),
    repo: str = typer.Option(None, "--repo", help="Only this repository: its folder name or full path."),
    machine: str = typer.Option(None, "--machine", help="Only repositories on this machine."),
    fields: str = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: name,path,machine,state. "
        "Valid: name, path, machine, state, branch, uncommitted, ahead, behind, behind-main, worktrees, "
        "safe-to-reap, worktree-bytes, provider, org, remote, director, provisional.",
    ),
) -> None:
    """List the fleet's repositories: name, full path, machine and state."""
    from .repo_ops import list_repositories

    list_repositories(json_output, dirty_only=dirty, state=state, repo=repo, machine=machine, fields=fields)


# The pooled-worktree commands below take their arguments exactly as cc-worktrees takes them and
# forward them untouched, so a flag never means one thing here and another there, and a flag added to
# cc-worktrees works through this command the day it lands. cc-worktrees rejects an argument it does
# not know, with its own usage exit code, so nothing is silently ignored on the way through.
_PASS_THROUGH = {"allow_extra_args": True, "ignore_unknown_options": True}


@worktree_app.command("list")
def worktree_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
    repo: str = typer.Option(
        None,
        "--repo",
        help="Only this repository: its folder name or full path for the fleet listing, its PATH with --pool.",
    ),
    state: str = typer.Option(
        None,
        "--state",
        help="Fleet listing only. Only these states, comma separated: needs-attention, in-use, safe-to-reap, verifying.",
    ),
    machine: str = typer.Option(
        None, "--machine", help="Fleet listing only. Only worktrees on this machine."
    ),
    fields: str = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Fleet listing default: path,repo,machine,state; valid: path, repo, "
        "machine, state, branch, reason, sessions, bytes, last-activity, repo-path, director, data-age, provisional. "
        "With --pool: repo, slot, path, state, holder, reason, updated.",
    ),
    pool: bool = typer.Option(
        False, "--pool", help="List this machine's cc-worktrees pool instead of the fleet."
    ),
) -> None:
    """List the fleet's worktrees, or this machine's pool of reusable ones with --pool.

    The fleet view is every machine's worktrees as the Gateway sees them, with verdicts, sizes and
    which session is in each. With --pool it is this machine's pool, answered by cc-worktrees:
    slot, state (free, in-use, held), holder and reason.
    """
    if pool:
        from . import worktree_pool_ops

        for name, value in (("--state", state), ("--machine", machine)):
            if value is not None:
                worktree_pool_ops.usage_error(
                    f"{name} filters the fleet listing and has no meaning for the pool",
                    ["cc-devthrottle worktree list --pool [--repo <path>] [--fields <a,b>]"],
                    json_output,
                )
        arguments = ["list"]
        if repo is not None:
            arguments += ["--repo", repo]
        if fields is not None:
            arguments += ["--fields", fields]
        if json_output:
            arguments += ["--json"]
        worktree_pool_ops.run_pool_command(arguments)

    from .repo_ops import list_worktrees

    list_worktrees(json_output, repo=repo, state=state, machine=machine, fields=fields)


@worktree_app.command("get", context_settings=_PASS_THROUGH)
def worktree_get(ctx: typer.Context) -> None:
    """Take a free pooled worktree to work in. Runs: cc-worktrees get.

    A free slot is handed out, or a new one is created beside the repository while the pool is
    under its size.

      cc-devthrottle worktree get --repo <path> --holder <text> [--pool-size N] [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.run_pool_command(["get"] + list(ctx.args))


@worktree_app.command("return", context_settings=_PASS_THROUGH)
def worktree_return(ctx: typer.Context) -> None:
    """Give a pooled worktree back. Runs: cc-worktrees return.

    It is reset and freed only when cc-worktrees can prove its work landed; otherwise it is held
    with the reason and nothing in it is touched.

      cc-devthrottle worktree return <path-or-slot> --lease <id> [--repo <path>] [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.run_pool_command(["return"] + list(ctx.args))


@worktree_app.command("lease", context_settings=_PASS_THROUGH)
def worktree_lease(ctx: typer.Context) -> None:
    """Take one named pooled worktree. Runs: cc-worktrees lease.

      cc-devthrottle worktree lease <path-or-slot> --holder <text> [--reclaim-held] [--repo <path>] [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.run_pool_command(["lease"] + list(ctx.args))


@worktree_app.command("destroy", context_settings=_PASS_THROUGH)
def worktree_destroy(ctx: typer.Context) -> None:
    """Remove one pooled worktree. Runs: cc-worktrees destroy.

    A dry run unless --yes, and refused whatever the recorded state says unless the work is proven
    landed at that moment.

      cc-devthrottle worktree destroy <path-or-slot> [--yes] [--allow-held] [--allow-in-use] [--repo <path>] [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.run_pool_command(["destroy"] + list(ctx.args))


# The three commands below write the SETTING the Director reads on the create path, in the one place
# it reads it (config.json, worktreePool.repoDefaults). They run no tool. Without them the setting is
# unreachable: step 4 shipped the Director side and left the only way to turn it on as editing a JSON
# file by hand.


@pool_app.command("status")
def worktree_pool_status(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, one object."
    ),
    repo: str = typer.Option(
        ".", "--repo", help="The repository, by full path. Defaults to the current directory."
    ),
) -> None:
    """Is this repository set to use a pooled worktree, and how many slots it may have.

      cc-devthrottle worktree pool status [--repo <path>] [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.pool_status(repo, json_output)


@pool_app.command("on")
def worktree_pool_on(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, one object."
    ),
    repo: str = typer.Option(..., "--repo", help="The repository, by full path."),
    size: int = typer.Option(
        None, "--size",
        help="The most slots this repository's pool may hold. Defaults to 4 the first time; "
             "otherwise the size already stored is kept.",
    ),
) -> None:
    """Run this repository's sessions in a pooled worktree.

      cc-devthrottle worktree pool on --repo <path> [--size N] [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.pool_on(repo, size, json_output)


@pool_app.command("off")
def worktree_pool_off(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, one object."
    ),
    repo: str = typer.Option(..., "--repo", help="The repository, by full path."),
) -> None:
    """Stop running this repository's sessions in a pooled worktree.

    Sessions already running in a slot are untouched - each still holds its lease and still gives
    its slot back when it closes. Only the next session opened here is affected.

      cc-devthrottle worktree pool off --repo <path> [--json]
    """
    from . import worktree_pool_ops

    worktree_pool_ops.pool_off(repo, json_output)


@machine_app.command("list")
def machine_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
    state: str = typer.Option(
        None, "--state", help="Only these states, comma separated: online, offline, too-old."
    ),
    fields: str = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: name,state,version. "
        "Valid: name, state, version, pid, started, last-seen.",
    ),
) -> None:
    """List the machines you can search and start applications on.

    Shows each machine's name, state and launcher version.
    """
    from .machine_ops import list_machines

    list_machines(json_output, state=state, fields=fields)


@director_app.command("list")
def director_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
    state: str = typer.Option(
        None, "--state", help="Only these states, comma separated: online, wobbly, offline, stopped."
    ),
    machine: str = typer.Option(None, "--machine", help="Only Directors on this machine."),
    fields: str = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: id,name,machine,state. "
        "Valid: id, name, machine, state, version, pid, user, started, last-seen.",
    ),
) -> None:
    """List every Director this account runs, with the id 'session spawn' takes.

    Pass that id to 'cc-devthrottle session spawn <repo> --director <id>'.
    """
    from .machine_ops import list_directors

    list_directors(json_output, state=state, machine=machine, fields=fields)


@director_app.command("restore")
def director_restore(
    workspace: str = typer.Argument(..., help="The workspace the drain recorded (the id in its restore commands)."),
    director: str = typer.Option(..., "--director", help="The Director that brings the seats back - after a restart, the NEW one. See 'director list'."),
    seat: List[str] = typer.Option([], "--seat", help="Only this seat (its captured session id). Repeat for several. Default: every seat decided restore that has not come back."),
    seed: List[str] = typer.Option([], "--seed", help="<captured session id>=<path>: start that seat from this seed file instead of its handover. Repeatable."),
    force_seat: List[str] = typer.Option([], "--force-seat", help="Start this seat (its captured session id) even though an earlier start of it MAY have landed. Only after checking the session list. Repeatable."),
    wait_seconds: int = typer.Option(600, "--wait-seconds", help="How long to wait for every seat's answer. 0 asks and does not wait, and exits 3 (accepted, not waited)."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Bring a drained fleet back: the Director starts each seat under its old owner.

    Owners come from what the Gateway captured, never from you: an owner restarted in the same drain
    comes back first and is named by its new id. Each seat that fails is reported and the rest carry on.
    A seat still running is not started again, and one whose earlier start may have landed needs --force-seat.

    Exit 0 means every seat asked for came back. Exit 1: a seat failed or is still pending.
    Exit 3: --wait-seconds 0 - accepted, not waited, so nothing is known to have come back.
    """
    from .machine_ops import restore_workspace

    restore_workspace(workspace, director, seat, seed, wait_seconds, json_output, force_seat)


@machine_app.command("apps")
def machine_apps(
    machine: str = typer.Argument(..., help="The computer to look on."),
    query: str = typer.Argument(None, help="Filter by name. Omit to list everything installed."),
    count: int = typer.Option(100, "--count", "-n", help="Largest number of results to return."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
    fields: str = typer.Option(
        None, "--fields", help="Fields to show, comma separated. Default and valid: name, source, path."
    ),
) -> None:
    """List the applications installed on another computer."""
    from .machine_ops import list_apps

    list_apps(machine, query, count, json_output, fields)


@machine_app.command("files")
def machine_files(
    machine: str = typer.Argument(..., help="The computer to search."),
    query: str = typer.Argument(..., help="Filename to find. Use * and ? to match patterns."),
    count: int = typer.Option(200, "--count", "-n", help="Largest number of results to return."),
    seconds: int = typer.Option(20, "--seconds", "-s", help="How long the search may run before it reports what it found."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
    fields: str = typer.Option(
        None, "--fields", help="Fields to show, comma separated. Default and valid: name, size, modified, path."
    ),
) -> None:
    """Find files by name across every drive on another computer.

    The search is bounded by both a result count and a time limit, and says which one stopped it when
    it returns early, so a partial answer is never mistaken for the whole one.
    """
    from .machine_ops import search_files

    search_files(machine, query, count, seconds, json_output, fields)


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
    """Ask for a Director restart on one machine; the owner accepts it once.

    The machine scrutinises the request first; once accepted, the restart runs alone.
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


@session_app.command(name="hand-over")
def hand_over(
    target: str = typer.Argument(..., help="Session to hand over (full id, id prefix, number, or exact name)."),
    to: Optional[str] = typer.Option(
        None, "--to", help="Who owns it afterwards: fleet-manager, or owner (no owning session)."
    ),
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: the Gateway's answer, unchanged."
    ),
) -> None:
    """Hand a running session to the Fleet Manager, or back to the owner.

    The Gateway allows it from the owner's own signed-in phone or browser (the Cockpit's Fleet Manager
    page and session menu), and from the account's Fleet Manager with its own session key - which takes
    a session only when the owner has asked it to. Any other session's key is refused with the reason.

    The Gateway also refuses: a session this account is not running, the Fleet Manager itself, handing to
    a Fleet Manager the account does not have running, a session another running session owns, a session
    that is already where it is being sent, and a session whose Director is too old to change an owner.
    """
    from .fleet_manager_ops import hand_over as _hand_over

    _hand_over(target, to, json_output)


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
    """Type raw text into a session. REFUSED to agents: only the owner may type.

    The Gateway refuses this to every session key and says what to do instead - queue a message
    with `message send`, which the recipient reads when it is free.
    """
    prompt_session(target, text, no_submit=no_submit)


@session_app.command()
def interrupt(
    target: Optional[str] = typer.Argument(
        None, help="Session to interrupt. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Interrupt a session. REFUSED to agents: only the owner interrupts a session."""
    interrupt_session(target)


@session_app.command(name="report")
def report(
    summary: Optional[str] = typer.Argument(
        None, help="What you did, in your own words. One or two sentences."
    ),
    target: Optional[str] = typer.Option(
        None, "--target", help="Session to report for. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Tell the session that owns you what you did, now that your turn has ended.

    This is the last step of delegated work, not a courtesy. Your parent asked you to do something;
    getting back to them is part of doing it - so you send it yourself, in your own words, the moment
    your turn ends. It is QUEUED in their inbox, never typed into them: when they are not working, one
    doorbell line tells them to run 'cc-devthrottle message inbox', and it stays open until they read
    it. It is still held to the six-an-hour message limit, but not to the ten-minute spacing.

    If NO live parent owns you, the USER does, and nothing is sent: you are already red and in his
    queue, so that red is your report. Leave your answer in this session where he will read it.

    If a FLEET MANAGER owns you, nothing is sent either: the Gateway tells it that you stopped, with the
    Wingman's reading, when it is next waiting for a prompt.
    """
    report_to_parent(summary, target)


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
    """Put your hand up to the session driving you when you are blocked.

    Use it when you cannot go on without an answer.

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
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: the Gateway's rows for these sessions, a bare array."
    ),
    fields: str = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: id,name,state,hand,need. "
        "Valid: id, name, state, repo, machine, number, model, agent, mission, path, hand, need.",
    ),
) -> None:
    """List the sessions you are driving, and which of them have their hand up.

    A manager learns what its workers are doing by READING them, not by being messaged "notice me".
    This is that read in one line - who you are driving, what state each is in, and what any of them
    is blocked on.
    """
    list_my_workers(target, json_output=json_output, fields=fields)


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

    A hold ends when it is released, when the owner types or speaks into the session, when the
    --minutes timer runs out, or when the session starts work the Director cannot attribute to
    anyone. Another agent's message does NOT end it: the doorbell that announces a message is
    agent-origin work, and the owner decided on 17 September 2026 that it leaves the hold in place.
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
    """Compact a session's context, then send it a message to get it moving.

    This is the rescue for a STUCK session.

    A session whose context window is full cannot read anything you send it: every message is
    swallowed and the tool just reprints its context-limit line. Compaction is the only thing that
    unblocks it, and this verb also gets it moving again afterwards.

    THE OWNER'S TOOL. The message it sends afterwards is typed into the session, so the Gateway
    refuses this verb to every session key (an agent may not type into a session). An agent rescuing
    its own worker runs `cc-devthrottle session compact` and queues a message instead.

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
    """Print what a session's terminal is showing right now.

    This is how you see what a session is actually doing.
    """
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

    Does NOT kill the session now - it is flagged, and the owning Director's reaper removes it on
    a sweep after the grace period, once it is no longer working. Use this at
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
        help="WHO OWNS the new session. REQUIRED when you spawn from inside a session - there is no "
        "default, because who a session answers to is too important to be decided by an environment "
        "variable. Pass 'self' to own it yourself (it stays quiet and reports back to you), or 'none' (same "
        "as --standalone) to spawn a peer that answers to the USER. An explicit session id is accepted only "
        "when it is your own: the Gateway refuses a session that names another session as the owner, "
        "because the owner is who the new session may message. A person spawning from the desktop or the Cockpit needs none of this: a "
        "session a person opens is the user's.",
    ),
    why: Optional[str] = typer.Option(
        None,
        "--why",
        help="REQUIRED with --standalone from inside a session: why this work is the USER's rather than "
        "yours. It is printed with the result, so the reason sits in your own transcript beside the "
        "session it explains.",
    ),
    standalone: bool = typer.Option(
        False,
        "--standalone",
        help="Spawn a session that answers to the USER, not to you: no controller, even when run from "
        "inside a session. The same declaration as --controlled-by none, spelled for the common case - "
        "work you are starting on the user's behalf rather than work you will collect yourself.",
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
        help="Attach the new session to a Mission by its id at spawn (fleet.html). "
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
    """Open a new session here, on another machine, or on one named Director.

    Use --machine for another computer, or --director for one named Director.
    """
    spawn_session(
        repo, agent, prompt, name, purpose, command, command_args, controlled_by, args, standalone, why, role,
        machine, mission, workflow_run, director,
    )


@fleet_manager_app.command("show")
def fleet_manager_show(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output JSON: {\"sessionId\": <id or null>}."),
) -> None:
    """Show which session is this account's Fleet Manager, or none."""
    fleet_manager_ops.show(json_output)


@fleet_manager_app.command("set")
def fleet_manager_set(
    session: Optional[str] = typer.Argument(
        None, help="The session to mark: its number, an id prefix, or its name. Omit to mark this session."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output JSON: {\"sessionId\": <id>}."),
) -> None:
    """Mark a session as this account's one Fleet Manager, replacing any earlier mark."""
    fleet_manager_ops.set_mark(session, json_output)


@fleet_manager_app.command("clear")
def fleet_manager_clear(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output JSON: {\"sessionId\": null}."),
) -> None:
    """Remove this account's Fleet Manager mark."""
    fleet_manager_ops.clear(json_output)


@mission_app.command("create")
def mission_create(
    name: str = typer.Argument(..., help="Human-friendly name for the Mission."),
) -> None:
    """Create a Mission record on the Gateway and print its id."""
    mission_ops.create_mission(name)


@mission_app.command("list")
def mission_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
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
        help="Show only this state: active, complete, removed, or all. Overrides --all.",
    ),
    name: Optional[str] = typer.Option(
        None, "--name", help="Only missions whose name contains this text, ignoring case."
    ),
    fields: Optional[str] = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: id,name,state. "
        "Valid: id, name, state, why, why-updated, state-changed, run.",
    ),
) -> None:
    """List the Missions on the Gateway (active ones by default): id, name and state."""
    mission_ops.list_missions(
        json_output, state=state or ("all" if show_all else None), name=name, fields=fields
    )


@mission_app.command("rename")
def mission_rename(
    mission: str = typer.Argument(
        ..., help="The Mission to rename: its id, an id prefix, or part of its name."
    ),
    name: str = typer.Argument(..., help="The new display name."),
) -> None:
    """Rename a Mission; its id stays, so attached sessions stay attached."""
    mission_ops.rename_mission(mission, name)


@mission_app.command("complete")
def mission_complete(
    mission: str = typer.Argument(
        ..., help="The Mission to complete: its id, an id prefix, or part of its name."
    ),
) -> None:
    """Mark a Mission as finished; it leaves the default list but is kept.

    This is the ending to use when the work is done.
    """
    mission_ops.end_mission(mission, "complete")


@mission_app.command("remove")
def mission_remove(
    mission: str = typer.Argument(
        ..., help="The Mission to remove: its id, an id prefix, or part of its name."
    ),
) -> None:
    """Remove a Mission that should not exist; soft, so the record is kept.

    For a duplicate or a mistake. Use 'mission complete' for finished work.
    """
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
    """Attach an existing session to a Mission, moving it from any other."""
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
    """Check each device's network path from the Gateway.

    Per connected device: direct-vs-DERP-relay and latency, plus UDP/NAT health. Runs on the Gateway with no phone and no app open - the check an agent uses to tell "warming up on
    the relay" apart from "genuinely slow".
    """
    diag_ops.show_network(json_output)


@diag_app.command("results")
def diag_results(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Show recent speed-test results from the app or Cockpit."""
    diag_ops.show_results(json_output)


@message_app.command("send")
def message_send(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name - or 'all' for your workers."),
    message: str = typer.Argument(..., help="The message text. It may span lines; it is read, never typed."),
    everyone: bool = typer.Option(
        False,
        "--everyone",
        help="Queue a copy for EVERY session in the account, not just your workers. It needs a "
        "human-issued grant (--grant) and a --reason, and is refused otherwise (issue #1229). Without "
        "this flag, 'all' reaches only the sessions you started.",
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
    reply_wanted: bool = typer.Option(
        False,
        "--reply-wanted",
        help="Ask the recipient for a reply. The Gateway gives the message a correlation id, printed "
        "here; the reply arrives in your inbox, and if none comes by the deadline a no-reply notice "
        "arrives instead. Nothing waits for it. One session only, not 'all'.",
    ),
    reply_by: Optional[int] = typer.Option(
        None,
        "--reply-by",
        help="Minutes the recipient has to reply, with --reply-wanted: 1 to 1440, 60 when omitted.",
    ),
) -> None:
    """Queue a message for your supervisor or a worker ('all' for every worker).

    Nothing is typed into the recipient; it reads the message from its inbox when it is free. The
    Gateway refuses any other recipient, and more than 6 messages an hour or 1 per recipient every
    10 minutes - put what you would have said in your report instead.

    Add --everyone (with --reason and --grant) to reach the whole fleet; it is queued the same way.

    Add --reply-wanted to ask for a reply without waiting for it; answer one with 'message reply'.

    Exit code: 0 when the message was queued or an identical one is already waiting unread - for 'all', when that is true of at least one worker - and 1 when nothing was queued and nothing was waiting.
    """
    send_message(target, message, everyone=everyone, reason=reason, grant=grant,
                 reply_wanted=reply_wanted, reply_by=reply_by)


@message_app.command("reply")
def message_reply(
    reply_id: str = typer.Argument(
        ..., metavar="ID",
        help="The correlation id (or message id) of the message you are answering, from 'message inbox'.",
    ),
    text: str = typer.Argument(..., help="The answer. It may span lines; it is read, never typed."),
) -> None:
    """Answer a message that asked for a reply; the answer goes to whoever asked.

    Only the session the question was sent to may answer it, once. A reply is not held to the message
    limits, and one sent after the deadline still arrives. Nothing is typed into the asker; it reads
    the reply from its inbox.
    """
    send_reply(reply_id, text)


@message_app.command("inbox")
def message_inbox(
    include_read: bool = typer.Option(
        False,
        "--all",
        help="Also show the messages you read in the last 24 hours, newest first, at most 200. A read "
        "marks messages read before their text reaches you, so if a read failed part way they are "
        "gone from the plain inbox: this is the only way to get them back, and only for 24 hours after "
        "that read.",
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the Gateway's answer as JSON."),
) -> None:
    """Read your unread messages in full, which marks them read.

    Reading is the acknowledgement: the sender's message stays open until you read it. A message that
    wants a reply shows its correlation id and the command to answer it; a reply shows the question it
    answers; a no-reply notice says which question got no answer by its deadline.
    """
    read_inbox(include_read=include_read, json_output=json_output)


@app.command()
def selftest(
    timeout_ms: int = typer.Option(
        25000, "--timeout-ms", help="Kept for callers that still pass it; nothing waits any more."
    ),
) -> None:
    """Windows only: check that a message to a throwaway worker is queued."""
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
    """Read and author fleet Skills, held on the Gateway."""
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
    """Print a Skill's full instructions; run it just before using it.

    Follow what it says. Supporting files are written to this machine and their paths printed after
    the body. Fails loudly if the Gateway cannot be reached; never proceed from memory."""
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
    """Pull a Skill into a directory for editing.

    Writes skill.json, SKILL.md, and every supporting file at its own relative path.
    """
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
    """Push a directory as a Skill's draft; unseen until published."""
    skill_ops.push_skill(skill_id, directory, note, force)


@skill_app.command("publish")
def skill_publish(
    skill_id: str = typer.Argument(..., help="The skill id."),
) -> None:
    """Publish a Skill's draft; every agent gets it on its next fetch."""
    skill_ops.publish_skill(skill_id)


@skill_app.command("clone")
def skill_clone(
    skill_id: str = typer.Argument(..., help="The skill to copy."),
    new_id: str = typer.Argument(..., help="The new skill id."),
) -> None:
    """Clone a Skill into one you own; how a built-in is customized."""
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
    """Switch a Skill off: out of briefings, fetch refused, kept."""
    skill_ops.set_skill_enabled(skill_id, False)


@skill_app.command("delete")
def skill_delete(
    skill_id: str = typer.Argument(..., help="The skill id."),
    yes: bool = typer.Option(False, "--yes", "-y", help="Do not ask for confirmation."),
) -> None:
    """Archive a Skill (never a built-in); its versions stay readable."""
    skill_ops.delete_skill(skill_id, yes)


@workflow_app.callback()
def workflow_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Read and author the fleet's shared Workflows on the Gateway."""
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
    """Print a Workflow's raw instructions; fetch this and FOLLOW it."""
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
    """Pull a Workflow into a directory for editing.

    Writes workflow.json, instructions.md, and the helper files under helpers/.
    """
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
    """Push a Workflow directory as a draft; creates it if new."""
    workflow_ops.push_workflow(workflow_id, directory, note, force)


@workflow_app.command("publish")
def workflow_publish(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
) -> None:
    """Publish a Workflow's draft; every agent reads it from then on."""
    workflow_ops.publish_workflow(workflow_id)


@workflow_app.command("materialize")
def workflow_materialize(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific published version (default: the current one)."
    ),
) -> None:
    """Write a Workflow's files to this machine and print the paths."""
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
    """List workflow runs, one per execution, newest first."""
    workflow_ops.list_runs(workflow, status, json_output)


@workflow_app.command("run")
def workflow_run(
    run_id: str = typer.Argument(..., help="The run id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one workflow run: version, status, criteria and proof."""
    workflow_ops.show_run(run_id, json_output)


@workflow_app.command("enable")
def workflow_enable(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
) -> None:
    """Turn a Workflow back on; runs and seats resume."""
    workflow_ops.set_workflow_enabled(workflow_id, True)


@workflow_app.command("disable")
def workflow_disable(
    workflow_id: str = typer.Argument(..., help="The workflow id (built-ins included)."),
) -> None:
    """Turn a Workflow off: no new runs or seats; nothing deleted."""
    workflow_ops.set_workflow_enabled(workflow_id, False)


# "workflow reset" was retired with the Shared Workflow Library phase 3: built-ins are read-only,
# can never diverge from the shipped content, and have nothing to reset.


@workflow_app.command("clone")
def workflow_clone(
    workflow_id: str = typer.Argument(..., help="The source workflow id (e.g. mission)."),
    new_id: str = typer.Argument(..., help="The id for the clone (a fresh slug, never a built-in id)."),
) -> None:
    """Clone a Workflow into a new editable Workflow you own.

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
    """Archive a custom Workflow (never a built-in); history remains."""
    workflow_ops.delete_workflow(workflow_id, yes)


@schedule_app.command("list")
def schedule_list(
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output raw JSON: every field, a bare array. Filters still apply."
    ),
    enabled: Optional[bool] = typer.Option(
        None, "--enabled/--disabled", help="Only enabled schedules, or only disabled ones."
    ),
    machine: Optional[str] = typer.Option(None, "--machine", help="Only schedules that run on this machine."),
    fields: Optional[str] = typer.Option(
        None,
        "--fields",
        help="Fields to show, comma separated. Default: id,name,enabled,next-run. "
        "Valid: id, name, enabled, next-run, machine, kind, cron, run-at, time-zone, work-list, path, "
        "last-fired, last-status, notify, created.",
    ),
) -> None:
    """List every schedule: id, name, whether enabled, and next run."""
    schedule_ops.list_jobs(json_output, enabled=enabled, machine=machine, fields=fields)


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
    """Start the Gateway when you log in."""
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
    """Send one email to the account owner, the only recipient there can be.

    There is no way to address anyone else. Passes only a subject, body, and any attachments to the Gateway, which relays it to the cloud
    with your account token; the cloud resolves the owner and sends. Use it to escalate from an
    unattended or scheduled run, or to send yourself a report to read offline.
    """
    email_ops.send_owner(subject, body, html, attach, json_output)


# ---- fleet: the Fleet Manager's records, preferences and digest ---------------------------------------

_JSON_OPT = typer.Option(False, "--json", "-j", help="Output the Gateway's answer as JSON.")
_SESSION_ABOUT = typer.Option(
    None, "--session", "-s",
    help="The session this is about: id, id prefix, number, or exact name.",
)
_ADVICE = typer.Option(
    None, "--advice",
    help="One line of advice for the owner: what you know and the Wingman does not. At most 300 characters.",
)
_VERDICT = typer.Option(
    None, "--verdict",
    help="The verdictId of the stop event this record is about, exactly. Needs --session.",
)
_PICK = typer.Option(
    None, "--pick",
    help="The key of the Wingman option you would pick for the session, exactly. Needs --advice.",
)


@fleet_app.command("ready")
def fleet_ready(
    title: str = typer.Argument(..., help="One line naming what is ready."),
    pr: str = typer.Option(..., "--pr", help="The full pull request link."),
    risk: str = typer.Option(..., "--risk", help="low, medium or high."),
    checks: str = typer.Option(..., "--checks", help="passed, failed or none."),
    tested: str = typer.Option(..., "--tested", help="How it was tested."),
    reviewed_by: str = typer.Option(..., "--reviewed-by", help="Who reviewed it."),
    change: str = typer.Option(..., "--change", help="One sentence on what changed, for a user."),
    session: Optional[str] = _SESSION_ABOUT,
    verdict: Optional[str] = _VERDICT,
    advice: Optional[str] = _ADVICE,
    pick: Optional[str] = _PICK,
    json_output: bool = _JSON_OPT,
) -> None:
    """File a READY record: work that is ready for the owner."""
    fleet_ops.file_ready(title, pr, risk, checks, tested, reviewed_by, change, session, json_output,
                         advice=advice, pick=pick, verdict=verdict)


@fleet_app.command("finding")
def fleet_finding(
    title: str = typer.Argument(..., help="One line naming what was found."),
    answer: str = typer.Option(..., "--answer", help="The answer, first."),
    reason: Optional[str] = typer.Option(None, "--reason", help="The reason."),
    link: Optional[List[str]] = typer.Option(None, "--link", help="A full link to a report (repeatable)."),
    session: Optional[str] = _SESSION_ABOUT,
    verdict: Optional[str] = _VERDICT,
    advice: Optional[str] = _ADVICE,
    pick: Optional[str] = _PICK,
    json_output: bool = _JSON_OPT,
) -> None:
    """File a FINDING record: a report or investigation that is finished."""
    fleet_ops.file_finding(title, answer, reason, link, session, json_output, advice=advice, pick=pick,
                           verdict=verdict)


@fleet_app.command("decision")
def fleet_decision(
    title: str = typer.Argument(..., help="One line naming the decision."),
    question: str = typer.Option(..., "--question", help="The question."),
    option: Optional[List[str]] = typer.Option(None, "--option", help="One option (give two or more)."),
    recommend: Optional[str] = typer.Option(None, "--recommend", help="The option you recommend, exactly as given."),
    why: Optional[str] = typer.Option(None, "--why", help="Why you recommend it."),
    session: Optional[str] = _SESSION_ABOUT,
    verdict: Optional[str] = _VERDICT,
    advice: Optional[str] = _ADVICE,
    pick: Optional[str] = _PICK,
    json_output: bool = _JSON_OPT,
) -> None:
    """File a DECISION record: something only the owner can settle."""
    fleet_ops.file_decision(title, question, option, recommend, why, session, json_output,
                            advice=advice, pick=pick, verdict=verdict)


@fleet_app.command("outcomes")
def fleet_outcomes(
    status: str = typer.Option("open", "--status", help="open, answered or all."),
    kind: Optional[str] = typer.Option(None, "--kind", help="ready, finding or decision."),
    count: int = typer.Option(50, "--count", "-n", help="Largest number of records on one page (1-200)."),
    cursor: Optional[str] = typer.Option(None, "--cursor", help="Continue after an earlier page: its nextCursor."),
    every_page: bool = typer.Option(False, "--all", help="Follow every page to the end and list every record."),
    json_output: bool = _JSON_OPT,
) -> None:
    """List the account's outcome records, newest first.

    One page at a time, or every page with --all.
    """
    fleet_ops.list_outcomes(status, kind, count, json_output, cursor=cursor, every_page=every_page)


@fleet_app.command("show")
def fleet_show(
    outcome_id: str = typer.Argument(..., metavar="ID", help="The record's id, or the start of it."),
    json_output: bool = _JSON_OPT,
) -> None:
    """Show one outcome record in full."""
    fleet_ops.show_outcome(outcome_id, json_output)


@fleet_app.command("answer")
def fleet_answer(
    outcome_id: str = typer.Argument(..., metavar="ID", help="The record's id, or the start of it."),
    words: str = typer.Argument(..., metavar="ANSWER", help="The owner's words, exactly as they said them."),
    json_output: bool = _JSON_OPT,
) -> None:
    """Close a record with the owner's answer. An answered record is never re-answered."""
    fleet_ops.answer_outcome(outcome_id, words, json_output)


@fleet_app.command("advise")
def fleet_advise(
    outcome_id: str = typer.Argument(..., metavar="ID", help="The record's id, or the start of it."),
    advice: str = typer.Argument(..., metavar="ADVICE", help="One line for the owner, at most 300 characters."),
    pick: Optional[str] = typer.Option(
        None, "--pick", help="The key of the Wingman option you would pick, exactly. Leave it out to clear the pick."),
    json_output: bool = _JSON_OPT,
) -> None:
    """Write your one line of advice on an open record, for the owner's walkthrough.

    Replaces the advice and pick already there. Only the Fleet Manager may.
    """
    fleet_ops.advise_outcome(outcome_id, advice, pick, json_output)


@fleet_app.command("digest")
def fleet_digest(
    session: Optional[str] = typer.Option(
        None, "--session", "-s",
        help="The Fleet Manager session (default: this session). Id, id prefix, number, or exact name.",
    ),
    json_output: bool = _JSON_OPT,
) -> None:
    """Everything the Fleet Manager reads at the start of a conversation."""
    fleet_ops.digest(session, json_output)


@fleet_app.command("events")
def fleet_events(
    show_all: bool = typer.Option(False, "--all", help="Include acknowledged events (newest first)."),
    count: int = typer.Option(50, "--count", "-n", help="Largest number of events on one page (1-200)."),
    cursor: Optional[str] = typer.Option(None, "--cursor", help="Continue after an earlier page: its nextCursor."),
    every_page: bool = typer.Option(False, "--every-page", help="Follow every page to the end and list every event."),
    json_output: bool = _JSON_OPT,
) -> None:
    """List the stops and deaths of sessions a Fleet Manager owns.

    Unacknowledged ones oldest first, one page at a time.
    """
    fleet_ops.list_events(show_all, count, json_output, cursor=cursor, every_page=every_page)


@fleet_app.command("ack")
def fleet_ack(
    event_ids: Optional[List[str]] = typer.Argument(
        None, metavar="[ID]...", help="The event ids, or the start of each."),
    ack_all: bool = typer.Option(False, "--all", help="Acknowledge every unacknowledged event delivered to this session."),
    json_output: bool = _JSON_OPT,
) -> None:
    """Acknowledge events once you have acted on them.

    Nothing is acknowledged if one id is unknown.
    """
    fleet_ops.acknowledge_events(event_ids, ack_all, json_output)


@fleet_app.command("prefer")
def fleet_prefer(
    text: str = typer.Argument(..., help="The owner's preference, in their own words."),
    json_output: bool = _JSON_OPT,
) -> None:
    """Keep one of the owner's standing preferences, exactly as given."""
    fleet_ops.add_preference(text, json_output)


@fleet_app.command("preferences")
def fleet_preferences(json_output: bool = _JSON_OPT) -> None:
    """List the owner's standing preferences, oldest first."""
    fleet_ops.list_preferences(json_output)


@fleet_app.command("forget")
def fleet_forget(
    preference_id: str = typer.Argument(..., metavar="ID", help="The preference's id, or the start of it."),
    json_output: bool = _JSON_OPT,
) -> None:
    """Remove one standing preference."""
    fleet_ops.forget_preference(preference_id, json_output)


if __name__ == "__main__":
    app()
