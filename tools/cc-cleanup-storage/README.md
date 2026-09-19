# cc-cleanup-storage

Walk a disk, say what fills it, and recommend only what can be proven safe to remove.

This is phases 1 and 2 of the Reclaim the Disk mission. The mission document is
`docs/missions/reclaim-the-disk-2026-09-18/mission.md`.

**It never deletes, moves or changes anything.** There is no removal code in it of any kind. It
reads, measures and explains. Removal with a holding folder arrives in phase 3.

## Commands

    cc-cleanup-storage                               the saved scans on this machine
    cc-cleanup-storage scan "<folder>" [flags]       walk a folder and save what was seen
    cc-cleanup-storage report "<folder>" [flags]     report the saved scan of a folder
    cc-cleanup-storage recommend "<folder>" [flags]  say what is provably safe to remove, and why

Flags:

| Flag | What it does |
|---|---|
| `--json` | Answer as machine-readable text, with every field. |
| `--index-directory <folder>` | Where saved scans live. The default is one folder per machine. |
| `--top <number>` | How many of the largest folders to name, 1 to 1000. Default 20. |
| `--folder-depth <number>` | How deep a scan records folder totals, 1 to 10. Default 2. Scan only. |
| `--help`, `-h` | The help page. |
| `--version` | The version of the tool. Taken with no command word only. |

Exit codes:

| Code | Meaning |
|---|---|
| 0 | The command succeeded. |
| 1 | The command failed, or the scan behind it is a broken instrument. |
| 2 | The command line was wrong: an unknown command, an unknown flag, or a bad value. |

## What a report always says

Every report states the bytes the scan saw against the bytes the volume counts as used, and shows
the difference as a number, with every folder that refused a listing named underneath. That is the
unseen-gap line, and it is required output rather than a diagnostic: a scan that is not elevated
cannot see everything on a Windows disk - about ninety-seven gigabytes of the machine this was
designed for is invisible without an administrator - and a report that printed only what it saw
would read as a complete answer when it is not one.

Links and junctions are counted and never followed. Cloud placeholder files are counted apart from
the bytes on the disk, because they occupy none of it.

## A scan that saw nothing is broken

An empty or zero result reports `verdict: broken`, never "nothing here". A working scan of an empty
folder and a scan that failed produce the same zeroes, and only one of the two is safe to act on. A
broken scan is not saved either: the saved scan is what a screen will show later without walking
anything, and a measurement this tool has just called broken must not become that answer.

## What a recommendation always says

A rule may recommend removing something only when it holds one of exactly three proofs, and it can
hold no other:

1. **A record says nothing needs it.** Windows records, for every installed product and every applied
   patch, the cached package it needs to repair or uninstall that thing. A package no record names
   cannot be asked for.
2. **The owner has its own cleanup command.** The tool that wrote the data ships a command that
   clears it, and that command is what runs. We never delete inside somebody else's store, because
   only that tool knows what its own store still needs.
3. **We made it, by an exact name, and it is old and closed.** A folder matching a name DevThrottle's
   own code creates, older than the rule's age gate, with nothing in it open.

**Anything no rule matched is reported as unclassified and is never offered for removal**, whatever
it is and however large it grows. This is an allow-list: the tool enumerates what to remove, never
what to skip. A path pattern from somebody else's product is not a proof.

Every recommendation carries the rule, what it removes, the proof that it is safe, what is lost, how
to get it back, whether an administrator is needed, the age gate, and the rule's own controls.

## A rule that could not do its work says so

Every rule reports what it counted. A rule that compares two lists and finds one of them empty
reports `verdict: broken` and offers nothing - never "nothing to remove". A comparison against a
record set that failed to load finds nothing every single time and reads exactly like a clean disk,
and only one of those two is safe to act on. A rule that counted nothing at all is broken for the
same reason.

One rule failing does not take the rest down with it: it says why and offers nothing, and the rules
beside it still stand.

## Rules look in one place each

A rule examines one known folder. `recommend` runs the rules whose folder is inside the folder you
asked about, and **names the ones it left out, with where they look**. Asking about a folder no rule
looks inside gives `verdict: broken` and exit code 1, with every rule named underneath - not exit 0
and "nothing to remove", because that is the answer a genuinely clean disk gives.

`recommend` reads a saved scan; it never walks the disk itself. With no saved scan it says so and
prints the `scan` command.

## The saved scan

A scan writes one small file, and `report` reads it without walking the disk again. It is the
contract between the background scan (the Launcher, in phase 5) and the screen that shows it (the
Director, in phase 6), so it carries its own name and version, and a reader that meets a version it
does not know says so and stops.

The default folder is one per machine: `<machine root>/reclaim/index`.

## Output shape

Output follows the AXI standard (`docs/axi-standard.md`): compact lines, definitive empty states
(`count: 0`, and a list header with a nought in it rather than blank output), structured errors with
a word an agent can branch on, an unknown flag that fails rather than being ignored, and `help[]`
lines offering what to run next. Every line is plain ASCII.

`--json` keeps its shape. Other code parses it, so it carries every field, including the report's own
finished sentences under `lines`.

## Building and testing

    dotnet build tools/cc-cleanup-storage/cc-cleanup-storage.slnx
    dotnet test src/CcDirector.Reclaim.Tests

The tests cover the engine (`src/CcDirector.Reclaim`) and this tool together, in one suite, and that
suite is in the default local gate (`.\scripts\test-local.ps1`).
