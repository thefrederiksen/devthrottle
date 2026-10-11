# Review: PR 3869 live exchange proof

## Scope

Read the PR diff for `missions/license-switching/live_exchange_proof.py`, `proof/README.md`, and both committed outputs; read sections 5 and 7 of `MISSION.md`; and inspected the existing `license_store.switch` / `license_ops.switch_account` path to understand exchange recovery. I did not run the proof or tests, as requested. Findings below are from static inspection; the committed outputs show the normal successful path only.

## Findings

### High - reset timestamps do not prove which account answered

In `live_exchange_proof.py:139-152,217-230`, an event is classified as post-exchange according to when `on_line` handles it, and account change is inferred by comparing only the first pre-event and last post-event reset-time pairs. The event's status and utilization are recorded, but no account identity is checked. A single account's reset timestamp can change when its window rolls over; missing reset fields also compare unequal to present fields. In either case, the comparison can say "different account" while the event came from the original account. The later `active_account()` check proves only that the config folder now names `--to`; it does not prove which key the running Claude process used for that request.

A completely empty post-event list does fail the explicit `bool(post)` check. However, events are timestamped by callback handling rather than correlated with a request made after the switch, so a delayed pre-switch event can be classified as post-switch. The current check therefore does not establish the mission's required claim that the running process's next call was served by the other account.

### High - rollback is neither guaranteed nor part of the pass result

`live_exchange_proof.py:209-215` computes and prints the verdict before attempting to switch back. It ignores the boolean returned by `switch(before, ...)`, prints "restored" after merely reading the current account, and returns the earlier verdict without checking that the account equals `before`. Thus a successful proof followed by a failed switch-back can still exit 0 with `PROOF PASSED`, leaving the isolated default folder on the target account.

The switch-back is also not in a `finally` block. An exception or interruption after the first exchange, or a task that never exits, skips it. For an ordinary returned first-switch failure, the existing store attempts journal recovery before the next switch, and switching to an account that is already active is a no-op; that makes the attempted recovery reasonable in that case. It does not make cleanup guaranteed: an unresolved recovery or failed switch-back is not checked by this script. The code does not itself copy key material, but it cannot substantiate the README's unconditional "left as it was" claim.

### High - the helper does not enforce isolated config folders

`live_exchange_proof.py:255-261` rejects the resolved `Path.home() / ".claude"` directory, including ordinary symlink aliases. I found no direct source path that circumvents that specific check. But this is a denylist for one path, not a check that the supplied folders are isolated: any other existing config directory is accepted and passed to `claude auth status` and `license switch`, and `--store-dir` is also passed through without an isolation check. A real custom Claude profile supplied as `--default-config-dir` can therefore have its active key exchanged. The committed outputs do not prove which folders were used because their paths are omitted.

### Medium - no subprocess has a deadline

`license()` and `active_account()` use `subprocess.run` without a timeout; `run_task()` blocks reading the Claude process's stdout and then calls `wait()` without a timeout (`:82-100,114-128`). A hung Claude request, tool call, or license command can leave the proof running indefinitely. If the first exchange already happened, the cleanup block is never reached.

### Medium - redaction does not guarantee that emails or paths stay out

`Redactor` masks only aliases explicitly supplied, the exact hostname (and lowercase spelling), and exact variants of `Path.home()` (`:48-63`). `--alias` is optional and is not checked for `--to`, the current account, or every account printed by `license list` (`:255-258`). Arbitrary email addresses in child output can remain. It also does not redact paths as a class: for example, the `--tool-src` validation error prints the resolved source path directly, and child-process errors may contain store/config paths. Replacing the home prefix with `~` still leaves the remainder of a path.

The committed outputs contain no literal owner email, hostname, home directory, or filesystem path that I could find; their account addresses use `example.com` and their machine labels are neutral. They do publish account activity data, including exact reset times, utilization percentages (81%, 99%, and 100%), and a rejected-limit status. Those values are non-identifying in these files but are still subscription usage telemetry.

## Verdict

The two committed runs show successful exchanges and normal switch-back on Windows and macOS. They do not close the false-positive, failure-cleanup, isolation, timeout, or redaction gaps above. The live proof does not yet establish the mission's full check reliably.
