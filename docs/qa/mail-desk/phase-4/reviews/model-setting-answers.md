# Answers: review of the loopback model setting

The review is `model-setting-review.md` beside this file. Answered by the rig Developer, 20 October 2026.

## Finding 1. No test proves the Gateway's screening uses the configured address - ACCEPTED, FIXED

`src/CcDirector.Gateway.Tests/MailDeskModelEndpointWiringTests.cs` now reads, by reflection, the model factory the
screening itself holds (`MailDeskService._modelFor`, set by the wiring line in `GatewayHost`), builds the account's
model with a key in the throwaway vault, and checks where it sends (`MailDeskModel._chatUrl`):

- `GatewayHost_LoopbackKeyInConfig_TheScreeningsModelAsksThatAddress`: `http://127.0.0.1:7993/v1/chat/completions`.
- `GatewayHost_NoKeyInConfig_TheScreeningsModelAsksTheHostedEndpoint`: the hosted endpoint's address.

**Shown to fail with the line removed.** With `baseUrl: MailDeskModelBaseUrl` deleted from the screening's lambda in
`GatewayHost.cs`, the run was:

```
Failed CcDirector.Gateway.Tests.MailDeskModelEndpointWiringTests.GatewayHost_LoopbackKeyInConfig_TheScreeningsModelAsksThatAddress
  Assert.Equal() Failure: Strings differ
  Expected: "http://127.0.0.1:7993/v1/chat/completions"
  Actual:   "https://devthrottle.com/api/v1/chat/compl"...
Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4
```

With the line back: 4 passed.

**The rig guard.** `docs/qa/mail-desk/rig/run_qa.py` now fails loudly unless the fake model received the model
questions the observe case expects, every one for `devthrottle/wingman-fast` and carrying the rig-only key (the fake
answers 401 to any other key, so a call it logged came from the throwaway Gateway with the rig's vault). At the end of
every run it checks the throwaway Gateway's log for any line naming `devthrottle.com` and fails if there is one.

## Observation: the constant passed when the key is absent - ACCEPTED, CHANGED

`MailDeskModelEndpointConfig.Read` now returns null when the key is absent, `GatewayHost.MailDeskModelBaseUrl` is null
then, and the factory keeps its own `TranscriptionEndpointResolver.ResolveWingman` choice, so a later change of that
endpoint reaches the Mail Desk too. The absent case is proven through the screening by the second test above.

## Tests run

```
dotnet test src/CcDirector.Gateway.Tests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDeskModelEndpointWiring"
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDeskModel"
Passed!  - Failed:     0, Passed:    74, Skipped:     0, Total:    74
```
