# Evidence: the deploy refuses to deploy onto a production site that is already down (issue #4515)

## What was built

- `scripts/check-production-healthy.sh` probes `<base url>/healthz`. It passes only on a presence:
  HTTP 200, a JSON body, and a non-empty `commit`. No connection, a 502, no answer inside the
  per-request timeout (10 seconds, three attempts, two seconds apart - about 36 seconds at worst),
  an empty body, a body that is not JSON, or JSON with no commit all refuse, with a message that says
  production is already unhealthy, names what `/healthz` returned, and points at
  `rollback-hosted-gateway.yml`.
- `.github/workflows/deploy-hosted-gateway.yml` runs it in the step "Refuse to deploy onto a
  production site that is already unhealthy", after the built image is proved and BEFORE the outgoing
  commit and image are read and before anything changes production.
- The escape hatch is the `workflow_dispatch` boolean input `deploy_to_unhealthy_production`
  (default false). It is handed to the script, not put on the step as an `if:`, so the script still
  probes and writes a loud warning into the run summary naming what `/healthz` returned.
- `src/CcDirector.Core.UnitTests/Release/ProductionHealthGateRunsBeforeEveryDeployTests.cs` guards the
  step's presence, its place before the outgoing reads and the image pin, the absence of any `if:` or
  `continue-on-error:`, the input's type and default, and the script's pass condition and summary text.

## What this proof does NOT cover

Production cannot be made unhealthy on purpose and the deploy workflow must not be dispatched for this
change, so there is no proving run on GitHub. The script was proved against a local fake `/healthz`
(below) and, read-only, against the live production `/healthz`. The first real deploy after merge is
the first time the step runs on a runner.

## The probe against a local fake /healthz

`bash scripts/check-production-healthy.test.sh` - one fake path per shape, each case checks the exit
code AND the words printed, and the run summary where one is written. Run on devlinux, 2026-10-26,
with a 2 second timeout so the hang cases finish quickly. Ports replaced by `<port>`.

