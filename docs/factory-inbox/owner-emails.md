# Every place that emails the owner - and what became of each

Factory Inbox mission, phase 5 "Email off" (step D of `MISSION-FINISH.md`). Searched on 6 November 2026, in this
repository at `origin/main` a09edb1e7 plus this branch (the Teams folder looked at again after the review, at
`origin/main` ff55d00e4), and in `devthrottle_internal` at `main` 215ced86 (read only,
never changed from here).

The owner's decision (`MISSION.md`, decisions table, "Factory emails"): when Your inbox ships, the factory morning
email becomes a Your inbox message and the email stops. "Email me" and "tell me" mean Your inbox. A real email goes
out only when the owner names someone else, or says "send me an email".

How the search was done, so a reader can repeat it:

- Every caller of the owner-email clients in this repository: `git grep -E "SendOwnerAsync|SendOwnerForTenantAsync"`
  over `src`, and every user of `AccountNotifyClient`, `AccountNotifyByTenantClient` and `notify-owner`.
- Every use of the command `cc-devthrottle email owner`, the route `/account/email`, and orders such as "email the
  owner", "email me", "send me an email", in skills, briefs, factory definitions, scheduled run definitions and
  `tools/`.
- The built-in skills: `src/CcDirector.Gateway/Skills/Content/*.skill.md`.
- `devthrottle_internal`: a shallow clone of `main`, the same patterns, over `factory/`, `.claude/skills/`,
  `marketing/` and `tools/`.

Status words: **Changed** (done in this pull request), **Kept** (stays an email, and why), **Needs
devthrottle_internal** (a change this repository cannot make).

## 1. This repository - the Gateway

