---
name: release-manager
description: Guided run-book for cutting a DevThrottle release - assemble the changes, write the release notes, coordinate the internal docs site, cut the tag, and announce it. Triggers on "/release-manager", "cut a release", "prepare a release", "ship a release", "do the release".
---

# Release Manager

The end-to-end run-book for releasing DevThrottle. Follow the steps in order. Each
step has a gate you must clear before the next. The goal is a release that is
accurate (never claims a design document is a shipped feature), documented in
plain language that humans, search engines, and other agents can read, and
announced only to people who have not opted out.

This is enterprise software with a public repository. The release notes are a
public, permanent record. Get them right.

## Quick Reference

| Step | What happens | Gate before moving on |
|------|--------------|-----------------------|
| 1 | Pre-flight: sync to origin/main, confirm it is green | Your view is not stale; the mainline builds and passes |
| 2 | Decide the version number | Agreed with the human |
| 3 | Assemble the change list from git | Full list, categorized |
| 4 | Accuracy gate: shipped vs designed | Every headline is real, running code |
| 5 | Write the canonical release notes markdown | File written in the required shape |
| 6 | Coordinate the internal documentation site | Internal session has the file, building in draft |
| 7 | Human signs off on the notes | The human says the wording is correct |
| 8 | Freeze the candidate: `new-release.ps1` merges bump and notes in ONE pull request | Merged; its merge commit is the candidate |
| 9 | Gate the candidate once; the human tags it with `new-release.ps1 -Tag` | `assert-gated.ps1` accepts the candidate; tag pushed |
| 10 | Announce to the mailing list | Only if the opt-out feature is live and tested |
| 11 | Post-release verification | Download and unsubscribe both work |

## CRITICAL rules

- **Never claim a design document is a shipped feature.** Step 4 exists because it
  is easy to read an architecture document and write "we added X" when the code
  is not merged, or is merged but turned off by default. Verify against the code.
