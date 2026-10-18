# Review of pull request 4239: mailbox problems, the channel row's sign-in, and the 08:00 Mail Desk section

Reviewed against sections 6.2, 7 (the spend problem) and 9 of `docs/qa/mail-desk/phase-3/CONTRACT.md` on
`origin/mail-desk-phase-3`, with the one deviation the Tech Lead accepted (the very first good check of a mailbox is
written too). Branch `mail-desk-p3-mailbox-problems-email` at `646d31de6`.

**Verdict: no defect in what the pull request builds.** Two findings, both about inputs the change leaves as
placeholders for later tasks. Neither breaks anything while the work stays on the phase branch; both must be closed
before the phase branch reaches main.

## Findings

### 1. Nothing writes `mail_desk_channel_checks` yet, but `signedIn` already reads only that table

**What breaks.** `MailDeskChannelFold` no longer takes `signedIn` from the Mail Desk start's trigger. It reads only
`MailDeskChannelCheckStore.ByChannel`. `Record` and `RecordAll` have no caller outside the tests: a search of
`src` for `RecordAll(`, `Checks.Record(` and `MailDeskChannelChecks.` outside test projects finds nothing. The class's
own comment says the screening (task F) will call it. So until task F lands, every channel row shows
`signedIn: unknown`, after any number of good checks. The Open problems card and the 08:00 email also never show a
`mailbox-failed` problem, because no row ever exists.

**Why it must be tracked.** On the phase branch this is a known gap between tasks. On main it would be a regression:
phase 1 showed `yes` or `failing` from the trigger's outcome, and this change shows `unknown` for every mailbox. Since
pull request 4229, one mailbox's failure no longer fails the run, so the trigger alone cannot say which mailbox
failed. Until task F lands, a failing mailbox is shown nowhere. **The phase branch must not merge to main before
task F wires `RecordAll` into the screening.** A test should then prove the whole path: a report naming one failed
channel raises the problem and turns that channel's `signedIn` to `failing`.

### 2. The spend values are fixed: the limit is always $5.00, and the limit has never stopped a call

**What breaks.** `GatewayHost` builds `MailDeskSpendOf` as
`new MailDeskSpend(SpentMicros(tenant), MailDeskSpend.DefaultLimitMicros, StoppedUtc: null)`. The comment there says
so. Two results:
- The 08:00 email's spend line always reads "of the $5.00 limit", whatever `mail_desk_spend_limit_usd` holds once
  task D adds it.
- `StoppedUtc` is always null, so the section 7 spend problem can never open, and the email's stopped-spend sentence
  can never show.

`git grep mail_desk_spend_limit_usd origin/mail-desk-phase-3 -- 'src/*.cs'` finds nothing outside tests, so this
depends on task D and task F. The fold and the problem are tested with real inputs
(`Problems_TheLimitStoppedACall_OpensTheSpendProblemInTheContractsWords`,
`Section_SpendLimitStoppedTheModel_SaysTheSpendSentence`). The gap is only in the one wiring line.

**Why it must be tracked.** An email that states the wrong limit tells the owner something false about money. And a
spend problem that cannot open defeats the point of section 7. Task D must replace the default with the setting, and
task F must supply `StoppedUtc`. There should be a test through `GatewayHost`, not only through the fold.

## Checked and found sound

- **A problem that never clears, clears too early, or is cleared by another mailbox.** `ByChannel` groups by channel
  id, and `MailboxProblems` throws when a check's channel is not the mailbox being folded. One mailbox's good check
  therefore cannot reach another's run of failures (`Problems_TwoMailboxesOneFailing_OnlyTheFailingOneHasAProblem`;
  `RecordAll_OneReportOfTwoMailboxes_KeepsEachByItsOwnRule`). A run of failures closes only on a good row of the same
  channel, and the store always writes the first good row after a failure, because it compares with the channel's own
  newest row. The skipped "good after good" case cannot hide a clear. A report that arrives late is placed by
  `CheckedUtc`, both when the store decides to write and in the fold, so the result still follows check time. A
  failure after a hand resolve starts a new problem, and a failure before the resolve time stays in the resolved one.
- **Resolve.** The mailbox lookup runs only after the scheduled-run and trigger-check lookups miss, so those two paths
  are unchanged. Only a run's head id resolves its problem. A later failure, a cleared problem, a good check, a second
  resolve, a removed mailbox and the switch off are each refused with a sentence. The spend problem is refused with
  409 and told how it clears.
- **Tenant scope.** The new entity has `ApplyTenantScope`. Every store read and write goes through
  `CreateContext(tenant)`. A channel of another account is refused at write
  (`Record_AnotherAccountsChannel_IsRefused`, `ByChannel_TwoAccounts_EachReadsOnlyItsOwn`).
- **Factory.** Mailbox problems carry the channel's factory, which the channel store always sets to `mail-desk`. The
  spend problem carries `MailDeskWords.DefaultFactory`. When `Problems` is narrowed to another factory, both are
  skipped.
- **The 08:00 email.** When the switch is off, there is no channel, or only removed channels, the section is null. The
  composer then takes exactly its old path: one test compares that email with a composed copy, and another compares
  it with hashes taken before the change. Every sender, subject, address, layer, reason and change line goes through
  `E()`, and the test with a `<script>` subject passes. The plain text puts each subject on one line. Junked, kept and
  not-screened emails are read from `DecidedBetween` with no page limit. The section has no preview or body field, and
  its log line carries only counts. The section is only in the 08:00 email (`Compose_TheFourPMCheck_NeverCarriesTheSection`).
- **Migrations.** Both databases get a new table only. `Up` touches no existing table, and `Down` drops only the new
  table. Column sizes match the model's limits.

## Scope

- **Read:** the whole diff of `origin/mail-desk-phase-3...HEAD`, apart from the two generated `Designer.cs` files and
  the model snapshots, which I only checked for build success. That covers `MailDeskChannelCheckStore`,
  `MailDeskProblems`, `MailDeskEmailSection`, `MailDeskChannelFold`, the `CronRunResultFold` and
  `CronRunResultService` changes, `FactoryEmailComposer`, `FactoryEmailSweep`, the `GatewayHost` wiring, both
  migrations, and the names and key assertions of the new tests.
- **Ran:** `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter
  "Category=MailDesk|FullyQualifiedName~CronRunResult|FullyQualifiedName~FactoryEmail"`: 498 passed, 0 failed,
  0 skipped.
- **Not reached:** I did not run `scripts/test-database.ps1`. The Tech Lead reports 81 of 81. I did not apply the
  migrations to a database holding real data, so the Up and Down judgement comes from reading the code. I did not
  recompute the golden hashes against the old composer. The path where a failed decision is updated in place (and
  whether its `DecidedUtc` moves into the next email's window) belongs to task F and is not on this branch.
