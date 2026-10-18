# Answers to the review of pull request 4230: the Mail Desk model call

The review is `model-call-review.md`, written against commit `e2013cdc5`. These are the Developer's answers. Each point is accepted or declined with a reason, and each change names the commit and the test that proves it.

## Finding 1: an unexpected exception from the web client escapes the question

**Accepted.** This is the boundary with an outside service, where the coding standard puts a catch, and `HostedCandidateJudge`, the class this one is shaped like, ends with the same catch.

- **The fix.** `MailDeskModel.AskAsync` now ends with a catch of `Exception`. It becomes a typed failure with a new error kind, `unexpected` (`MailDeskModelErrorKinds.Unexpected`). The error sentence is "the hosted model's reply could not be read (<exception type>)", which carries the type only. The message is never recorded, because it could carry the host or the body. The usual "AskAsync FAILED" line is logged with the error kind and the elapsed time.
- **The test.** `MailDeskModelTests.AskPersonOrSpamAsync_ASuccessReplyWithAnUnknownCharacterSet_IsAnUnexpectedFailureNotAnException` sends the reviewer's exact case: HTTP 200 with `Content-Type: application/json; charset=no-such-charset`. It asserts a `Failed` result of kind `unexpected` that names `InvalidOperationException`, and the logged failure line.
- **It can fail.** With the final catch narrowed to `NotSupportedException`, that test went red (1 failed of 52) and nothing else did. The change was then put back.

## The Tech Lead's decision: the factory asks the AI gate

**Done, as decided.**

- **The change.** `MailDeskModelFactory.Create` now takes the `TenantAiGate`. It calls `EnsureAllowedForTenant(tenant, EntitlementScopes.Wingman)` before it reads the key, the way the dictionary-suggestion and Mentor factories do.
- **When the gate does not allow the call,** the factory returns a model whose every question fails with the gate's own sentence: the `TenantAiRefusedException` message, which names the scope, the verdict and the reason. No key is read and no call is made.
  - A `Refused` verdict is error kind `plan-refused`.
  - An `Unknown` verdict (the plan could not be read) is `plan-unknown`, so a failed read is never reported as a refusal. This follows the gate's own rule.
- **A self-hosted Gateway** passes `TenantAiGate.SelfHost`, which allows every call (decision D5).
- **The test.** `MailDeskModelFactoryTests.Create_TheAiGateRefusesTheAccount_EveryQuestionFailsWithTheGatesSentenceAndNoKeyIsRead` uses a hosted gate (entitlements wired, over the in-process test database the gate's own tests use) and a tenant with no account to check. Both questions fail with `plan-refused`, and their sentence holds `scope=wingman` and the gate's reason. The key reader is never called, and no request is made.
  - The other factory tests pass `TenantAiGate.SelfHost`.
- **It can fail.** With the gate call removed, that test went red (1 failed of 52). The change was then put back.
- **Not tested:** the `plan-unknown` path. Making the gate's database read fail needs a broken database. The mapping is one line on the gate's verdict.

## Notes the review recorded

- **The strict answer parsing stays,** by the Tech Lead's ruling. The contract says exactly one listed letter after trimming, so `a`, `A.` and `AB` are off-labels. Phase 0 measured with a more lenient parser, so the real off-labels rate may be higher than phase 0 saw. Phase 6 measures how often the real model adds a full stop.
- **A failed call's cost stays null, and the spend limit can undercount.** A question that fails with a timeout, a status that is not success, a broken connection, `unexpected` or `not-a-chat-completion` carries `CostUsd = null`, because no token count came back. The backend may still have billed some of those calls. A call that times out after the model has generated its answer is the clear case. So the spend limit, which sums the costs that are known, can undercount by exactly those calls. Task F must not read `null` as free. At about $0.0001 a call, the undercount is that much per failed call.

## The numbers

- **After these changes:** 52 tests in `MailDeskModelTests` and `MailDeskModelFactoryTests`, all passing. Together with the rest of the `MailDesk` category, `SpokenLanguageContractTests`, `AiCallTagTests` and `IncludedModelTypeSurfaceTests`: 106 passed, 0 failed, 0 skipped.
- **Each new promise was broken once on purpose and went red,** as listed above.
- **The whole `Gateway.UnitTests` project was not rerun for these changes.** Its earlier run is in the pull request description.
