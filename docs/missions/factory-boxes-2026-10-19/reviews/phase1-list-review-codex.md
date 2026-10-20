# Factory Boxes phase 1 hosted desktop list review

## Scope

Reviewed pull request 4271, branch `boxes/phase1-hosted-desktop-list`, against `origin/main`. Read the Factory Boxes mission, including section 5 item 1 and section 7, the path-prefix spike's relay requirements, the pull request description and complete list of changed files, the hosted desktop contract, `CLAUDE.md`, and `docs/CodingStyle.md`. Inspected the Director registry and push path, Gateway tenant store, switch, gate, listing route, account status response, hub registration and disconnection handling, and their relevant tests. The patch does not contain the relay leg or Cockpit changes.

Ran `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj`: 2,462 passed, 6 failed, 23 skipped, 2,491 total. The six failures were Windows path or process behavior on Linux, as listed in the pull request description. The focused Gateway tests did not reach test execution: the build ran for more than two minutes and was cancelled. The initial no-restore attempt stopped because the isolated worktree had no package assets. No Director test suite was run. `git diff --check origin/main...origin/boxes/phase1-hosted-desktop-list` produced no output.

Could not reach the live Gateway from an outside network, exercise a real tunnel, or run the relay check. Those surfaces are not included in this pull request.

## Findings

1. `src/CcDirector.Gateway.UnitTests/Boxes/BoxesEndpointTests.cs:129` — The signed-out test does not test the contract's authentication refusal. Its test server installs a header-based fake identity middleware at lines 52–61 and does not install the production `AuthMiddleware`; a request without the test header therefore reaches `BoxesGate`, which returns 404. The test only asserts 404 at lines 133–134. The contract says a signed-out request receives 401 from authentication, before the flag gate can answer 404. As written, this test would still pass if the production authentication layer stopped protecting `/boxes`, so the suite does not prove that a signed-out caller is refused by authentication. Include the production authentication behavior in this test or add an integration test that observes the 401.

## Verdict

One change is required: make the signed-out test prove the authentication refusal stated in the contract.
