# macOS: why the install is a command, and what "download, double-click, it runs" would take

Written 7 October 2026 for the owner's question after one user's Mac failed five installs in a row
(issue #3411): *"How hard can it be to run an application on a Mac? Are we overcomplicating this? Why
can't it just run an executable and work?"*

The short answer: on a Mac, "double-click and it runs" is not a property of the program. It is a property
of **who signed the program and whether Apple has notarized it**: a Developer ID signature from a paid Apple
Developer Program membership, plus Apple's notarization ticket stapled to the download. Everything unusual
about our macOS install exists to work around not having those. The one-time cost of having them is small,
and it removes most of what went wrong.

## 1. What a Mac user gets today, and why each piece is there

| Piece | Why it exists |
|---|---|
| `curl ... \| bash` in Terminal | A browser download of an app that is not notarized by Apple is stamped with the quarantine flag, and Gatekeeper refuses to open it: "Apple could not verify 'DevThrottle Setup' is free of malware". On macOS 15 and later the old right-click, Open bypass is gone. `curl` downloads carry no quarantine flag, so the script is the product's supported path that does not end in that dialog (a person can also allow the app once under Privacy and Security after the refusal; that is a per-machine workaround, not an install path). |
| A SHA-256 check in the script | Because the download is not signed by a trusted identity, the script verifies it against the release manifest itself. The check used Python, and Python on a Mac is only a stub that hands off to Xcode. The user's Xcode was broken, so the install died at that line (fixed in #3288: the check now uses JavaScript for Automation, which every Mac has). |
| A setup wizard app, ad-hoc signed | It downloads and places the Director app, the launcher and the tools. Ad-hoc signing (`codesign --sign -`) gives the binary a stable identity but no trusted developer behind it, so every security surface on the Mac treats it as unknown. |
| A launcher as a raw executable under `~/Library/Application Support`, started by a per-user launch agent (launchd) | The launcher keeps the Director available: start it from the phone or the Gateway, apply updates, run at login. launchd is the Mac's way to run something at login. A raw executable there is the cheapest thing to build; it is also what macOS shows as "Item from unidentified developer" in Login Items and Extensions. |
| Everything per user, nothing under `/Applications` | No administrator password for a normal per-user install; only the recovery from an earlier elevated install may ask once, to give the files back to the user. A good rule; it also means we cannot ask the system to trust anything on our behalf. |

Nothing in that table is wrong on its own. Together they add up to an install that has five places to fail
that a signed app does not have, and the failures look like the machine's fault rather than ours.

## 2. What went wrong on the user's Mac, in order

Every fact below is from the reports his installs sent to the Gateway and from his emails. Nothing is a guess.

1. **21 September.** The hash check ran Python; his Xcode was broken; the install stopped. Fixed the next day.
2. **22 September (version 2.9.1).** The wizard registered the launch agent and launchd started the launcher once. The kernel ended that first run for code signing (`last exit reason = OS_REASON_CODESIGNING`). Why the kernel refused an ad-hoc signed program is unknown; it does not happen on our Macs.
3. **23, 24, 25, 28 September, 6 October.** Five more installs, versions 2.9.2 to 2.15.0. Every one found the launch agent already on disk and only asked launchd to **restart the job it already had** (`launchctl kickstart -k`). launchd answered every time with `78: EX_CONFIG`, `job state = spawn failed`, an empty standard error: the program was refused before it ran a line. The run counter went 1, 2, 4, 5, 6 across the five reports, which means launchd was holding the SAME job the whole time. Nothing any later installer did to registration could reach his machine, because registration was never run again.
4. His Mac also logged `pending spawn, domain in on-demand-only mode` at each attempt. In that mode launchd does not honour a job's start-at-load or keep-alive; it starts a job only when something explicitly asks. That matches a counter that advanced by exactly one per install and never at login.
5. **5 October.** We diagnosed root-owned files from a `sudo` run. The evidence for that was a reproduction on our side that produced the same signature, not anything from his machine. The fix shipped in 2.16.0 at 06:34 Coordinated Universal Time on 6 October; his sixth attempt ran at 03:21 Coordinated Universal Time with wizard 2.15.0, which did not contain it. His emails also show he never used `sudo`: he could not find his Library folder.

The structural defect, fixed in this change, is item 3: **an installer that trusts the job launchd already holds.** The reports also lacked the facts needed to settle the cause from our side (who owned the files, whether the log folder existed, what the launch agent on disk said, Gatekeeper's verdict, whether the program runs at all when asked directly). Those travel with every report now, and a success is reported as well as a failure.

## 3. What "download, double-click, it runs" takes on a Mac

### The parts

1. **An Apple Developer Program membership.** 99 US dollars a year, in the company's name (Center Consulting Inc., matching the Windows certificate). It gives a *Developer ID Application* certificate. Signing with it is what makes macOS treat the program as from a known developer.
2. **Notarization.** Apple's automated malware scan of each build, free with the membership, run from the release pipeline with `notarytool`. A notarized app downloaded by a browser opens after the ordinary one-time "downloaded from the internet, are you sure?" confirmation, instead of being refused as unverified. This is the step that removes the `curl | bash` requirement.
3. **Hardened runtime with the .NET entitlements.** Notarization requires the hardened runtime. A .NET app under it typically needs some of the entitlements Microsoft documents for macOS deployment (just-in-time compilation, unsigned executable memory, environment variables for the runtime, library validation off), and which ones depends on how the app is published and what it uses; the exact set is settled by testing the signed build. Nothing in the repository defines them yet.
4. **One app bundle, not three loose programs.** `Director.app` carries the launcher inside it (`Contents/Library/LaunchAgents/com.devthrottle.cc-launcher.plist` plus the launcher executable in `Contents/MacOS`), registered with Apple's `SMAppService` as a login item. The user sees "DevThrottle" in Login Items with our name, not "cc-launcher, item from unidentified developer". The user can still turn it off there; the system then tells the app so (the service reports that it requires approval) instead of silently refusing it. No launch agent file is written into `~/Library/LaunchAgents`, so there is no job for a stale install to inherit. The command-line tools stay where they are, inside `~/Library/Application Support`.
5. **A .dmg** (drag to Applications) or a signed `.pkg`. The .dmg is simpler and is what Mac users expect. The setup wizard becomes the first-run screen of the Director itself, as it already is on the second screen today.
6. **Updates that keep the seal.** A signed bundle must be replaced whole; changing one file inside it breaks the signature. The Director already downloads and swaps itself as a whole `.app` on macOS (`UpdateService.ExtractMacApp`), so the update path is the same, only signed and notarized at build time.
7. **Tooling.** The release pipeline already builds on an Apple Silicon runner. Signing adds a certificate in GitHub secrets, `codesign` with the entitlements file, `notarytool submit --wait`, and `stapler`. About a day to wire and prove.

### What it costs

| | |
|---|---|
| Apple Developer Program | 99 US dollars a year |
| Engineering | Signing and notarization in the pipeline: 1 to 2 days. Launcher inside the Director bundle as an `SMAppService` login item, with the install and update paths changed to match and proven on the Mac mini and in a clean virtual machine: 3 to 5 days. Retiring the Mac setup wizard and `install-mac.sh` in favour of the .dmg and first-run screen: 1 to 2 days. |
| Ongoing | Each release notarizes automatically (a few minutes of pipeline time). Certificate renewal once a year. |

### What in our architecture prevents it today

- The launcher is a separate raw executable with its own launch agent. `SMAppService` login items must live inside an app bundle. This is the one real design change.
- Three ad-hoc signed artifacts (Director, launcher, setup wizard) are built and uploaded as separate files. They need to become one signed bundle plus the command-line installer.
- The release pipeline has no signing identity for macOS. The Windows side already has the shape (`sign-windows` action, gated on a certificate profile); macOS needs its own.
- Nothing architectural blocks it on the Gateway or the phone side: both talk to the Director and the launcher over outbound connections and do not care how either was started.

## 4. Honest comparison

| | Today (`curl \| bash`, ad-hoc signed, raw launch agent) | Signed and notarized `Director.app` in a .dmg |
|---|---|---|
| First install | Open Terminal, paste a command, then a wizard | Download, drag to Applications, double-click |
| Gatekeeper | Avoided by using `curl`; any other path shows a malware warning | Passes; one ordinary first-open confirmation |
| Login Items and Extensions | "cc-launcher - Item from unidentified developer" | "DevThrottle" with the company name |
| A stale or refused launch agent | Possible: the file in `~/Library/LaunchAgents` outlives every install (what happened here; now rebuilt on every install) | Far less likely: the agent is defined inside the bundle and registered by the system from the bundle, and a user who turns it off is reported to the app as "requires approval" rather than hidden as a refusal |
| Code-signing kills | Ad-hoc signed programs are at the mercy of per-machine policy; one run was killed on the user's Mac and we do not know why | Developer ID plus notarization is the identity those policies check for |
| Company-managed Macs | Often refuse unsigned background items by policy | Approved once by the user in Login Items, or approved in advance by an administrator by Team ID through device management |
| Cost | Nothing | 99 US dollars a year, about a week of engineering once |
| What we can see when it fails | Only what our own reports carry (now: bounded, scrubbed diagnostics for the known launcher failure modes) | The same reports, and far fewer failures to report |

## 5. Recommendation

**Do both, in this order.**

1. **Now (this change):** rebuild the launch agent on every install instead of restarting whatever is there, make the Director repair a refused launcher itself at start-up so a fix reaches a Mac through the app's own update, and send bounded, scrubbed diagnostics for the known launcher failure modes on every failure and a report on every success. This is what the current round trip needs, and it holds whatever we decide about signing.
2. **Next (owner decision, 99 US dollars):** join the Apple Developer Program as Center Consulting Inc., sign and notarize the Director, and move the launcher inside the Director bundle as a system login item. Ship a .dmg. Retire `install-mac.sh` and the separate Mac setup wizard. About a week of work after the certificate exists. This is the only path to "download, double-click, it runs", and it removes the class of failure this user hit rather than any one instance of it.

Not recommended: keeping the current model and adding more checks to the script. Every check so far has been correct and has fixed the failure in front of it; none of them changes the fact that an unsigned background program on a Mac is refused for reasons the machine does not have to explain to us.
