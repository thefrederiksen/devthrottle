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
| 8 | Commit the notes file | Only when the human asks |
| 9 | Cut the release (the human publishes) | Tag exists, build attached |
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
- **This is a shared working tree.** Many sessions edit this repository at once.
  Stage only the files you created or changed, by name. Never `git add -A`.
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
  which counts happens later - on MERGED `main`, at the exact commit about to be tagged, after the
  version-bump pull request has landed (see "Cut the release"). A run against the pull-request head
  does not count: the squash merge produces a different commit.
  It is `.\scripts\test-local.ps1 -Parked -Configuration Release` PLUS the two
  installer test projects the script cannot reach, and it is the one release-blocking wait, because
  the release workflow runs ZERO tests and a pushed tag cannot be un-pushed. A release is the
  single place "fix it forward" is unavailable.
- You may run it now as an early warning, but an early run is NOT the gate: everything committed
  after it - notes, the version bump, other people's merges - is untested by it.

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

### Step 8: Commit the notes file

Only when the human asks. Stage the notes file by name (shared working tree):

```bash
git add docs/public/release-notes/v<version>.md
git commit
```

Follow the repository commit standard (the `/commit` skill).

### Step 9: Cut the release (the human publishes)

Read this whole step before you start. `docs/Release-Process.md` describes the web
interface flow, but it is out of date in two ways that will cost you time:

- It says the version comes from `CcDirector.Wpf.csproj`. That is from the WPF era.
  The application is Avalonia now, and **the version lives in exactly one file,
  `Directory.Build.props`**.
- It does not mention branch protection. `scripts/new-release.ps1` push-es `main`
  directly, and `main` requires a pull request, so **the script cannot finish**
  (issue #1133, still open). Do not reach for it and expect it to work.

The version-bump commit therefore reaches `main` like any other change - through a
pull request. That is how v1.1.0 shipped (pull request #1489).

1. Make sure `docs/public/release-notes/v<version>.md` is already merged to `main`.
   The workflow publishes that file verbatim and FAILS if it is missing, so a tag
   pushed without it burns a whole build and cannot be un-pushed.
2. Open a pull request that bumps `Directory.Build.props` to the new version, titled
   `release: v<version> - <one-line summary>`.
3. Merge the bump pull request once its ordinary local gate is green and the review is clean -
   never on a continuous integration result, which is never waited for.
4. **Run the release gate on MERGED MAIN, immediately before you tag.** Not on the pull request
   head: a squash merge produces a different commit, and `main` can move underneath you during a
   run that takes tens of minutes. Park the checkout on the exact commit you are about to tag,
   and run all three of these:

       git checkout main && git pull
       .\scripts\test-local.ps1 -Parked -Configuration Release
       dotnet test tools/cc-director-setup.Tests/ -c Release
       dotnet test tools/cc-director-setup-engine.Tests/ -c Release

   Every part of that is deliberate:
   - `-Parked` adds `Gateway.Tests` and `Core.Tests`, which the ordinary gate skips.
   - `-Configuration Release` matches what is shipped. The script defaults to **Debug**, while
     the continuous integration job this replaced ran `-c Release`. Releasing on a Debug-only
     run would quietly test a different build from the one users download.
   - **The two installer projects are NOT optional and NOT covered by the script.** They are not
     in `cc-director.sln`, so `test-local.ps1` never touches them - it runs nine projects, all
     under `src\`. The continuous integration job ran them separately, and the release publishes
     `cc-director-setup.exe`, so leaving them out ships the first thing a new user sees with no
     test behind it at all. Folding them into `-Parked` is the follow-up that removes this
     footgun; until then, run them by hand.

   If anything here is red, fix it forward on `main` and run the gate again. Do not tag until it
   is green: this is the one release-blocking wait, and it is local.
5. Create the tag `v<version>` on `main` at the gated commit and push it. That
   is the last manual act. Monitor the Actions run.

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
8. On request, commits the notes file (staged by name).
9. Opens the `release: v1.2.0` pull request bumping `Directory.Build.props`, merges
   it green, then the human publishes the v1.2.0 tag from main; Actions attaches the
   executable; the agent sends the internal session FINAL.
10. The opt-out feature is not yet live, so the announcement email is recorded as a
    fast-follow rather than sent.
11. Verifies the download link and the changelog page.

---

**Skill Version:** 1.1
**Last Updated:** 2026-07-14
**Changes in 1.1:** Recovered - version 1.0 was written but never landed, and was named
`skill.md` where the runtime requires `SKILL.md`, so it never loaded for any session
despite being referenced. Renamed and corrected on landing: Step 9 now records that
`main` is branch-protected, so `scripts/new-release.ps1` cannot finish (issue #1133) and
the version bump reaches main through a pull request like any other change; that the
version lives only in `Directory.Build.props`; and that `docs/Release-Process.md` is
itself stale (it still cites `CcDirector.Wpf.csproj` from before the move to Avalonia).
Added the tag-before-bump reconciliation recipe (merge commit, never squash). Refreshed
the worked example, which described the already-shipped v1.1.0 as prospective.
