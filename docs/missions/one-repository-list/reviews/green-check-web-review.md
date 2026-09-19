# Web green-check review

Reviewed 19 September 2026. Verdict: **no change-blocking findings within the scope below.**
The central baseline claim is independently reproduced. These changes repair the test environment;
the green result was not obtained by removing tests or loosening assertions.

## Scope

Reviewed branch `mission/one-repo-list-green-check` at
`b534bd67101bca6449301c1fdad55f667067004a` against freshly fetched `origin/main`,
`74485174fb6e0bcf846322997b158a555edeba6f`. Read the complete seven-file difference, the mission
mandate, DevThrottle Method's Reviewer section and laws, repository root instructions, and
`docs/CodingStyle.md`. Also read the production Cockpit Vite configuration, workspace scripts,
continuous integration web job, enrollment test and helpers, About tests and component, colour
legend reader/component/tests, roster attention test, and the installed Vitest environment code.

No implementation file was edited. The only branch addition from this review is this record.
The scope is this preparatory web-test repair, not the six repository-catalogue implementation phases.

## Independently run evidence

Ran `npm ci` first in the review worktree. Created a separate detached checkout at the full baseline
commit in `/tmp/green-review-main-650e0f4c`, then ran `npm ci` there. Both installations exited zero.
Both checkouts had clean tracked state after the runs and before this record was written.

The baseline Node 22 run was completed and checked before accepting the author's baseline claim.
Each row below runs `npm test --workspaces --if-present` to completion, with the same installed
dependencies for the two runtime runs in each checkout. Node 22 was selected with
`npx --yes --package=node@22.20.0 -c 'node --version; npm test --workspaces --if-present'`.
The machine's ordinary `node --version` reported `v26.5.0`.

| Tree | Node | client-core | desktop companion | Cockpit | mobile | Exit |
|---|---|---|---|---|---|---|
| Pristine baseline | 22.20.0 | 1,453 passed | 106 passed | 457 passed | 101 passed | 0 |
| Pristine baseline | 26.5.0 | 1,430 passed, 23 failed | 106 passed | 419 passed, 38 failed | 65 passed, 36 failed | 1 |
| Reviewed branch | 26.5.0 | 1,453 passed | 106 passed | 457 passed | 101 passed | 0 |
| Reviewed branch | 22.20.0 | 1,453 passed | 106 passed | 457 passed | 101 passed | 0 |

Every green row executed 205 test files and 2,117 tests, with no skipped tests reported. The baseline
Node 26 run also reported one unhandled Cockpit error and two mobile errors. The failed-test total
is exactly 97. The errors and failing assertions show the reported missing storage, rejected signal,
and enrollment-storage premises. This establishes an actual failing control, not just two green runs.

`npm run typecheck` on the reviewed branch under Node 26 exited zero across all four workspaces.
`git diff --check` exited zero.

Local complete transcripts are `/tmp/green-review-main22.log`, `/tmp/green-review-main26.log`,
`/tmp/green-review-head22.log`, `/tmp/green-review-head26.log`, and
`/tmp/green-review-typecheck.log`. These are local supporting material, not committed artifacts;
the counts and outcomes above are this review's durable record.

One correction to the brief: `.github/workflows/ci.yml` selects Node **22**, not the exact patch
22.20.0. Its web job explicitly runs the three browser workspaces and separately builds both shells.
I reproduced the requested exact runtime locally; I did not inspect historical hosted job executions.

## The three environment changes

### Storage

The installed Vitest `getWindowKeys` skips existing globals unless they belong to its explicit key
list. Storage is not on that list. The worker preload removes the interfering Node globals before
Vitest constructs the environment, allowing its existing jsdom installation path to work.

A separate process probe using the actual preload and Vitest's actual jsdom environment confirmed:
Node 26 starts with both globals present; the preload removes both; the resulting localStorage is
an instance of jsdom's Storage, equals window.localStorage, and successfully round-trips a value.
On Node 22.20.0 both globals were absent before and after deletion, and the same Storage probe passed.

Deletion affects all tests in those fork workers, including node-environment tests. It does not
change the parent process or a shipped browser. Those node tests consequently cannot be used as
proof of Node's native Web Storage behavior. That is acceptable for these browser workspaces;
the desktop companion workspace does not load this configuration. No product path imports the preload.