| Where | What it emailed | What became of it |
|---|---|---|
| `src/CcDirector.Gateway/Factory/Email/FactoryEmailSweep.cs` with `FactoryEmailSender.cs` | The 08:00 factory email: open problems, what needs the owner, one line per factory, the Mail Desk section. One email per account per day, when `factory_email_enabled` is on. | **Changed.** The sweep writes one Your inbox message per factory with news (`FactoryMorningMessage`): the factory's summary of the last 24 hours - the same one its Summary button shows, read through the one `FactoryInboxStore.Summary` - then that factory's part of the 08:00 report. `FactoryEmailSender.cs` is deleted; the sweep is handed no mail sender. Open problems no factory summary carries (a schedule in no factory, or in an archived or unregistered one) get ONE more message that morning, from "Morning report - schedules in no factory", and none when there are none; nobody reads a reply to it, so it offers no Reply (the Delivery Lead's ruling: email off must not make a problem silent). The composed report is still kept for the factory page ("Morning reports"). The switch keeps its name and still turns the morning summary on and off. |
| `src/CcDirector.Gateway/Running/ScheduleFailureNotifier.cs` (issue 4436), through its `ScheduleFailureMailer` delegate wired in `GatewayHost.cs` | "Schedule failed: <schedule>": one email per cause of a failed schedule, at most once an hour each - about fifteen a day on the owner's account on 7 November 2026. **This inventory missed it**: the search above looked for callers of the owner-email clients by name, and this notifier reached the mailer through a delegate the host wired to the license checks' owner mailer, so no search for those names found it. The morning check of 7 November did. | **Changed.** The notice is a Your inbox message, never an email: the `ScheduleFailureMailer` delegate is gone and the notifier holds no mail sender. A schedule in a factory is told from that factory, seat "Schedule failure", so Reply leaves a message in the factory's Factory inbox, as the morning summary does; a schedule in no factory is told from "Schedule failure - schedule in no factory" and offers no Reply. The hourly limit per schedule and cause, the push to phones, the fleet notice and Notify Off all stay as they were. The body's last line now reads "To stop these messages, set the schedule's Notify to Off." |
| `src/CcDirector.Gateway/Api/AccountEmailEndpoint.cs` (`POST /account/email`) | The relay behind `cc-devthrottle email owner`. | **Kept.** It is the "send me an email" path the decision keeps. The skills now say to use it only then. |
| `src/CcDirector.Gateway/Api/NetDiagAlertService.cs` | "Your home network is slow" alerts, capped per day. | **Kept, question for the owner.** A system alert about the machine, not a factory and not a session. Outside this step's decision. |
| `src/CcDirector.Gateway/Licenses/LicenseOwnerMailer.cs` | License Switching: "no account has room". | **Kept, question for the owner.** A system alert, not a factory. |
| `src/CcDirector.Gateway/GatewayHost.cs` supervisor escalation (`sendOwnerEmail`, `GatewaySupervisorEnvironment`) | The session supervisor's escalation when a session could not be recovered. | **Kept, question for the owner.** A system alert. It could become a Your inbox message from the session; that is a product decision this step does not make. |
| `src/CcDirector.Gateway/Teams/TeamInvitationMailer.cs` (`ITeamInvitationMailer`) | Team invitations. | **Kept.** Goes to the person invited, not the owner. |
| `src/CcDirector.Gateway/Teams/TeamBillEndedNotice.cs`, through `ITeamBillEndedMailer` (implemented by `TeamInvitationMailer`) | Once per ended bill: the team's Owner is told the team's bill has ended, when someone else is refused an invitation or an accept because of it. | **Kept, question for the owner.** A billing notice to a team's Owner, ordered by the owner's Teams ruling of 7 October ("tell the Owner"), not a factory and not a session. It must reach a team Owner who may not open DevThrottle until the bill is paid, which is what an email is for, and Your inbox is per account, so it would land in an inbox that person is not reading. Added after the review of pull request 4664 (finding 2). |
| `src/CcDirector.Core/Sessions/FleetPreambleTemplate.cs` | Nothing: it tells every session that "me / my account / email me" means the signed-in user. | **No change here, noted.** An identity rule, not an order to email. Rewording "email me" there changes the start text of every session, so it is left to a change of its own. |

## 2. This repository - the built-in skills

| Where | What it said | What became of it |
|---|---|---|
| `dev-throttle.skill.md`, "Who the user is" | "email me" means the signed-in user. | **Changed.** New section "Telling the user something: Your inbox, not email": "email me" and "tell me" mean `cc-devthrottle inbox send`; `cc-devthrottle email owner` only when the user names someone else or says "send me an email"; a report is a dev report, announced in Your inbox. Version 6.2. `.claude/skills/dev-throttle/SKILL.md` regenerated from it. |
| `terminology.skill.md`, "Your inbox" | Already says "Email me" and "tell me" mean Your inbox. | **No change needed.** |
| `dev-reports.skill.md`, `devthrottle-method.skill.md`, `fleet-comms.skill.md`, the rest | No order to email the owner. | **No change needed.** |

## 3. This repository - the factory definition check

| Where | What became of it |
|---|---|
| `src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionCheck.cs` | **Changed.** A new notice on any seat whose brief tells it to email the owner (`cc-devthrottle email owner`, `/account/email`, "email me", "email the owner", "send the owner an email", "send the report to him by email"), on the brief's line, in words decided once on the Gateway (`BriefEmailsTheOwner`). A prohibition ("never email the owner") is not flagged, email to anyone else is not flagged, and a brief holding the owner's own "send me an email" is not flagged. A notice, not an error: the words are matched, not understood. |

## 4. This repository - the command-line tools

| Where | What became of it |
|---|---|
| `tools/cc-devthrottle` `email owner` | **Kept** - the "send me an email" path. Its group help now says "Email the owner, only when asked. Otherwise: inbox send." The pinned catalogue description is left as it was (a test pins it byte for byte). |
| `tools/cc-devthrottle` `factory email status/on/off/list/show` | **Changed words only.** The help says it is the 08:00 morning summary into Your inbox; the command names stay, because agents already hold them. |
| `tools/cc-devthrottle` `inbox send` | Already on main: "Tell the owner something in Your inbox, instead of emailing him." |
| `tools/cc-gmail`, `tools/cc-outlook` | **Kept.** General mail tools; they send to whoever is named. |

## 5. This repository - definitions, scheduled runs and documents

- No factory definition, seat brief or scheduled run definition lives in this repository; they are on the Gateway
  or in `devthrottle_internal`.
- `docs/factories/definition-format.md` and `src/CcDirector.Gateway/Factory/Definition/Schemas/seat.schema.json` used
  "Email the owner" as the example branch step. **Changed** to "Tell the owner in Your inbox".
- Historical mission documents (`docs/missions/...`, `docs/reviews/...`, `docs/qa/factory-control/morning-email/`)
  describe what was true then. **Left as written.**

## 6. devthrottle_internal - what it still needs

None of these were changed from here. Each is a seat brief or a script that emails the owner; once the Gateway with
this change is deployed and those factories are published again, the definition check flags each brief.

| Where | What it emails | What it needs |
|---|---|---|
| `factory/AUTONOMY.md` (grant G5 "Email the owner"), `factory/BOSS-BRIEF.md` ("The memo", Mondays), `factory/EXPANSION.md`, `factory/FACTORY.md` | The rule every seat follows: escalate by `cc-devthrottle email owner`. | Rewrite G5 as "tell the owner in Your inbox" (`cc-devthrottle inbox send`). This is the root: the seat briefs below quote it. |
| `factory/agents/boss.yaml` | The Monday memo and "something waits on the owner". | Your inbox. |
| `factory/agents/outreach.yaml`, `factory/outreach/outreach_send.py` ("Email the owner now.") | A person replied; bounces; a merge refused. | Your inbox. |
| `factory/agents/health-watch.yaml` | "DevThrottle HEALTH - <what is down>". | Your inbox (or kept as an alert, if the owner rules health is an alert). |
| `factory/agents/defects.yaml` | The 06:30 morning note; a merge refused. | Your inbox. |
| `factory/agents/mail-desk.yaml` | A human message arrived. | Your inbox. |
| `factory/agents/marketing.yaml` | Each day's uploaded videos. | Your inbox. |
| `factory/agents/usage-watch.yaml`, `factory/agents/sales-desk.yaml` | "DevThrottle Sales - <who> - <the moment>". | Your inbox. |
| `factory/agents/onboarding.yaml` | A change merged or reverted; something to approve. | Your inbox. |
| `factory/agents/merge-review.yaml` | A serious finding. | Your inbox. |
| `factory/marketing/seats/heycatch.yaml` (and `seats/README.md`) | The 06:30 HeyCatch summary; "he needs to sign in". Its drawn steps include "Email the owner". | Your inbox, steps renamed. |
| `factory/marketing/seats/replies.yaml` | Nothing: "cold-email the owner" describes agencies that write to him. | No change needed. |
| `factory/agents/motivation-videos.yaml`, `marketing/motivation/factory/digest.py`, `.claude/skills/motivation-factory/SKILL.md` | Every motivation video link after each batch. The owner once asked: "make sure to email me the link to all of these videos periodically." | **Question for the owner:** that request predates the decision that "email me" means Your inbox. Ask whether he still wants it as an email. |
| `.claude/skills/youtube-channel-manager/SKILL.md` | The daily YouTube report as HTML email. | Your inbox, with the report as a dev report. |
| `tools/mentor/send_report.py` | The weekly mentor report with attachments. | **Question for the owner:** attachments do not fit Your inbox; a dev report announced in Your inbox is the likely answer. |
| `.claude/skills/factory-interviewer/` | Nothing: no order to email anyone. | It writes new seat briefs, so it should say "tell the owner in Your inbox", never "email the owner". One sentence. |
