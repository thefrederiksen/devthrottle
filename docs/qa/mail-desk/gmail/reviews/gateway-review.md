# Review of pull request 4336 - Mail Desk: the Gateway accepts Gmail and names both providers (G2)

Reviewer seat: Mail Desk - Reviewer - Gmail on the Gateway. Commit reviewed: `2570db978` (head of
`mail-desk-gmail-gateway`). 21 October 2026.

## Verdict

**Approve, with two test findings to fix before merge and one documentation finding.** The product code is right:
`channel add` accepts `gmail`, every unknown provider is still refused, every word a client shows is decided on the
Gateway, the new `help` field is additive, and no migration is needed. But two of the tests that are meant to guard
this would stay green if the guarded behaviour were broken; I broke it on purpose and they did.

## Findings

### F1. The page test for the Gmail row cannot see a provider branch in the client (rule 7 guard has a hole)

`packages/client-core/src/maildesk/mailDeskFixture.testkit.ts` gives the new Gmail row the Gateway's real words,
`provider: "gmail"` and `providerLabel: "Gmail"`. The file's own header says every word in the fixture is
deliberately odd so that a page that works out its own words fails; this row breaks that principle, so the test
`MailDeskPage_GmailChannel_ShowsTheGatewaysProviderLabelAndSignInSentenceVerbatim` cannot tell "rendered verbatim"
from "derived from `provider`".

Proof: I changed `MailDeskPage.tsx` line 100 to
`{channel.provider === "gmail" ? "Gmail" : channel.providerLabel}` - exactly the client branch rule 7 forbids - and
all 36 `src/maildesk` tests still passed. The existing Outlook row (`"Not Outlook At All"`) only guards the
Outlook path.

Why it must change: the test's name claims it proves the Gmail label is the Gateway's; it does not, and the very
next provider-specific edit in a `.tsx` view would pass review on its strength. Fix: give the Gmail row an odd
label (for example `providerLabel: "Not Gmail Either"`) and assert that.

### F2. The provider refusal is proven by one far-off word only; blank and near-miss providers are untested

The store (`MailDeskChannelStore.Add`, trim, lower case, exact ordinal match against `Providers.All`) is correct
today: by reading, `""`, `null`, `"g mail"`, `"gmailx"`, `"googlemail"` and `"outlook2"` are all refused, and
`"GMAIL"` / `" Gmail "` are accepted as `gmail`. But the only refusal tests use `"carrier-pigeon"`.

Proof: I changed the store so that a blank provider, or any provider starting with `gmail`, is quietly turned into
`gmail`. All 561 `FullyQualifiedName~MailDesk` tests still passed.

Why it must change: a blank `--provider` (a script with an empty variable) or a typo would then create a Gmail
channel for what may be an Outlook mailbox, whose every check fails - and nothing would catch the regression. Fix:
a theory over `""`, `"   "`, a missing `provider` in the POST body, `"gmailx"`, `"g mail"`, `"googlemail"` and
`"outlook2"`, each asserting `BadProvider` / 400 `bad_provider` and nothing recorded.

### F3. The contracts that define the shape were not amended (documentation, low)

- `docs/qa/mail-desk/phase-1/CONTRACT.md` line 19 still says a channel accepts "`outlook` only", and line 146 shows
  `--provider outlook`.
- `docs/qa/mail-desk/phase-7/CONTRACT.md` section 2 - which the client fixture names as its shape - still shows
  `emptyLabel` with `--provider outlook --why "<why>"`, and does not list the new `help` field on
  `GET /gateway/mail-desk/channels` at all.

Harm: the endpoint's declared shape and its real shape now differ; the next Developer or reviewer who checks a
client against the contract finds a field that the contract does not name and a sentence that no longer matches.
Fix: a short amendment note in each (or a Gmail-phase contract that supersedes them), naming `help` as additive.
`docs/cli-reference.md` is already right.

## What I judged and found sound

- **`channel add` and gmail.** `Providers.All` is `{ outlook, gmail }`; the label table has both; the refusal now
  names `'outlook' or 'gmail'` through `InWords`. Removing `Gmail` from `All` turns 14 tests red, so the acceptance
  side is well guarded.
- **Rule 7.** No provider branch in any `.tsx` view (the only provider word rendered is `channel.providerLabel`)
  and none in the command: `channel add` sends `--provider` as given, and `channel list` now prints the Gateway's
  `help` instead of its own line. Restoring the old hard-coded line turns two command tests red.
- **The `help` field.** Additive. The page's TypeScript type does not declare it and ignores it; no C# client reads
  `GET /channels`; `--json` prints the Gateway's answer unchanged, so its shape only gains a key (AXI: shape kept,
  `count: 0` still printed on an empty list). It mirrors the `help` key `GET /status` already carries. One
  behaviour to know, not a defect: a new command against a Gateway that predates this change prints no next step on
  an empty list, where it used to print the add command.
- **The failure sentence.** Nothing parses the check's failure sentence. The Gateway quotes it behind
  `SignedInFailingPrefix`; the only prefix match in Mail Desk code (`MailDeskEmailSection` line 234) is on the
  Gateway's own `KeptAfterFailuresHead`. The no-channel sentence is now built from `ChannelAddCommand` and gains the
  `cc-devthrottle` prefix; problems are folded per run, not keyed on the sentence, so the wording change opens no
  duplicate problem.
- **Migration.** None needed: `Provider` is `HasMaxLength(16)` with no check constraint in either the SQLite or the
  PostgreSQL migrations; `gmail` is five characters.
- **Remaining Outlook assumptions outside `screen.py`.** Searched the whole branch: Gateway Mail Desk code, the
  endpoints, the problems, the 08:00 email section, the memory, the template (`factory.yaml`, `seats/mail-desk.yaml`
  - which already says "Never assume a channel is Outlook"), the three prompts, the command, the client and the
  docs. Nothing left outside F3 and the historical phase documents (phase-0 to phase-7 READMEs, reviews, seed
  scripts, mission decision text), which record what was true at the time and are not live.

## Scope

- **Read:** the pull request description and its gate comment; `docs/qa/mail-desk/gmail/RULINGS.md`; the full diff
  of `2570db978` against its base; `MailDeskWords.cs`, `MailDeskChannelStore.Add`, `MailDeskEndpoints.ListChannels`,
  `MailDeskService.NoChannelSentence`, `MailDeskEmailSection` (prefix matches), `TriggerService` (where the
  no-channel sentence is used), the template and prompts, `maildesk_ops.py`, `MailDeskPage.tsx`, the phase-1 and
  phase-7 contracts, mission decision D20.
- **Ran** (devlinux, .NET 10 from `~/.dotnet`, polling file watcher, scratch `LOCALAPPDATA`):
  - `dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~MailDesk`: 561 passed, 0 failed.
  - `tests/test_maildesk_ops.py`: 125 passed (needed `requests` and `click` added to the brief's `uv run` line).
  - client-core `vitest run src/maildesk`: 36 passed.
  - Four deliberate breaks, each put back: Gmail removed from the provider list (14 red), the command's old
    hard-coded next step restored (2 red), a client branch on `provider` (0 red - F1), a store that turns blank or
    near-miss providers into Gmail (0 red - F2).
- **Could not reach / not in scope:** the repository gate on Windows (the pull request says so itself; its Linux run
  is "never ran", not a pass); the PostgreSQL database tests (`test-database.ps1`, needs Docker); `checks/screen.py`
  and its tests (another reviewer's).
