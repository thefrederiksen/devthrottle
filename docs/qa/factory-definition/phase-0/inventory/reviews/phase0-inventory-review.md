# Review - Factory Definition, phase 0 inventory and email check (pull request #3994)

Reviewer: session "Factory Definition - Reviewer - phase 0 inventory" (a separate review session, different agent
from the author). Date: 2026-10-14. Reviewed commit: 461510c22 on branch `factory-definition/p0-inventory`, read in
the worktree `D:\ReposFred\_wt\fd-p0-inventory` (not edited). Mission document read from origin/main.

## Verdict

**Accept, with one finding that needs an answer before phase 0 closes (finding 1) and three the Developer decides.**
The measurements and lists come from the sources they claim: every script re-run from a scratch copy reproduced its
committed output byte for byte apart from the timestamp line. The email check keeps the 5.5a contract and reads only,
and its tests prove both. No secret value, no message body and no mail address is in any committed file.

## Scope

What I read: all 19 files of the pull request; the pull request body; mission sections 5.5, 5.5a, 5.6 and the phase 0
row of section 6 on origin/main; the method skill and the mission workflow.

What I ran, from a copy in my scratchpad, never in the worktree:

| Run | Result |
|---|---|
| `python -m pytest email-check -q` | 13 passed |
| `measure_records.py --json` | output identical to `records-measured.txt` apart from the timestamp line |
| `list_seat_machines.py --json` | identical apart from the timestamp line |
| `list_factory_parts.py --json` | identical apart from the timestamp line (14 factories, 90 seats, 70 schedules, 3 triggers, 11 schedules with no factory, 18 of 129 seat folders unresolved) |
| `run_naming_proof.py` | identical to `naming-proof.txt`, all eight cases |
| `cc-secrets list --json` | 122 entries; exactly 11 whose `envName` differs from the rule, the 11 the README names; 0 entries without the `run` use |
| `cc-secrets ask --help` | has `--env-name`, so the three entries can be made with the rule's names as the README says |
| recount from `factory-parts.json` | 36 of 70 schedules start outside the factory's registered folder |
| the real outreach ledger (keys only) | a `people` list, each with `address` and `sent`; 11 of 17 would be watched - the shape the check reads |
| sweep of every committed `.json` and `.txt` | no `@` address, no `seed` or `prompt` key, no secret value (the probe prints variable names only) |
| mtimes of the screenshots | clickfunnels 2026-10-05 to 2026-10-11, devthrottle the same span - the rate claim holds |

What I could not reach: records on devlinux and the Mac mini (not counted, as the README says); the live Outreach
mailbox (no cc-secrets entry can read it, finding 2); a login-only cc-secrets entry (none exists, adding one is
owner-only), so that refusal is unproven, as the README says.

## Findings

### 1. The two definition caps rest on figures no committed script produced, and the website-business figure is wrong as a folder measurement

README, "The caps, recommended": per definition file "the largest definition-shaped file today is 113 KB (`record.py`
in the website factory's own Python package)"; per definition version "the biggest factory today is website-business,
about 1.2 MB with its whole Python package; every other factory is under 250 KB".

Neither number is in `records-measured.*` or `factory-parts.*`. `list_factory_parts.py` never read the website-business
folder at all (the registry names a devlinux path, so it printed NOT READ), and it only sizes files at the top of a
factory folder plus `tools/` and `permissions/`. The figures came from a hand measurement that is not committed, so a
reader cannot check them from the record.

I measured the folders on this machine, `.git` excluded:

| Factory folder | Whole folder | After leaving out `tests`, `state`, `.temp`, `__pycache__`, `research`, `sample`, `workshop` and the like |
|---|---|---|
| website-business (`cc-consult/ideas/website-factory/factory`) | **1,112 MB** - a `workshop/` folder of raw client photo batches, 5 to 6 MB each | 1.6 MB Python package + 0.2 MB top level |
| mindzie-ai-reports | 2.5 MB (`sample/` 1.8 MB, reports) | 84 KB |
| business-research | 0.7 MB | 233 KB |
| clickfunnels | 0.6 MB | 481 KB (tools, with their `__pycache__`) |
| tallyhand | 0.5 MB (`research/` 0.3 MB) | 97 KB |

`record.py` is 113 KB on disk, so that number is right. "About 1.2 MB" and "every other factory is under 250 KB" are
true only under an exclusion rule the README never states.

**The harm.** Phase 7 is told to write the DevThrottle factory "as a folder from today's files" and import it, and the
owner then "recreates the others by import". The website-business folder as it stands is 1.1 GB; a 4 MB per-version
cap refuses it, and nothing in this inventory tells phase 3 or phase 7 which folders inside a factory folder are not
definition. The 4 MB cap is probably right, but the record that justifies it must say what was counted.

**What must change.** Either commit the measurement that produced the two figures (a script, or a table of what was
counted per factory) and state the exclusion rule phase 7 should use, or replace the two sentences with the measured
folder sizes and say plainly that `workshop`, `tests`, `state`, `sample`, `research` and `__pycache__` are not
definition. One paragraph; no code change.