```
PASS  healthy (exit 0, 1s)
        Attempt 1/2: http://127.0.0.1:<port>/healthy/healthz -> HTTP 200, commit abc1234
        Production health gate passed: production is healthy on its own commit abc1234.
PASS  healthy writes commit=abc1234 to the step output
PASS  bad-gateway (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/bad-gateway/healthz -> HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway
        Attempt 2/2: http://127.0.0.1:<port>/bad-gateway/healthz -> HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/bad-gateway/healthz returned: HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/bad-gateway/healthz returned: **HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  refused (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/healthz -> no HTTP response (connection failed, curl exit 7; HTTP 000)
        Attempt 2/2: http://127.0.0.1:<port>/healthz -> no HTTP response (connection failed, curl exit 7; HTTP 000)
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/healthz returned: no HTTP response (connection failed, curl exit 7; HTTP 000) (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/healthz returned: **no HTTP response (connection failed, curl exit 7; HTTP 000)** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  hang (exit 1, 4s)
        Attempt 1/2: http://127.0.0.1:<port>/hang/healthz -> no answer within 2s (timed out; HTTP 000)
        Attempt 2/2: http://127.0.0.1:<port>/hang/healthz -> no answer within 2s (timed out; HTTP 000)
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/hang/healthz returned: no answer within 2s (timed out; HTTP 000) (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/hang/healthz returned: **no answer within 2s (timed out; HTTP 000)** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  empty-body (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/empty/healthz -> HTTP 200 with an empty body
        Attempt 2/2: http://127.0.0.1:<port>/empty/healthz -> HTTP 200 with an empty body
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/empty/healthz returned: HTTP 200 with an empty body (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/empty/healthz returned: **HTTP 200 with an empty body** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  no-commit (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/no-commit/healthz -> HTTP 200 but the body carries no commit: {"status":"ok"}
        Attempt 2/2: http://127.0.0.1:<port>/no-commit/healthz -> HTTP 200 but the body carries no commit: {"status":"ok"}
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/no-commit/healthz returned: HTTP 200 but the body carries no commit: {"status":"ok"} (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/no-commit/healthz returned: **HTTP 200 but the body carries no commit: {"status":"ok"}** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  empty-commit (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/empty-commit/healthz -> HTTP 200 but the body carries no commit: {"status":"ok","commit":""}
        Attempt 2/2: http://127.0.0.1:<port>/empty-commit/healthz -> HTTP 200 but the body carries no commit: {"status":"ok","commit":""}
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/empty-commit/healthz returned: HTTP 200 but the body carries no commit: {"status":"ok","commit":""} (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/empty-commit/healthz returned: **HTTP 200 but the body carries no commit: {"status":"ok","commit":""}** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  not-json (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/not-json/healthz -> HTTP 200 but the body is not JSON: <html>Service Unavailable</html>
        Attempt 2/2: http://127.0.0.1:<port>/not-json/healthz -> HTTP 200 but the body is not JSON: <html>Service Unavailable</html>
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/not-json/healthz returned: HTTP 200 but the body is not JSON: <html>Service Unavailable</html> (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/not-json/healthz returned: **HTTP 200 but the body is not JSON: <html>Service Unavailable</html>** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  refusal-words (exit 1, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/bad-gateway/healthz -> HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway
        Attempt 2/2: http://127.0.0.1:<port>/bad-gateway/healthz -> HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/bad-gateway/healthz returned: HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway (after 2 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/bad-gateway/healthz returned: **HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway** (after 2 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.
PASS  escape-hatch (exit 0, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/bad-gateway/healthz -> HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway
        Attempt 2/2: http://127.0.0.1:<port>/bad-gateway/healthz -> HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway
        WARNING: PRODUCTION IS ALREADY UNHEALTHY, AND THIS DEPLOY IS GOING AHEAD ANYWAY.
                 deploy_to_unhealthy_production=true was set on this run. /healthz returned: HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway.
                 The outgoing commit and image this run records are NOT a trustworthy rollback target.
        -- run summary --
        ### WARNING: deployed onto UNHEALTHY production - the production health gate was SKIPPED
        This run was started with **deploy_to_unhealthy_production=true**. Before the deploy, http://127.0.0.1:<port>/bad-gateway/healthz returned: **HTTP 502, body: 502 - Web server received an invalid response while acting as a gateway**.
        The outgoing commit and image recorded by this run are NOT a trustworthy rollback target; roll back to last-known-good if this deploy does not recover production.
PASS  escape-hatch summary names what /healthz returned
PASS  escape-hatch-healthy (exit 0, 0s)
        Attempt 1/2: http://127.0.0.1:<port>/healthy/healthz -> HTTP 200, commit abc1234
        Production health gate passed: production is healthy on its own commit abc1234.
        NOTE: deploy_to_unhealthy_production was set, but production is healthy, so it changed nothing.
        -- run summary --
        ### Production health gate: passed (deploy_to_unhealthy_production was set but not needed)
        Production was healthy before the deploy: HTTP 200, commit abc1234.
PASS  hang-is-bounded (exit 1, 6s)
        Attempt 1/3: http://127.0.0.1:<port>/hang/healthz -> no answer within 2s (timed out; HTTP 000)
        Attempt 2/3: http://127.0.0.1:<port>/hang/healthz -> no answer within 2s (timed out; HTTP 000)
        Attempt 3/3: http://127.0.0.1:<port>/hang/healthz -> no answer within 2s (timed out; HTTP 000)
        REFUSED: production is already unhealthy, so nothing will be deployed on top of it.
          http://127.0.0.1:<port>/hang/healthz returned: no answer within 2s (timed out; HTTP 000) (after 3 attempts, 2s each).
          The next action is recovery, not a deploy: run the rollback workflow, for example
            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good
          If this deploy IS the fix for the broken production, start it again with
            -f deploy_to_unhealthy_production=true
        -- run summary --
        ### Deploy REFUSED: production is already unhealthy
        Before anything was changed, http://127.0.0.1:<port>/hang/healthz returned: **no answer within 2s (timed out; HTTP 000)** (after 3 attempts, 2s each).
        Recover first with the rollback workflow: `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`.
        If this deploy is itself the fix, start it again with `-f deploy_to_unhealthy_production=true`.

ALL CASES PASSED
```

## The probe against live production, read-only

```
$ bash scripts/check-production-healthy.sh https://devthrottle-gw.azurewebsites.net
Attempt 1/3: https://devthrottle-gw.azurewebsites.net/healthz -> HTTP 200, commit b6d6acb
Production health gate passed: production is healthy on its own commit b6d6acb.
exit 0
```

## The guard fails when the gate is weakened

With an `if: ${{ !inputs.deploy_to_unhealthy_production }}` added to the step and the input's default
flipped to true, `ProductionHealthGateRunsBeforeEveryDeployTests` failed 2 of 3 (`Collections differ`
on the step keys, `Sub-string not found` on `default: false`). With the workflow restored, 3 of 3 pass.