- **A release that adds or changes a Gateway hub method deploys the hosted Gateway
  BEFORE the tag is pushed.** The desktop Director auto-updates; the hosted Gateway
  does not - it ships only when someone runs the deploy workflow. So a release that
  teaches the Director to call something the live Gateway has never heard of puts
  every machine in the fleet on a version the Gateway cannot serve, the moment they
  update. That is not hypothetical: v1.9.9 made the per-session Gateway key the only
  door an agent has to the fleet, the hosted Gateway had not been deployed since
  before `RegisterSessionKey` existed, and every session on every machine had its key
  refused with 401 until the Gateway was deployed hours later (#2457, #2459).
  Check with `git diff <last-tag>..origin/main -- src/CcDirector.Gateway/Streaming/`;
  if anything there changed, deploy first (see the `deploy-hosted-gateway` skill),
  confirm `/healthz` reports the new commit, and only then cut the tag.
- **The human publishes the release, never the agent.** Cutting the tag is an
  outward-facing, hard-to-reverse action. Prepare everything; the human clicks
  publish.
- **Never send a bulk email without a working, tested unsubscribe.** Step 10 is
  gated on the mailing-list opt-out feature being live. If it is not, hold the
  email and ship the release without it.
- **Do not commit unless the human asks.** Even after one commit, a later commit
  needs its own explicit request.
- **The candidate is frozen.** The version bump and the notes merge together in one
  pull request, and its merge commit is the release candidate. The notes are never
  edited after that merge, the gate runs once on that commit, and the tag goes on that
  commit even when main has moved on. Work merged after the candidate waits for the
  next release. See Step 8.
- **One seat runs the release gate.** The release seat - the session running this
  skill - is the only session that runs `-Parked` for a candidate. Every other session
  runs the default gate. Two release gates on one machine fight over the Gateway test
  lock, and the loser executes nothing.
- **Work in your own worktree cut from origin/main**, never the shared checkout. Stage
  only the files you created or changed, by name. Never `git add -A`.
- **Plain English, no abbreviations. ASCII only, no emoji or special symbols.**
  This applies to the notes, the emails, and every message.

## Workflow

### Step 1: Pre-flight

The single most common mistake is releasing from a stale local view. Do this first:

```bash
git fetch origin --tags -q
git rev-list --count <last-tag>..origin/main    # how many commits shipped
```

- Confirm the release is cut from `origin/main`, and that your local branch is not
  behind it. The code you are documenting lives on `origin/main`; your working
  branch may not have it.
- Know that a release is gated by a LOCAL run, not by continuous integration, and that the run
  which counts happens later - on the frozen candidate, the merge commit of the bump-and-notes
  pull request (Step 8). A run against the pull-request head does not count: the squash merge
  produces a different commit. It is ONE command,
  `.\scripts\test-local.ps1 -Parked -Configuration Release`, and it is the one release-blocking
  wait, because the release workflow runs ZERO tests and a pushed tag cannot be un-pushed. A
  release is the single place "fix it forward" is unavailable.
- An early warning is the DEFAULT run (`.\scripts\test-local.ps1`), never `-Parked`. A parked run
  now is not the gate - the candidate does not exist yet - and it is one more contender for the
  Gateway test lock.

### Step 2: Decide the version number

- Find the last product tag: `git tag --sort=-creatordate` (ignore non-product
  tags such as `agenteyes-latest`).
- Apply semantic versioning (see `docs/Release-Process.md`): new backward-
  compatible features raise the minor number (for example v1.0.7 to v1.1.0); bug
  fixes only raise the patch number.
- Confirm the number with the human.

### Step 3: Assemble the change list

```bash
git log <last-tag>..origin/main --first-parent --format="%s"
git log <last-tag>..origin/main --merges --format="%s"
```

Read every line. Group the changes into a small number of themes (for example
"session roles", "transcription", "mobile dictation", "security"). Separate the
few headline items from the many smaller ones.

### Step 4: Accuracy gate (mandatory)

For each headline item, verify against the code, not the documentation:

- Does real, running code implement it, or does only a design or plan document
  exist? Search for the actual types, endpoints, and interface elements.
- Is the feature turned on by default, or is it behind a flag that defaults to
  off? A feature that is merged but off by default is a preview, not a default.
  Say so plainly ("available now as an opt-in setting, off by default").
- Does the described mechanism match the real one? Do not invent a mechanism a
  reader could check and find false.

If a headline is design-only or flag-gated, change the wording to the truth, or
drop it, before writing the notes. When in doubt, spawn a read-only Explore agent
to return an evidence-backed verdict per feature with file paths.

### Step 5: Write the canonical release notes

Write to `docs/public/release-notes/v<version>.md` in the public repository. This
file is the single source of truth: the GitHub release body and the internal
documentation website both draw from it. Newest release at the top of the folder.

Use exactly this shape, because the internal site folds it in near-mechanically:

```
## v<version> - <Month D, YYYY>

One plain-language sentence summarizing the release.

### Highlights
- <Feature name>: one plain sentence on what it is and why it matters, phrased as
  what the user can now do.
- <Feature name>: ...

### Also in this release
- <shorter change phrased for the user>
- <shorter change>
```

Rules that keep it clean and honest:

- One `##` heading per version, with a human-readable date.
- Plain language, no engineering jargon. Say "your other machines no longer need
  to open a port", not "removed the inbound port requirement".
- Phrase user-visible changes as what the user can now do.
- Semantic bullet lists, not tables.
- ASCII only. No emoji, no special symbols.
- Every factual claim must be true against the shipped code (see Step 4).

### Step 6: Coordinate the internal documentation site

The internal site (`devthrottle_internal` repository) turns the public markdown
into a searchable, discoverable website. The direction of truth is one-way:
public markdown to internal website, never the reverse. The internal site invents
no release facts; it only expands and phrases what the public file states.

- The durable changelog already exists at the route `/docs/reference/changelog`
  (body file `website/src/content/docs/reference/changelog.jsx`, manifest
  `website/src/content/docsList.js`). Every release prepends a new
  `## v<version>` block, newest first. Do not create a new changelog page.
- The internal pipeline is a React body plus a manifest record, not markdown with
  front matter, so the transform from your markdown is the internal session's
  job. You only owe it clean, structured markdown in the shape above.
- To coordinate, open or find a session on the internal repository and hand it the
  path to your finished file:

```bash
cc-devthrottle session spawn D:/ReposFred/devthrottle_internal --controlled-by self
cc-devthrottle message send <session-id> "<one-line message with the file path>"
```

  Fleet messages must be a single line; newlines are truncated. For anything long,
  write a brief to a file and send the path.
- The internal session builds in draft and holds until you send an explicit
  "FINAL" message. Do not send FINAL until the human has signed off (Step 7) and,
  if the changelog would claim the release shipped, until the release is actually
  cut (Step 9). Publishing the changelog before the tag exists claims a release
  that has not happened.

### Step 7: Human sign-off

Show the human the full notes text. Change whatever they ask. Nothing is committed,
published, or sent until they confirm the wording is correct.

### Step 8: Freeze the candidate

Only when the human asks you to commit. The version bump and the notes travel in ONE
pull request, and its merge commit is the release candidate. Never merge them separately:
the release workflow requires the notes file in the tagged tree and the gate must run on
the tagged commit, so every separate notes edit is a new commit that voids any gate run
already under way. v2.18.0 rewrote its notes four times this way, each time to cover
work merged while a gate was running.

`scripts/new-release.ps1` does this step, and only this step:

    git fetch origin
    git worktree add ../devthrottle-release-v<version> -b release/v<version> origin/main
    cd ../devthrottle-release-v<version>
    # write docs/public/release-notes/v<version>.md here, covering the last tag up to origin/main
    .\scripts\new-release.ps1 -Version <version> -Yes

`-Version` and `-Yes` are what let a seat run it: without them it asks, and a
non-interactive session cannot answer. The human's sign-off is Step 7. It refuses unless
the checkout is exactly origin/main plus the notes file. It bumps
`Directory.Build.props` (the version lives in exactly that one file), commits the bump with
the notes, opens the `release: v<version>` pull request and merges it - and it does NOT tag.

The notes describe the last tag up to the base the pull request was cut from, so the
candidate must be exactly that base plus this one commit. The script checks both ends:
- If main moved before the merge, it stops with the pull request open and unmerged, and
  lists what landed. **If main moved**, recut in this order, because the release branch
  now exists locally and on origin:

      gh pr close release/v<version> --delete-branch

  Copy `docs/public/release-notes/v<version>.md` out of the release worktree, then leave
  it and, from the main repository folder:

      git worktree remove ../devthrottle-release-v<version>
      git branch -D release/v<version>

  Then cut the worktree again as above, copy the saved notes back in, extend them to
  cover what landed, and run the script again.
- After the merge it checks that the candidate's parent is that base. If not, it says so:
  the candidate carries work the notes do not cover, and must not be gated or tagged.

It prints the candidate commit, C. From here the candidate is frozen:
- The notes are never edited again for this candidate.
- Work merged to main after C waits for the next release. It is not added to the notes
  and does not move the candidate.

### Step 9: Gate the candidate once, then tag it (the human publishes)

Read this whole step before you start. The release seat runs the gate and confirms it is
green; the human runs the tag step, because pushing a tag is the one irreversible act.

1. **Run the release gate ONCE, on the candidate C, in a worktree of its own.** Only the
   release seat runs it, and it is one command:

       git fetch origin
       git worktree add ../devthrottle-gate-v<version> --detach <C>
       cd ../devthrottle-gate-v<version>
       .\scripts\test-local.ps1 -Parked -Configuration Release

   - `-Parked` adds the three suites the default run skips: `Gateway.Tests`, `Core.Tests` and
     `Gateway.UnitTests`. The installer suites (`cc-director-setup.Tests`,
     `cc-director-setup-engine.Tests`, `cc-director-setup-cli.Tests`) are already in the
     script's own project list. Do not run them again by hand.
   - `-Configuration Release` matches what is shipped. The script defaults to **Debug**.
   - The worktree is detached at C on purpose: main may move during the run, and the gate
     certifies C, not whatever main is when the run finishes.
2. **If the gate is red, C is not released.** Do not run the gate on C again: a worktree
   detached at C never sees a fix made on main, so a second run is a retry, and a retry that
   happens to pass hides the defect. Find the cause - a flaky test is a defect in the test,
   and it is fixed like any other - and fix it on main. Then cut a new candidate: a pull
   request that corrects the notes to cover everything merged since the last tag, merged when
   nothing else has landed since its base, whose merge commit is the new C. Nothing checks that
   last condition for you on a recut candidate: before gating it, confirm
   `git rev-parse <C>^` is the base you extended the notes against. Gate it once.
3. **The human tags the gated candidate:**

       .\scripts\new-release.ps1 -Tag <C>

   It refuses unless C is on origin/main, C itself changes the notes for its version (a later
   commit that merely carries them is not a candidate), and
   `scripts/assert-gated.ps1 <C>` accepts it - that script refuses a commit with no green
   `-Parked` run recorded against it. If `assert-gated.ps1` is not in the checkout, the tag
   step refuses too: nothing can prove C was gated. Only then does it tag C and push the tag.

   The tag goes on C even when main has moved on. A tag does not have to be the tip of main,
   and C is on main because it is the squash commit of the candidate pull request. That push
   is the last manual act. Monitor the Actions run.
4. Remove the gate and release worktrees and the local release branch (the merge deleted
   the remote one): `git worktree remove ../devthrottle-gate-v<version>`,
   `git worktree remove ../devthrottle-release-v<version>`, `git branch -D release/v<version>`.

**Do NOT create or publish a GitHub release by hand, and do NOT paste the notes into
the release body.** The workflow does both: it creates the release as a DRAFT, attaches
every asset, and publishes it only once `release-manifest.json` is provably attached.

Two defects live in the habit of doing it by hand, and both produce something that
looks right to whoever reads it:

- A release published before its assets exist is "latest" while being uninstallable.
  Measured on v1.8.8: published 10:48:48Z, assets attached 10:54:11Z, and a launcher
  that checked at 10:54:05Z failed. A failed update check renders exactly like being up
  to date, so nobody notices (issue #1079). If you pre-create a published release, the
  workflow attaches its assets to yours and the window comes straight back.
- Pasting or generating the body by hand is why the page was right by accident rather
  than by design; v1.8.7 shipped a list of internal pull-request titles (issue #1106).

If you ever cut a tag before the bump commit is on `main`, reconcile it: push the
tagged commit to a `release/v<version>` branch, open a pull request into `main`, and
merge it with a **merge commit, never a squash**, so the tagged commit stays
reachable from the mainline.

Once the tag exists, send the internal session "FINAL" so it deploys the changelog
and any concept pages.

### Step 10: Announce to the mailing list (gated)

**Precondition: the mailing-list opt-out feature must be live and tested.** See
"Mailing list and opt-out" below. If it is not live, stop here and record the
announcement as a fast-follow. Never send a bulk email without a working, honored
unsubscribe.

When the precondition is met:

1. Draft the announcement email from the canonical release notes (use the `/write`
   skill for voice).
2. Render an HTML mockup and get the human's approval in the chat.
3. Send only to members who have not opted out. Every message must carry a working
   one-click unsubscribe link and a physical postal address, with an honest sender
   and subject.

### Step 11: Post-release verification

- Confirm the latest-release download link serves the new executable.
- Confirm the internal changelog page shows the new version and links resolve.
- Confirm an unsubscribe link actually opts a test recipient out and is honored.

## Mailing list and opt-out (dependency for Step 10)

We keep our own list rather than renting a third-party service. The list is our
DevThrottle members (the `members` table in the Supabase project used by the
website). Before any release email can be sent, this must exist and be tested:

- **Opt-out state** on members: an `unsubscribed_at` timestamp and a per-member
  unsubscribe token (or a small `email_preferences` table). The send query selects
  only members who have not opted out.
- **One-click unsubscribe endpoint** on devthrottle.com: no sign-in required,
  flips the member to opted-out immediately, shows a plain confirmation page with
  a resubscribe option.
- **Preferences page** for signed-in members to manage email settings.
- **A real transactional email provider** for sending, not a personal mailbox, so
  that deliverability holds and the unsubscribe headers are correct.
- **Email template** carrying the unsubscribe link and a physical postal address.

This feature is built and tested once; thereafter Step 10 simply uses it.

## Examples

**User:** `/release-manager`

**Agent:**
1. Fetches origin, finds the mainline is 59 commits ahead of the last tag v1.1.0.
2. Proposes v1.2.0 (new features, backward compatible); the human agrees.
3. Assembles the change list; groups it into roles, transcription, mobile, security.
4. Verifies the two headliners against the code: the role model is shipped and on
   by default; the second is merged but defaults to off, so writes it as an opt-in
   preview rather than a headline feature.
5. Writes `docs/public/release-notes/v1.2.0.md` in the required shape.
6. Hands the file path to the internal session, which folds it into the changelog
   in draft and holds for FINAL.
7. The human signs off on the wording.
8. On request, runs `scripts/new-release.ps1 -Version 1.2.0 -Yes` from a worktree cut from origin/main,
   which merges the bump and the notes in one pull request and prints the candidate.
9. Runs the release gate once on the candidate in a detached worktree; it is green,
   so the human runs `new-release.ps1 -Tag <candidate>`, which checks
   `assert-gated.ps1` and tags the candidate; Actions attaches the executable; the
   agent sends the internal session FINAL.
10. The opt-out feature is not yet live, so the announcement email is recorded as a
    fast-follow rather than sent.
11. Verifies the download link and the changelog page.

---

**Skill Version:** 1.2
**Last Updated:** 2026-10-10
**Changes in 1.2:** The frozen candidate: the version bump and the notes merge in one pull
request, its merge commit is the candidate, the notes are never edited after, the gate runs
once on it, the tag goes on it, and work merged later waits. `scripts/new-release.ps1` now
does Step 8 only, and refuses if main moved under the notes; its `-Tag` mode tags only a
candidate `assert-gated.ps1` accepts. A red candidate is never re-run: the fix makes a new
candidate. One seat runs the release gate.
The gate is one command - the installer suites were already in the script, so the two
hand-run `dotnet test` lines are gone. The early warning is the default run, never `-Parked`.
The two stale claims about `new-release.ps1` and `docs/Release-Process.md` are gone.
**Changes in 1.1:** Recovered - version 1.0 was written but never landed, and was named
`skill.md` where the runtime requires `SKILL.md`, so it never loaded for any session
despite being referenced. Renamed and corrected on landing: Step 9 now records that
`main` is branch-protected, so `scripts/new-release.ps1` cannot finish (issue #1133) and
the version bump reaches main through a pull request like any other change; that the
version lives only in `Directory.Build.props`; and that `docs/Release-Process.md` is
itself stale (it still cites `CcDirector.Wpf.csproj` from before the move to Avalonia).
Added the tag-before-bump reconciliation recipe (merge commit, never squash). Refreshed
the worked example, which described the already-shipped v1.1.0 as prospective.
