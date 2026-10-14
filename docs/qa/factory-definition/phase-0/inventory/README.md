# Factory Definition, phase 0: inventory (d, e, f) and the email check (g)

Measured and read on SOREN_NORTH on 14 October 2026, against origin/main `e44ee8496`. Everything here was
read only: no factory, schedule, trigger, brief or records folder was changed. Every script can be run again
from this folder and prints the same kind of output; the `.txt` files are what each printed, and the `.json`
files are the same data in full.

This repository is public, so the outputs hide what could name a person or a client: file paths inside a
records folder are cut to their first folder and extension, schedule and trigger prompts are not copied, a
start folder that is neither a registered repository nor its factory's registered folder has its last part
hidden and so does the id of the seat that starts there, each machine's list of repositories shows only the ones
a seat uses, and remote addresses lose their user-name part.

| Part | Script | Output |
|---|---|---|
| (d) records measured, and the caps | `measure_records.py` | `records-measured.txt`, `records-measured.json` |
| (d) factory folders sized as definitions | `measure_definition_folders.py` | `definition-folders.txt`, `definition-folders.json` |
| (e) every factory's parts | `list_factory_parts.py` | `factory-parts.txt`, `factory-parts.json` |
| (f) machines and their repositories | `list_seat_machines.py` | `seat-machines.txt`, `seat-machines.json` |
| (g) the email check | `email-check/check_new_mail.py`, `mail-rules.json` | `email-check/tests-run.txt` |
| (g) the variable names, by hand | `email-check/run_naming_proof.py` | `email-check/naming-proof.txt` |
| (g) the check run by hand | `email-check/run_email_check.py` | `email-check/email-check-run.txt` |

## (d) The records, measured

