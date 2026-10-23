# Phase 1 test Director review

## Scope

Read the pull request description, the changed mission record, the correction to `phase1-hosted-desktop-list.md`, all five rig scripts, and the mission sections 5 through 8 from the main branch. Checked `CcStorage.HostedDesktopsFile()` and `InstanceContext.InstanceHome` on the main branch. Inspected the two committed desktop images and the stop proof. Searched all 20 changed files for secret, key, signing key, bearer token, and password material. The search found generated-key code and dummy database credentials, but no committed secret, key, or signing key.

Ran zero tests and zero scripts. No Director was started. The local Gateway, the owner's installed Director and data, and the machine's actual local tool directory could not be reached for this review; committed proof is the available evidence for those items.

## Findings

1. `docs/missions/factory-boxes-2026-10-19/test-director-on-devlinux/rig/stop-director.sh:10,20` — The stop script finds processes by executable path and never records or checks the process identifier launched by `director.sh`. If the test Director is stopped and another process is later started from the same build path, the stop script can write the shutdown request for that later process while claiming it is stopping the test Director. This can stop a Director that this run did not start. The request is tied to the scratch root, but the script does not prove that the process using that root is the process from its own start.

2. `docs/missions/factory-boxes-2026-10-19/test-director-on-devlinux.md:231` — The documented comparison, `ls -la ~/.local/bin | diff - <the listing taken before the first start>`, is not a runnable command: the angle-bracket phrase is an explanatory placeholder, not a saved listing path or valid comparison input. A phase 2 Developer following the instructions cannot perform the promised before-and-after check, so a change to another entry in the real tool directory could go unnoticed. The single `readlink` command on the preceding line checks only one link.

## Verdict

Changes required: make the stop script identify and signal only the process started for this test, and give the full tool-directory check a runnable comparison against the saved before-state.
