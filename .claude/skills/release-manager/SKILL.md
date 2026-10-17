---
name: release-manager
description: Guided run-book for cutting a DevThrottle release - assemble the changes, write the release notes, coordinate the internal docs site, cut the tag, and announce it. Triggers on "/release-manager", "cut a release", "prepare a release", "ship a release", "do the release".
---

# Release Manager

The end-to-end run-book for releasing DevThrottle. **A release is a version tag on the LAST GOOD
COMMIT** - the newest commit on main that a full test run passed - plus the notes. Nothing is tested
at release time, so nothing at release time can go red. The target is one hour from "release it" to a
published installer, and most of that hour is the owner reading the notes and the build running.

This is enterprise software with a public repository. The release notes are a public, permanent
record. Get them right.

## Why it works this way (17 October 2026)

v2.23.0 took fifteen hours. The old flow merged a version bump and the notes into main, which made a
NEW commit that had to be tested from scratch; a red result meant a fix, another commit and another
full run, and two sessions fixed the same failure in parallel because nothing stopped a second release
seat. Meanwhile GitHub's full run had already passed the code. The owner's words: "Why the hell can we
not just release our software when we say, and then an hour later we have a release?"

So now:
- **Main is tested by itself.** `.github/workflows/ci.yml` runs the full suite (Release build, every
  suite including the parked ones) every two hours when main has moved. Every commit it passes is a
  last good commit. A red run opens an issue - fix or revert it at once, before other work.
