# Factory Boxes phase 1 relay review

## SCOPE

Read the Factory Boxes mission document at `origin/main`, including section 5 item 1 and section 7; the path-prefix spike and its relay requirements; pull request 4280 and the full branch diff against `origin/main`; the repository `CLAUDE.md` and `docs/CodingStyle.md`; the relay-leg record; and `wiring-after-list-merges.patch`.

Traced the browser ticket and desktop-host paths, cookie exchange and box scoping, tenant and Director ownership of tunnel streams, Director-side hosted-desktop lookup and loopback address construction, HTTP headers and body streaming, WebSocket framing and close handling, flow control, rate limiting, logging, and limits.

Ran `CcDirector.Gateway.UnitTests` filtered to `FullyQualifiedName~Boxes`: 118 passed, 0 failed, 0 skipped. An initial attempt without package restore did not execute tests because the assets file was absent; the restored run completed successfully.

Could not reach the live Gateway from outside the house network or independently repeat the hosted end-to-end browser run. I read the committed record and evidence descriptions but did not rerun that environment.

## Findings

None. The reviewed code and wiring patch did not prove a defect causing harm within this scope.

## Verdict

No proven findings within this review scope.