Records live in more places than the mission counted. The mission measured `%LOCALAPPDATA%\cc-factory\`
(236 files, 23 MB, all but a little of it the DevThrottle factory). Six more factories keep their own folder
beside it, and one of them is seventeen times bigger:

| Factory | Records folder under `%LOCALAPPDATA%` | Files | Size | Of which screenshots |
|---|---|---|---|---|
| clickfunnels | `clickfunnels-factory` | 869 | 388 MB | 216 MB (plus 171 MB of scratch audio) |
| devthrottle | `cc-factory/devthrottle` | 225 | 21.8 MB | 19.4 MB |
| website-business | `website-factory` | 231 | 5.4 MB | 1.0 MB |
| mindzie-web | `mindzie-web-factory` | 52 | 3.7 MB | none |
| business-research | `business-research-factory` | 82 | 1.5 MB | none |
| warmforward | `warmforward-factory` | 10 | 52 KB | none |
| cc-factory | `cc-factory/cfo`, `cc-factory-factory` | 10 | 41 KB | none |

`cc-factory/vercel-release` (two files) is the website release tool's lock log, not a seat's record.
`cc-director/factories/money-saver` exists and is empty. Records on devlinux and on the Mac mini, where a few
seats are scheduled (part f), were not reachable from here and are not counted.

Each file is sorted into one kind, by these rules in this order: anything under a `scratch`, `transcribe`,
`tmp`, `temp`, `cache` or `__pycache__` folder is **scratch**; an image, audio, video, PDF or zip is an
**attachment**; a `.db` or `.sqlite` file is a **database**; anything under `journal` or `memos`, or a `.log`
or `.jsonl` file, is a **journal**; everything else is a **working document**.

Two kinds the mission does not plan for turned up:

- **Databases.** website-business keeps `factory.db` (1.25 MB) and business-research one of 592 KB;
  clickfunnels has an 88 KB SQLite file. A database is not a document with versions; phase 2 has to say
  whether these become working documents, attachments, or stay on the machine.
- **Scratch.** clickfunnels' `transcribe` folder holds 171 MB of WAV chunks, 9 MB each. It is working space,
  not a record, and the one-time upload should leave it behind rather than refuse on it.

### The exclusion rule: what in a factory folder is not definition

`measure_definition_folders.py` sizes each factory's own folder twice, whole and without these folders, at any
depth: `.git`, `.temp`, `__pycache__`, `.pytest_cache`, `tests`, `qa`, `state`, `research`, `sample`,
`workshop`. Each was found holding test fixtures, saved state (a record, section 5.6) or raw material, never what
decides how a seat behaves. **Phase 3 and phase 7 should leave the same folders out** when a factory is written as
files; the 4 MB per-version cap holds only under that rule. website-business's registered folder is a devlinux
path, so its folder here is the one its triggers start in on SOREN_NORTH; money-saver's two seats keep their
briefs in two separate folders under `tools/`, so each is measured on its own.

### The caps, recommended

| Cap | Mission's start | Recommend | Why |
|---|---|---|---|
| Per definition file | 256 KB | **256 KB, confirmed** | The largest definition file today is 110.7 KB (`cc_website_factory/record.py`, the website factory's own Python package; `definition-folders.txt`). The largest brief or template is 36 KB. Twice the largest leaves room without letting a data dump in. |
| Per definition version | 4 MB | **4 MB, confirmed, under the exclusion rule below** | Measured in `definition-folders.txt`: the largest definition is website-business at 1,068 KB, then mindzie-ai-reports at 729 KB; the other twelve are 1 to 267 KB. Whole folders are NOT within 4 MB: website-business's is 1.1 GB, almost all of it `workshop/` (raw client photo batches). |
| Per working document | (none; largest named as 642 KB) | **2 MB** | The 642 KB `defects/reads/rows.json` is not the largest: mindzie-web's `weekly-usage/.../actions.json` is 1.66 MB, and its `cost-watch` snapshot 536 KB. 2 MB holds every document today. Note that every write keeps a version, so a 1.7 MB document written weekly is about 90 MB a year of history; the cap is per version, never on history, as the mission says. |
| Per journal entry | (none) | **256 KB** | The largest journal file today is 33 KB; none is over 64 KB. A cap stops a seat pasting a data dump into a journal. |
| Per attachment | 2 MB | **4 MB** | 2 MB would refuse 16 of today's 484 attachments, all clickfunnels full-page phone screenshots up to 2.69 MB. None is over 3 MB. 4 MB keeps every screenshot and still refuses audio and video (the WAV chunks are 9 MB). |
| Attachment total per factory | (to set) | **1 GB, with a refusal naming the total and the largest files** | clickfunnels wrote 216 MB of screenshots in 7 days (about 30 MB a day); devthrottle 19 MB in 6 days. 1 GB is about a month of clickfunnels at that rate and years of every other factory. Storing each distinct file once (by content hash) would also help: 39% of clickfunnels' screenshot bytes are byte-identical copies (221 distinct of 318 files, 132 MB of 216 MB), and 21% of devthrottle's. |

## (e) Every factory's parts

`factory-parts.txt` lists, per factory: the registry entry (boss seat, computer, purpose), every seat with its
role, computer and brief file, every schedule and trigger with its seat, machine and folder, the brief files
and skills a prompt names, the records folders, and from the disk the factory's folder, the git clone it sits
in, its brief files, templates, tools, rules files, `.claude/settings.json` and skills.

14 factories, 90 seats, 70 schedules (55 enabled), 3 triggers (all website-business). What phase 7 needs to
know before it recreates any of them:

- **Every factory schedule is set to 31 February.** All 70 carry the cron `0 0 31 2 *`, a date that never
  comes, so none fires (the owner turned the factories off, D7). The real times are no longer on the Gateway.
  Phase 7 has to take each seat's time from its brief (the Outreach brief says "every weekday at 08:15") or
  from the owner.
- **The brief store refuses a session outside the factory.** `factory brief history` answered "this brief
  belongs to factory '<id>', so only a person or a session of '<id>' may change or run it" for all 14
  factories, so the brief versions on the Gateway are not in this inventory. Export of an undefined factory
  (A8) reads the published briefs, so it must run as a person or a session of that factory.
- **Brief files missing on disk.** devthrottle's `agents/ceo.yaml` (its `ceo` seat has an active schedule
  naming it) and 6 of mindzie-web's 12 brief files in the mindzieWeb checkout (`worktree-cleaner`,
  `error-triage`, `i18n-coverage`, `code-review`, `qa-manager`, `repo-hygiene`).
- **Seats sharing one brief.** business-research and clickfunnels each have both a `ceo` and a `boss` seat on
  `agents/ceo.yaml`; three clickfunnels seats use `FACTORY.md` as their brief.
- **Schedules the registry does not know.** cc-factory's `cj_14734a` names seat `cfo`, which is not a seat.
  Eleven schedules carry no factory at all, three of them named for factory seats (Cost Sweep, Customer Care,
  Weekly Usage Report); phase 7 should decide whether each belongs to a definition.
- **Seats start outside their factory's folder.** 36 of 70 schedules start in a folder other than the
  factory's registered folder, mostly `devthrottle_internal` or `devthrottle` for the DevThrottle factory, and
  the Outreach seat starts in its own subfolder `factory/outreach`, whose `.claude/settings.json` carries its
  send fence. Each of these becomes the seat's declared repository, or none.
- **The DevThrottle factory's clone is a clone of devthrottle_internal.** `D:\ReposFred\devthrottle-factory`
  has remote `thefrederiksen/devthrottle_internal`. That is the separate clone the mission retires.
- **Rules today are `.claude/settings.json` deny lists in the factory folder**: devthrottle 216 deny rules,
  cc-factory 106, business-research 64 (plus a PreToolUse hook and a `permissions/` Python package).
- **Skills.** business-research has its own skill folder (`research-ceo`); prompts name fleet skills such as
  `finance-business`, `youtube-channel-manager` and `website-front-desk`.

## (f) Machines a factory seat runs on

`seat-machines.txt` lists each machine, whether its Director answers now and at what version, the
repositories registered on it that a seat uses, and every folder a seat starts in, resolved to a registered
repository the way 5.5 step 6 will.

| Machine | Director now | Repositories registered | Seats placed there |
|---|---|---|---|
| SOREN_NORTH | online, 2.20.1 | 82 (7 used by seats: cc-clickfunnels, cc-consult, devthrottle, devthrottle-factory, devthrottle_internal, mindzieWeb, private) | every factory |
| devlinux | not answering | 7 | warmforward: 4 schedules; website-business: its boss seat |
| devthrottle-mac-mini | online, **2.1.0** | 8 | machine-care: `machine-setup` |

129 seat folders; 111 resolve to a registered repository on their machine and **18 do not**. Under the new
rule each of these 18 would be refused at publish and at start until fixed:

- `mindzie-ai-reports` (5 seats): its folder `D:\wt\ai-reports-factory` is not a registered repository.
- `website-business` (11 seats, one of them its boss on devlinux): the registry says computer SOREN_NORTH but its folder is a devlinux path
  (`/home/soren/wt/...`), a worktree that is not registered on devlinux either. Its schedules and triggers run
  on SOREN_NORTH in `cc-consult`.
- `machine-care` `machine-setup`: the factory folder is a Windows path, the seat runs on the Mac.
- `house`, one seat (its id withheld, see review finding 3): its schedule starts in a folder that is not a
  registered repository. The seat has since been removed from the registry (90 seats, was 91 at the first read);
  its schedule `cj_9d2b12` remains.

Also for phase 5: warmforward's registry places every seat on SOREN_NORTH while four of its schedules target
devlinux, and the Mac mini's Director is 2.1.0, so it would be refused the new `FactoryWorktree`,
`FactoryHome` and `Model` flags until it updates.

## (g) The email check

**The live mailbox run is deferred to phase 5b, pending the owner's app registration.** The Outreach seat
reads `email@devthrottle.com` only through cc-outlook's own sign-in (device code flow, `cc-outlook -a
devthrottle`). No cc-secrets entry can read that mailbox, so a check under `cc-secrets run` cannot reach it
today. The Delivery Lead has the three `cc-secrets ask` lines and asks the owner. The owner first makes an
app registration in the devthrottle.com Microsoft 365 tenant with Microsoft Graph `Mail.Read` (application),
limited to that one mailbox by an application access policy; then the entries are
`outreach-mail-tenant-id` and `outreach-mail-client-id` (settings) and `outreach-mail-client-secret`
(secret). `email-check-run.txt` is the run as it stands: both forms refused with "No secret named
'outreach-mail-client-secret' is on this machine." (exit 2), which the Director must record as a failed check.

What is done:

- **The check**, `check_new_mail.py`, written to the 5.5a contract: exit 0 and one JSON object with an
  integer `count` and a `context` of at most 8 KB (one line per message: when, from whom, subject, folder);
  state in the file named by `CC_CHECK_STATE_FILE` (the newest receive time counted and the ids counted at
  that time); anything wrong is a non-zero exit with the reason on standard error and nothing on standard
  output. It counts mail from anyone the ledger says we wrote to (a reply or an out-of-office) and delivery
  failures, in the Inbox, Junk and Archive (the Outreach brief says bounces were found in Archive). It READS
  ONLY: it asks Graph for id, time, sender and subject - never a body - with GET, and its one POST is the
  sign-in. Who counts is in `mail-rules.json`; in phase 5b the ledger path becomes the working document in the
  seat's kit.
- **Its tests**, 13, passing (`tests-run.txt`), against a fake Graph: an empty inbox; a reply counted with
  its sender and subject; mail from someone not in the ledger not counted; a bounce in Archive counted; a
  second run remembering the first through the state file; a new message in the same second as the last
  counted one still counted; the first run looking back only its configured hours; 200 messages keeping
  `context` under 8 KB; every mail call a GET selecting no body, and the sign-in the only POST; a missing
  secret name failing with the name; no state file named failing with nothing on standard output; and the
  redaction used for this record.

### The variable-name rule, proven both ways (`naming-proof.txt`)

The mission's one rule - the entry name upper-cased, dashes and dots to underscores, `--env-name` for exactly
one secret - **does not hold as written**:

- **With one secret it holds**, whatever the entry's own variable: `--env-name LAPTOP_2_RUSTDESK` on an entry
  whose own variable is `LAPTOP2_RUSTDESK_PASSWORD` delivers `LAPTOP_2_RUSTDESK` (case 4).
- **But `--env-name` alone is not enough for an entry with no variable of its own**: it is then delivered on
  standard input, not in the environment (case 2). The Director must pass `--via env --env-name <NAME>`
  (case 3).
- **With two or more secrets the rule breaks**: each entry arrives in ITS OWN variable, and only an entry with
  none gets the upper-cased name (case 6: `laptop-2-rustdesk` arrived as `LAPTOP2_RUSTDESK_PASSWORD`, not
  `LAPTOP_2_RUSTDESK`). `cc-secrets run` has no way to rename a `--with` entry.
- **How often it bites**: 11 of the 122 entries on this machine have a variable that differs from the rule -
  `desktop-login` (`DESKTOP_LOGIN_PASSWORD`), `laptop-2-rustdesk` (`LAPTOP2_RUSTDESK_PASSWORD`),
  `stripe-live-secret-key` (`STRIPE_SECRET_KEY`), `stripe-live-webhook-secret` (`STRIPE_WEBHOOK_SECRET`),
  `wifi-home` (`WIFI_PASSWORD`), and six with no variable of their own (`cloudflare-account-id`,
  `cloudflare-registrar-token`, `devthrottle-supabase-service-key`, `devthrottle-supabase-url`,
  `stripe-secret-key`, `warmforward-forecast-secret`). Read from `cc-secrets list --json`, field `envName`.

The Delivery Lead answers this in the mission record. The three entries the email check asks for are made
with `--env-name` set to the rule's names, so the check itself is not affected.

### Refusals

- **A declared name this machine does not hold** is refused before the check runs, alone or beside a held
  one: "refused: No secret named 'outreach-mail-client-secret' is on this machine." (exit 2; cases 7 and 8).
- **An entry allowed for browser login only**: none exists on this machine - every one of the 122 entries
  allows `run` - so this refusal could not be shown. Adding an entry is owner-only.

## Running it again

```
python measure_records.py --json records-measured.json > records-measured.txt
python measure_definition_folders.py --json definition-folders.json > definition-folders.txt
python list_factory_parts.py --json factory-parts.json > factory-parts.txt
python list_seat_machines.py --json seat-machines.json > seat-machines.txt
python email-check/run_naming_proof.py > email-check/naming-proof.txt
python email-check/run_email_check.py > email-check/email-check-run.txt
python -m pytest email-check -q
```

Each needs `cc-devthrottle` (and the email ones `cc-secrets`) on the PATH, and exits with a clear error if
one is missing. `list_factory_parts.py` takes about 20 seconds (it reads the brief store once per factory) and
`measure_definition_folders.py` about a minute (it walks the 1.1 GB website factory folder).

## Review answers

The review is `reviews/phase0-inventory-review.md` (a separate review session, commit `461510c22`).

| Finding | Answer |
|---|---|
| 1. The two definition caps rest on figures no committed script produced | **Accepted.** `measure_definition_folders.py` and its output are committed, the exclusion rule is stated above with the folders it leaves out and why, and the two cap rows now cite the measured figures (1,068 KB largest definition, 110.7 KB largest file). The earlier "113 KB" was the same file in thousands of bytes rather than kilobytes. |
| 2. Part (g) as the mission wrote it is not delivered | The Delivery Lead's to answer, and answered: the live mailbox run carries into phase 5b, and the owner has the app registration request. The review's notes for 5b (an empty state file on a seat's first run, a state that cannot advance past 500 new messages in one folder, `%LOCALAPPDATA%` expanding on Windows only, and `--setting --env-name` unverified) are carried with it. |
| 3. A seat id carries a first name the masking rule hides | **Accepted.** A seat whose start folder is hidden now has its id, role and brief file shown as `<seat id withheld>` in every output and in this README. The hiding rule was also narrowed while doing it: a folder is hidden only when it is neither a registered repository nor the factory's own registered folder, so mindzie-ai-reports' folder and seats, which are not personal, stay readable. |
| 4. The mindzie factories print a private repository's whole skills catalogue | **Accepted.** Skills are now listed only from the factory's own files (the folder its briefs are under). The mindzie factories list none; business-research still lists its `research-ceo`. |