- **A release is cut from the last good commit, never from main's tip.** `scripts/new-release.ps1
  -Release` adds ONE commit on top of it holding only the version in `Directory.Build.props` and the
  notes file, tags that, and merges it back to main with a merge commit.
- **The release workflow refuses anything else.** Its first job, "The tagged commit is a tested commit
  plus its notes", stops every build unless the tagged commit has a green full run, or its parent does
  and the tagged commit changes only those two files.
- **One release seat.** `new-release.ps1 -Claim` creates the branch `release-claim/v<version>` only if
  it does not exist; a second session is refused and told who holds it. Only the holder can release.
- Work merged after the last good commit is not in this release. It goes in the next one. Nothing
  waits for it.

## Quick Reference

| Step | What happens | Gate before moving on |
|------|--------------|-----------------------|
| 1 | Claim the release: `new-release.ps1 -Claim -Version <v>` | You hold the claim |
| 2 | Find the last good commit: `new-release.ps1 -Release -Version <v>`, answer N | You know exactly which commits ship |
| 3 | Assemble the change list, last tag .. last good commit | Full list, categorized |
| 4 | Accuracy gate: shipped vs designed | Every headline is real, running code |
| 5 | Write the canonical release notes markdown | File written in the required shape |
| 6 | Coordinate the internal documentation site | Internal session has the file, building in draft |
| 7 | The owner signs off on the notes | The owner says the wording is correct |
| 8 | Deploy the Gateway if the release needs it | `/healthz` reports a commit that has the Gateway side |
| 9 | Release: `new-release.ps1 -Release -Version <v> -Yes` | Tag pushed, workflow green, release Latest |
| 10 | Announce to the mailing list | Only if the opt-out feature is live and tested |
| 11 | Post-release verification | Download and unsubscribe both work |

## CRITICAL rules

- **Release only from the last good commit.** Never tag main's tip, never cut a "candidate", never run
  the full gate at release time. If no new commit has a green run since the last tag, there is nothing
  to release yet: start a run by hand (`gh workflow run CI --repo thefrederiksen/devthrottle --ref main`)
  and release when it is green.
- **Never claim a design document is a shipped feature.** Step 4 exists because it is easy to read an
  architecture document and write "we added X" when the code is not merged, or is merged but turned off
  by default. Verify against the code AT THE LAST GOOD COMMIT (`git show <last-good>:path`).
- **A release that adds or changes a Gateway hub method deploys the hosted Gateway BEFORE the tag.** The
  desktop Director auto-updates; the hosted Gateway does not. v1.9.9 shipped a Director that called a
  hub method the live Gateway had never heard of, and every session's key was refused with 401 until the
  Gateway was deployed hours later (#2457, #2459). Check with
  `git diff <last-tag>..<last-good> -- src/CcDirector.Gateway/Streaming/ src/CcDirector.Gateway.Contracts/`;
  if the Gateway has not been deployed at or after those changes, deploy first (`deploy-hosted-gateway`
  skill) and confirm `/healthz`.
- **The owner signs off the notes; the release seat then releases.** When the owner says to release,
  run `-Release -Yes` yourself - never hand the command back.
- **Never send a bulk email without a working, tested unsubscribe.** Step 10 is gated on it.
- **Work in your own worktree cut from origin/main**, never the shared checkout.
- **Plain English, no abbreviations. ASCII only, no emoji or special symbols.** This applies to the
  notes, the emails, and every message.

## Workflow

### Step 1: Claim the release

```bash
git fetch origin --tags
git worktree add ../devthrottle-release-v<version> --detach origin/main
cd ../devthrottle-release-v<version>
.\scripts\new-release.ps1 -Claim -Version <version>
```

Apply semantic versioning (`docs/Release-Process.md`): new backward-compatible features raise the minor
number; bug fixes only raise the patch number. Put the number in front of the owner together with the
notes, not as a separate wait. If the claim is refused, another seat is releasing: tell the owner which
one, and stop. If you abandon a release, run `-Unclaim`.

### Step 2: Find the last good commit

```bash
.\scripts\new-release.ps1 -Release -Version <version>      # answer N at the question
```

It needs the notes file to exist, so write a stub of at least 200 characters first, or run it again
after Step 5. It prints the last good commit, the commits since the last tag that it covers, and how many
newer commits on main are still waiting for their first green run. Those waiting commits are NOT in this
release - do not describe them in the notes.

### Step 3: Assemble the change list

```bash
git log <last-tag>..<last-good> --first-parent --format="%s"
git log <last-tag>..<last-good> --merges --format="%s"
```

Read every line. Group the changes into a small number of themes (for example
"session roles", "transcription", "mobile dictation", "security"). Separate the
few headline items from the many smaller ones.

### Step 4: Accuracy gate (mandatory)

For each headline item, verify against the code at the last good commit, not the documentation:

- Does real, running code implement it, or does only a design or plan document
  exist? Search for the actual types, endpoints, and interface elements.
- Is the feature turned on by default, or is it behind a flag that defaults to
  off? A feature that is merged but off by default is a preview, not a default.
  Say so plainly ("available now as an opt-in setting, off by default").
- Does the described mechanism match the real one? Do not invent a mechanism a
  reader could check and find false.

If a headline is design-only or flag-gated, change the wording to the truth, or
drop it, before writing the notes.

### Step 5: Write the canonical release notes

Write `docs/public/release-notes/v<version>.md` in your worktree. It may stay uncommitted: the release
step commits it on top of the last good commit. This file is the single source of truth: the GitHub
release body and the internal documentation website both draw from it.

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
- The transform from your markdown is the internal session's job. You only owe it
  clean, structured markdown in the shape above.
- To coordinate, open or find a session on the internal repository and hand it the
  path to your finished file:

```bash
cc-devthrottle session spawn D:/ReposFred/devthrottle_internal --controlled-by self
cc-devthrottle message send <session-id> "<one-line message with the file path>"
```

  Fleet messages must be a single line; newlines are truncated. For anything long,
  write a brief to a file and send the path.
- The internal session builds in draft and holds until you send an explicit
  "FINAL" message. Do not send FINAL until the release is published (Step 9).

### Step 7: The owner signs off

Publish the notes and the version number to the owner as a dev report (or show them in the chat when
the owner is there). Change whatever they ask. Nothing is tagged until they confirm.

### Step 8: Deploy the Gateway if the release needs it

See the CRITICAL rule. If `/healthz` already reports a commit at or after the last good commit's
Gateway changes, there is nothing to do.

### Step 9: Release

```bash
.\scripts\new-release.ps1 -Release -Version <version> -Yes
```

It checks the claim and the notes, makes the one release commit on the last good commit, proves it
changes only the version and the notes, pushes `release/v<version>`, tags it, pushes the tag, merges
the branch back to main with a merge commit (never a squash - the tagged commit must stay on main's
history), and gives up the claim. Then watch the release workflow to the end:

```bash
gh run list --repo thefrederiksen/devthrottle --workflow release.yml --limit 1
gh run watch <id> --repo thefrederiksen/devthrottle --exit-status
gh release view v<version> --repo thefrederiksen/devthrottle --json isDraft,assets
```

The release is done when it is published (not a draft) with `release-manifest.json` attached. v2.23.0
went from tag to published in nine minutes.

**Do NOT create or publish a GitHub release by hand, and do NOT paste the notes into the release body.**
The workflow creates the release as a DRAFT, attaches every asset, and publishes it only once
`release-manifest.json` is attached. A release published before its assets exist is "latest" while
uninstallable (issue #1079), and a hand-written body is how v1.8.7 shipped a list of pull-request
titles (issue #1106).

If the workflow's first job refuses the tag, read its message: the tagged commit was not a tested
commit plus its notes. Do not work around it.

Once the release is published, send the internal session "FINAL" so it deploys the changelog.

### Step 10: Announce to the mailing list (gated)

**Precondition: the mailing-list opt-out feature must be live and tested.** See
"Mailing list and opt-out" below. If it is not live, stop here and record the
announcement as a fast-follow. Never send a bulk email without a working, honored
unsubscribe.

When the precondition is met:

1. Draft the announcement email from the canonical release notes (use the `/write`
   skill for voice).
2. Render an HTML mockup and get the owner's approval in the chat.
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

**Owner:** "Release it."

**Release seat:**
1. Cuts a worktree, runs `-Claim -Version 2.24.0` and holds the claim.
2. Runs `-Release -Version 2.24.0`, answers N: last good commit 1a2b3c4, 23 commits since v2.23.0,
   4 newer commits waiting for their first green run.
3. Reads the 23 commits, groups them, checks the headliners against the code at 1a2b3c4.
4. Writes `docs/public/release-notes/v2.24.0.md` and hands the path to the internal session.
5. Publishes the notes to the owner; the owner says "looks good".
6. The Gateway is already on a later commit - nothing to deploy.
7. Runs `-Release -Version 2.24.0 -Yes`; watches the workflow; v2.24.0 is Latest with the manifest.
8. Sends the internal session FINAL; checks the download link.

---

**Skill Version:** 2.0
**Last Updated:** 2026-10-17
**Changes in 2.0:** A release is a tag on the LAST GOOD COMMIT. The full suite runs on main every two
hours by itself; `new-release.ps1` claims the release (one seat), finds the newest green commit, adds
only the version and the notes, tags it and merges it back. The release workflow refuses a tag on
anything else. The frozen candidate, the release gate at release time and the recut are gone from the
flow - v2.23.0 lost fifteen hours to them while its code had already passed every test.
**Changes in 1.2:** The frozen candidate (now retired).
**Changes in 1.1:** Recovered the skill; the version lives only in `Directory.Build.props`.
