"""Rewrite the scenario the stand-in Gateway serves. Times are regenerated relative to NOW so the
Last Used column shows a live ladder in whichever screenshot is being taken next.

Every value here is INVENTED. This repository is public: no real machine name, no real account and
no path from anyone's disk appears in a committed screenshot.
"""
import json, os, sys, datetime

HERE = os.path.dirname(os.path.abspath(__file__))
now = datetime.datetime.now(datetime.timezone.utc)


def ago(**kw):
    return (now - datetime.timedelta(**kw)).strftime("%Y-%m-%dT%H:%M:%SZ")


DIRECTORS = [
    {"directorId": "north-1", "machineName": "BUILD-NORTH", "displayName": "North",
     "version": "2.6.0", "startedAt": ago(days=2, hours=4), "lastSeen": ago(seconds=8),
     "controlEndpoint": "http://127.0.0.1:7801", "sessions": 3},
    {"directorId": "studio-1", "machineName": "BUILD-STUDIO", "displayName": "Studio",
     "version": "2.6.0", "startedAt": ago(hours=6, minutes=12), "lastSeen": ago(seconds=4),
     "controlEndpoint": "http://127.0.0.1:7802", "sessions": 1},
    {"directorId": "laptop-1", "machineName": "BUILD-LAPTOP", "displayName": "Laptop",
     "version": "2.5.9", "startedAt": ago(minutes=41), "lastSeen": ago(seconds=6),
     "controlEndpoint": "http://127.0.0.1:7803", "sessions": 0},
]

# THE GATEWAY'S ORDER: most recently used first, then every never-opened repository, by name and by
# path. The screen renders this array index for index and never re-derives it.
FULL_LIST = [
    {"name": "atlas", "path": "/repositories/atlas", "lastUsed": ago(seconds=12), "neverOpened": False},
    {"name": "beacon", "path": "/repositories/beacon", "lastUsed": ago(minutes=7), "neverOpened": False},
    {"name": "cartograph", "path": "/repositories/cartograph", "lastUsed": ago(hours=3), "neverOpened": False},
    # Its NAME and its PATH say different things on purpose: a search for "beacon" finds the row above
    # by its name and this one by its path, which is the whole of the desktop tab's ApplyRepoFilter.
    {"name": "harbour", "path": "/repositories/beacon-notes", "lastUsed": ago(hours=9), "neverOpened": False},
    {"name": "driftwood", "path": "/repositories/driftwood", "lastUsed": ago(days=2), "neverOpened": False},
    {"name": "ember", "path": "/work/ember", "lastUsed": ago(days=124), "neverOpened": False},
    {"name": "foundry", "path": "/work/foundry", "lastUsed": ago(days=400), "neverOpened": False},
    {"name": "almanac", "path": "/scanned-folders/almanac", "lastUsed": None, "neverOpened": True},
    {"name": "quarry", "path": "/scanned-folders/quarry", "lastUsed": None, "neverOpened": True},
    {"name": "zephyr", "path": "/scanned-folders/zephyr", "lastUsed": None, "neverOpened": True},
]

AGENTS = [
    {"type": "ClaudeCode", "displayName": "Claude Code", "defaultModel": "claude-opus-5", "modelLabel": "Opus 5"},
    {"type": "Codex", "displayName": "Codex", "defaultModel": "gpt-5-codex", "modelLabel": "Codex"},
]

SCENARIOS = {
    # The flow: a machine with both halves of the one list on it.
    "full": {"knownRepositories": FULL_LIST, "repoAdd": {"status": 201, "added": True}},
    # A machine the Gateway knows nothing about - the first-run empty state.
    "empty": {"knownRepositories": [], "repoAdd": {"status": 201, "added": True}},
    # THE FAILURE CASE: the Director is not connected to the Gateway, so the route is a 502.
    "director-gone": {"knownRepositories": {"status": 502, "error": "Director not connected"},
                      "repoAdd": {"status": 502, "error": "Director not connected"}},
    # Add succeeds, and the path is genuinely not in the Gateway's catalogue afterwards.
    "add-not-listed": {"knownRepositories": FULL_LIST,
                       "repoAdd": {"status": 201, "added": True, "name": "harbour"}},
    # Add is refused: the path does not exist on that machine.
    "add-refused": {"knownRepositories": FULL_LIST,
                    "repoAdd": {"status": 400, "error": "directory not found: /no/such/folder"}},
}

name = sys.argv[1]
chosen = SCENARIOS[name]
json.dump(
    {"directors": DIRECTORS, "agents": AGENTS,
     "gateway": {"/gateway/director-restart-requests": {"requests": []}},
     **chosen},
    open(os.path.join(HERE, "scenario.json"), "w", encoding="utf-8"), indent=2)
print("scenario:", name)
