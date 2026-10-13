# Review: PR 3875 — License Switching phase 4 spares

## Findings

### P1 — Adoption leaves duplicate sign-in keys across a crash window

**Location:** `tools/cc-devthrottle/src/license_store.py:1168-1184` (writes at lines 734 and 801)

Each adoption path writes the new key into its destination and removes the staging copy only in a later journal step. A process or machine crash after the destination write and before the `drop` step leaves the same sign-in in both `signing-in/<...>` and the held folder. This occurs for a new spare, spare renewal, and active renewal. It violates the mission’s single-place invariant and its reason: refreshing one copy can invalidate the other. If an active session refreshes the default-folder copy before recovery, the staged copy no longer matches the journal; recovery can refuse to finish, and a spare renewal can remain journal-blocked after its old key was erased.

The crash test recovers first at `tools/cc-devthrottle/tests/test_license_store.py:1282`, then checks uniqueness at line 1284. It never asserts uniqueness in the state immediately after the simulated crash.

### P2 — Proof cleanup recursively deletes an unchecked caller path

**Location:** `missions/license-switching/phase-4/live_sign_in_proof.py:150-166`

The proof accepts `--test-root` / `--remote-root` as caller paths, then unconditionally deletes the entire path with `shutil.rmtree` or remote `rm -rf`. It does not create the root exclusively, validate its canonical location, or require a run-owned marker. A mistaken existing path can therefore delete unrelated files; cleanup also runs after failed proof checks. The saved outputs redact the absolute root, so they cannot independently establish where the live runs pointed.

### P2 — The proposed renewal error vocabulary does not match the CLI

**Location:** `missions/license-switching/contract.md:543-546`

Section 8 says `no-approver` is a reason `license renew --json` already emits. The CLI currently emits `failed-no-browser-configured`: `license_signin.py:83` defines that code, `renew_due` prefixes it at line 588, and `test_license_signin.py:294` asserts it. A future Director-to-Gateway report using the proposed closed vocabulary will have an unrecognized error unless the contract adds the current code or specifies a normalization.

## Scope and evidence

- Read `missions/license-switching/MISSION.md` section 5 (including its traps), `contract.md` including proposed section 8, and `phase-1-report.md`.
- Reviewed the PR diff against `origin/main...HEAD`, focusing on sign-in, browser approval, renewal, key-store adoption/recovery, CLI output, tests, and the phase 4 proof.
- Both committed proof outputs show operations under `<test root>/default` and `<test root>/store`; the Mac output also shows hashed Keychain service names for those custom config folders. The absolute roots are redacted. The proof source passes the supplied root into those paths, but does not validate it, so I cannot independently verify the actual absolute paths used in the live runs.
- The committed proof outputs contain redaction placeholders. I found no owner-specific email, browser profile, or host name in the changed files inspected; test fixtures use synthetic `example.com` addresses and generic labels.
- I did not run tests.
