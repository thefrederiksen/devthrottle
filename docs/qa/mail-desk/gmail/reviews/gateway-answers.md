# Answers to the review of pull request 4336 - Mail Desk: the Gateway accepts Gmail (G2)

Developer seat: Mail Desk - Developer - Gmail on the Gateway. Answering `gateway-review.md` (commit reviewed
`2570db978`). 21 October 2026. All three findings are accepted and fixed.

## F1. The Gmail fixture row used the obvious label - accepted, fixed

The Gmail row in `packages/client-core/src/maildesk/mailDeskFixture.testkit.ts` now has
`providerLabel: "Not Gmail Either"`, as the Outlook row has "Not Outlook At All". The file's header now names both.
`MailDeskPage_GmailChannel_ShowsTheGatewaysProviderLabelAndSignInSentenceVerbatim` asserts that label.

Proof: I made the reviewer's break, `{channel.provider === "gmail" ? "Gmail" : channel.providerLabel}` in
`MailDeskPage.tsx`. It turns 2 of the 36 `src/maildesk` tests red (this test and the every-word-in-order card
test). I put it back.

## F2. Blank and near-miss providers were untested - accepted, fixed

- `MailDeskChannelStoreTests.Add_ABlankOrNearMissProvider_IsRefusedNamingBothAndRecordsNothing`: a theory over
  null, `""`, `"   "`, `"gmailx"`, `"g mail"`, `"googlemail"`, `"imap"` and `"outlook2"`. Each answers
  `BadProvider` with the refusal ending "give 'outlook' or 'gmail'", and the store holds no channel.
- `MailDeskEndpointTests.AddChannel_ABlankOrNearMissProvider_Answers400NamingBothAndRecordsNothing`: the same
  values except null, through `POST /channels`. Each answers 400 `bad_provider` naming both, and nothing is
  recorded.
- `MailDeskEndpointTests.AddChannel_NoProviderInTheBody_Answers400NamingBothAndRecordsNothing`: a body with no
  `provider` key. It answers 400 `bad_provider`, and nothing is recorded.
- The acceptance theory now also covers `"GMAIL"`.

Proof: I made the reviewer's break, turning a blank provider or any provider starting with `gmail` into `gmail` in
`MailDeskChannelStore.Add`. It turns 8 of the Mail Desk tests red. I put it back.

## F3. The contracts were not amended - accepted, fixed

Dated notes were added the way phase 3 amended phase 2: "changed by Mail Desk Gmail support, 21 October 2026".
Nothing that was there was rewritten.
- `phase-1/CONTRACT.md`: the providers row (now `outlook` or `gmail`, any other value refused naming both), the
  `mail_desk_channels` provider column (no migration), and a note under the command list (`--provider
  outlook|gmail`, sent as given, the Gateway decides).
- `phase-7/CONTRACT.md` section 2: the new `emptyLabel` wording and where it is written
  (`MailDeskWords.ChannelAddCommand`), the additive `help` field on `GET /gateway/mail-desk/channels` (the add
  command when `channels` is empty, nothing otherwise, which the command prints and the page ignores), and
  `gmail` -> "Gmail".

## Tests after the fixes (devlinux)

- `dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~MailDesk`: 578 passed, 0 failed (was 561).
- `tools/cc-devthrottle` `tests/test_maildesk_ops.py`: 125 passed.
- client-core `vitest run src/maildesk`: 36 passed.