### 2. Part (g) as the mission wrote it is not delivered; the Delivery Lead decides whether phase 0 closes without it

Section 6, phase 0 (g): "The email check written for real against the Outreach seat's mailbox, run by hand under
`cc-secrets run`, printing the contract, once with one secret and once with two - so phase 5b starts from a check known
to work and the variable-name rule is proven."

Delivered: the check, 13 tests against a fake Graph, the variable-name proof on other entries, and two hand runs that
were refused at `cc-secrets run` because the entries do not exist. The README says so plainly at the top of (g), and
why: the mailbox is reachable only through cc-outlook's device-code sign-in, and an app registration by the owner is
needed first. I agree that is the right reason and that the tests prove what a fake can prove (contract, read only, the
state file, the 8 KB cap).

**The harm.** The purpose the mission gave (g) - "so phase 5b starts from a check known to work" - is not met. The
Graph query (`$filter` on `receivedDateTime` with `$orderby` on the same property, the well-known folder names
`inbox`, `junkemail`, `archive`, app-only `client_credentials`) has never run against Graph. If any of it is wrong,
phase 5b finds out at its first live run. The owner's app registration is not yet requested in any committed place;
the README says the Delivery Lead holds the `cc-secrets ask` lines.

**Not a defect in the work.** A scope gap against the mission row, declared by the author. The Delivery Lead decides
whether phase 0 closes with (g) carried into 5b, and should get the app registration asked for now so 5b does not
wait on it.

### 3. The seat id `erik-website` carries a first name the author's own masking rule hides

`list_seat_machines.py` masks a schedule folder that is not a registered repository because "such folders are
personal projects and can carry a third party's name, and this record is public". The folder behind schedule
`cj_9d2b12` is masked for that reason - and the seat id on the same line, `erik-website`, carries the same first name.
It is printed in `factory-parts.txt`, `factory-parts.json`, `seat-machines.txt`, `seat-machines.json` and the README.
The name appears nowhere else on origin/main (word search, `Frederiksen` excluded).

**The harm** is small: a first name only, of someone who may be family. But the masking rule is inconsistent with
itself, and this repository is public. The Developer decides; masking the seat id the same way is one line.

### 4. The three mindzie factories print a private client repository's whole skills catalogue, three times

`skillsInFolder` lists `.claude/skills` of the factory's registered folder. For `m-bpm-studio`, `m-cube-pi` and
`mindzie-web` that folder is the whole mindzieWeb clone (a private Azure DevOps repository), so the output carries its
80 skill names three times. Not a secret and not personal data, so not within the brief's three questions, and it
serves nothing in phase 7 (they are not factory skills). An observation for the Developer: list only skills under the
factory's own files (`factoryFiles`), or leave the mindzieWeb list out.

## Notes for phase 5b, not findings against this pull request

- The check refuses to run when the state file does not exist. The mission says the Director writes the stored state
  into the file before the run; on a seat's FIRST run there is no stored state, so 5b must make the Director create an
  empty file, or the first run is a failed check.
- More than 500 new messages in one folder is a permanent failure: the check raises before `write_state`, so the
  state never advances and every later run fails the same way. The design gives nobody a command to reset
  `factory_check_state`. 5b should either advance the state past what was read or give the owner a reset.
- `%LOCALAPPDATA%` in `mail-rules.json` expands on Windows only; fine for SOREN_NORTH, and in 5b the ledger becomes
  a kit document anyway.
- Whether `cc-secrets ask --setting --env-name` sets the variable name for a setting could not be verified without
  creating an entry (owner-only). The flag is on the command.

## What is confirmed good

- Read only throughout: every cc-devthrottle call is a list or a read, every git call is `rev-parse` or `remote
  get-url`, the records walk opens files only to hash attachments. The email check's one POST is the sign-in; every
  mail call is a GET with `$select=id,receivedDateTime,from,subject`, and the read-only test asserts exactly that.
- The 5.5a contract: exit 0, one JSON object, integer `count`, `context` capped at 8,192 bytes with a "more not
  shown" tail, state read from and written to `CC_CHECK_STATE_FILE` with a 64 KB cap, every failure a non-zero exit
  with nothing on standard output. The two subprocess tests prove the standard-output silence.
- The variable-name finding is real and matters for 5b: `--env-name` without `--via env` delivers on standard input
  for an entry with no variable of its own (case 2), and `--with` entries keep their own variable (case 6). The
  mission's "one rule" needs rewriting; the Delivery Lead answers it in the mission record.
- The cap recommendations for working documents (2 MB), journal entries (256 KB), attachments (4 MB) and the
  per-factory attachment total (1 GB) are each supported by the committed distribution lines in
  `records-measured.txt`.
- The three phase 7 facts that matter most are solid and reproduced: every factory schedule is parked on
  31 February, the brief store refuses every factory to a session outside it, and 18 of 129 seat folders would be
  refused under the 5.5 step 6 rule.