### Abort signals

Using the installed Vitest jsdom setup on Node 26, `new Request(url, { signal: new
AbortController().signal })` reproduced the exact brand-check rejection before loading the new
setup file. Loading the actual setup file made the Request succeed. Aborting the controller also
set `request.signal.aborted` to true. The acceptance and cancellation probe passed on Node 22 too.

This aligns the controller with the Node Request/fetch implementation actually used by these tests.
A browser supplies its own matching implementations; this change does not ship a Node controller to
it. The original router tests execute unchanged and now complete their expected redirects.

There is a real boundary to this repair: jsdom's DOM event listeners still require jsdom signals.
The probe `document.addEventListener('review', () => {}, { signal: new AbortController().signal })`
throws after this setup. The installed jsdom options converter confirms that brand check. I found
no current product event-listener registration supplying a signal, and all current suites passed.
This is a documented limitation, not a demonstrated regression in an existing caller. The green
run must not be described as proof of complete browser equivalence or of abortable DOM listeners.

### Cockpit build constants

The fixed strings are confined to `vitest.config.ts`. The unchanged production `vite.config.ts`
still derives the commit from the configured build input or git and derives the build time at build
execution; the same values feed its defines and `build.json` plugin. The production build script
still invokes `vite build`, not the test configuration.

About tests assert Gateway-returned stamps and formatting; they do not verify the real identity of
the locally built bundle. Fixed test inputs preserve those assertions. Separating the configuration
means a green unit run no longer exercises production configuration loading, so it cannot certify
a real build stamp or a working production bundle. Continuous integration retains separate shell
build steps. I did not run a production build, in keeping with this seat's instruction never to build.
There is no observed production defect caused by these constants.

## Standard error messages and the deferred legend work

Counted literal message occurrences in the complete independent transcripts:

| Message | Baseline Node 26 | Baseline Node 22 | Branch Node 26 | Branch Node 22 |
|---|---:|---:|---:|---:|
| Missing gatewayFetch export | 38 | 40 | 40 | 40 |
| Missing list of colours | 4 | 4 | 4 | 4 |
| window.scrollTo not implemented | 9 | 9 | 9 | 9 |
| canvas getContext not implemented | 2 | 2 | 2 | 2 |

The original 38/4/9/2 count is reproduced. The green runs show that these diagnostic categories
are not failed-test counts; the missing-export count is actually 40 once all tests execute normally.
For example, baseline Node 26 explicitly reports the attention-order file's five tests and the
session-tree file's eight tests passing despite their legend diagnostics. Canvas diagnostics also
occur in routing files, so a file containing that diagnostic can separately contain a real routing
failure; the diagnostic itself is not that failure. All four categories remain in exit-zero runs.

The stale roster mocks are a genuine coverage limit. The attention-order legend test only asserts
that the dialog opens; it does not await or assert any returned colours. Its green result therefore
proves the opening interaction, not successful legend loading. The shared legend tests separately
assert the Gateway's words and swatches and the failed-read behavior, and the Fleet Map test supplies
a legend response. I agree with leaving the roster mock repair outside this change, as explicitly
accepted by the owner. It must not be represented as successful roster legend-content coverage.

## Weakening, scope, and findings

There are **no actionable findings requiring an implementation change** from this review.

The complete difference adds no skip, exclusion, error suppression, or relaxed assertion. The sole
changed test still requires a null result for unavailable storage; it now supplies a store that throws
SecurityError instead of assuming Node has no store. Existing successful-storage tests remain.
As the author acknowledges, this particular test edit is not necessary for the aggregate green result:
the preload also makes its old absence-dependent form pass. It is a more explicit refusal premise,
not independent proof that the preload works. The failing baseline and direct environment probes
supply the latter evidence.

No changed product client derives a verdict or repository order, and no fallback was added to product
behavior. The setup fails explicitly if the preload is missing. This is within the explicitly assigned
preparatory repair of the mission's required web check, not an unrequested catalogue feature.

Limits: no production builds, live-browser or Gateway interaction, Windows/Linux execution, other
Node versions, hosted job-history verification, or .NET suites were run. This review does not certify
the full mission check, production build identity, or completion of the repository-list mission.
