# Review: the loopback model setting (commit 7af3e6c8e)

Mail Desk phase 4 (issue #4213). Reviewer seat opened by the phase 4 Tech Lead. 20 October 2026.

## Scope

**Read:** the whole of commit 7af3e6c8e - `MailDeskModelEndpointConfig.cs`, the two edits to `GatewayHost.cs`, the
edit to `MailDeskModelFactory.cs`, both new test files - against `docs/qa/mail-desk/phase-4/CONTRACT.md` section 5
(the paragraph starting "The model's address was a constant"). Also read, to check what the setting can reach:
`TranscriptionEndpointResolver` (`src/CcDirector.Core/Configuration/TranscriptionEndpoint.cs`), how `MailDeskModel`
builds its request address, `CcDirectorConfigService.ReadRaw`, `FactoryAgentsConfig.IsEnabled`, every write of
config.json from the Gateway (`SettingsEndpoints.cs`), and the one production construction of `GatewayHost`
(`GatewayService.cs:125`).

**Ran:**

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~MailDeskModel"`: 74 passed, 0 failed.
- `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~MailDeskModelEndpointWiring"`: 3 passed.
- A scratch console program, outside the repository, referencing the built `CcDirector.Gateway.dll`, that fed 29
  hostile values through the real `MailDeskModelEndpointConfig.Parse` and, for each one honoured, rebuilt the request
  address exactly as `MailDeskModel` does (`baseUrl.TrimEnd('/') + "/chat/completions"`) and printed its host.

**Could not reach:** the live phase 4 rig itself (no fake model was started); behaviour on Windows (run on Linux only);
how the operating system resolves the name `localhost` on a given machine (the contract allows that name, so it was
not treated as a defect).

## Verdict

**Sound, with one gap in the tests.** The rule in the contract holds for every value tried; production is unchanged
when the key is absent; nothing else is redirected; no credential is logged; no account can set the key. One finding:
no test proves that the Gateway's screening actually passes the configured address to the model, so the line that
does it can be removed with every test still green.

## What was checked and held

- **Hostile addresses.** Refused: `localhost.example.com`, `127.0.0.1.nip.io`, `http://127.0.0.1@evil.example`,
  `http://evil.example@127.0.0.1`, `http://127.0.0.1\@evil.example`, the IPv4-mapped `[::ffff:127.0.0.1]`, the
  trailing-dot forms `localhost.` and `127.0.0.1.`, a percent-encoded host, `xn--localhost`, a link-local IPv6
  address, an empty query `?` and an empty fragment `#`, a port above 65535. Honoured, and every one of them rebuilt
  to a request whose host is `127.0.0.1`, `[::1]` or `localhost`: the decimal `2130706433`, the octal `0177.0.0.1`,
  the hexadecimal `0x7f.1`, the short `127.1`, the long `0:0:0:0:0:0:0:1`, `[::1%25eth0]`, double slashes and `..` in
  the path, and a value with an embedded space or line break (escaped into the path). The value returned is the raw
  text rather than the parsed address, but appending `/chat/completions` cannot change the host because a query and a
  fragment are refused; the probe confirmed the rebuilt host matched in every honoured case. Redirects from a loopback
  server are the operator's own server, not a way for an outside party in.
- **Nothing else is redirected.** The address is threaded into one call, `MailDeskModelFactory.Create`, from one place,
  the screening lambda at `GatewayHost.cs:2555`. `TranscriptionEndpointResolver` is untouched, so transcription,
  Wingman, cleanup and speech keep the constant.
- **Production unchanged when absent.** `Read` returns `TranscriptionEndpointResolver.DevThrottleBaseUrl`, and
  `ResolveWingman` returns that same constant for every mode, so the factory sends to the same address as before.
- **A broken config.json.** `ReadRaw` throws on unparseable JSON, but the production construction already calls
  `FactoryAgentsConfig.IsEnabled`, which reads the same file the same way earlier in the same constructor, so no new
  failure is introduced.
- **Credential and logging.** `Read` logs only the key's name and the host, never the value's path or the vault key.
- **Reachable only by the machine operator.** The key is read once at construction. The only Gateway endpoint that
  writes config.json (`PUT /gateway/ai-provider`, `SettingsEndpoints.cs:619`) writes the fixed key
  `transcription_mode` and nothing an account supplies.

## Findings

### 1. No test proves the Gateway's screening uses the configured address

- **Where:** `src/CcDirector.Gateway/GatewayHost.cs:2556` (the `baseUrl: MailDeskModelBaseUrl` argument), and the
  test that claims to cover it, `src/CcDirector.Gateway.Tests/MailDeskModelEndpointWiringTests.cs:51` and `:61`.
- **The harm:** the wiring test is named `GatewayHost_LoopbackKeyInConfig_AsksThatAddress`, but it asserts only that
  the property `GatewayHost.MailDeskModelBaseUrl` holds the value. The unit test
  `Create_WithTheConfiguredBaseUrl_AsksThatAddressWithTheVaultKey` calls the factory directly with `baseUrl:` supplied
  by the test. Nothing builds a `GatewayHost` and checks where the screening's model actually sends. So the one line
  that joins the two - the argument at `GatewayHost.cs:2556` - is covered by no test.
- **Failure scenario:** a later edit to the screening lambda (for example a change of signature during phase 5, or a
  merge that keeps the older lambda) drops `baseUrl: MailDeskModelBaseUrl`. All 77 tests above still pass. The
  factory falls back to `ep.BaseUrl`, the hosted endpoint, so the phase 4 rig's throwaway Gateway sends its questions
  to `https://devthrottle.com/api/v1` with whatever key its vault holds - the exact outcome the contract's sentence
  "if either setting were not honoured, the call would reach the real service with a real key" rejected a proxy to
  avoid. Found by reading, not by running the mutation; the claim is that no listed test reaches line 2556, which a
  search of every test project for `MailDeskModelBaseUrl` and `MailDeskScreening` confirms.
- **Severity:** low today (the line is correct now, and the rig would likely see failed calls rather than real
  answers), but the test's name claims a proof it does not give.

## Observation, not a finding

`MailDeskModelEndpointConfig.Read` (line 38) returns the constant `DevThrottleBaseUrl` when the key is absent, and the
Gateway always passes that non-null value, so the factory's own `ep.BaseUrl` is never used from the Gateway. Today the
two are the same constant, so nothing differs. If `ResolveWingman` ever returns a different address for some mode, the
Mail Desk would silently keep the old one. Returning null when the key is absent would keep the factory's own choice;
whether that matters is the building seat's call.
