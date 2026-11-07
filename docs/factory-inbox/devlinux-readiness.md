# devlinux readiness - Factory Inbox finishing run

Checked by the Delivery Lead (session 91e6276e, "Factory Inbox - Delivery Lead - finish on devlinux") on
6 November 2026, before any other step, as `MISSION-FINISH.md` asks. Every line below was run, not assumed.

| Need | Found | How it was checked |
|---|---|---|
| PowerShell runs `scripts/test-local.ps1` | Yes. PowerShell 7.4.11 at `~/.local/bin/pwsh`. | `pwsh -v`; the baseline gate run below. |
| Docker | Yes. Docker 29.9.2, the daemon answers. | `docker version`, `docker ps`. |
| The .NET software development kit pinned in `global.json` (10.0.403) | Yes, at `~/.dotnet` (also 10.0.301). Not on the default path: put `~/.dotnet` first; `/usr/bin/dotnet` is 8.0 and fails. | `dotnet --list-sdks`. |
| `node` and `npm` for the client-core, Cockpit and mobile web tests | Yes. Node 22.20.0, npm 10.9.3, at `~/.local/share/node/bin` (not on the default path). | `node -v`, `npm -v`. |
| `gh` signed in to `thefrederiksen/devthrottle`, can merge and start the deploy workflow | Yes. Signed in as thefrederiksen with the `repo` and `workflow` scopes; permission on the repository is ADMIN. | `gh auth status`; `gh repo view --json viewerPermission`. |
| `cc-dev-reports` publishes | Installed at `~/.local/bin/cc-dev-reports`. | `cc-dev-reports --help`. Publishing is proven by the step A report to the owner. |
| A browser driven with browser-harness, signed in as the owner | Yes. The Director's browser profile "CenterConsulting" (soren@centerconsulting.com) starts, browser-harness attaches, and `https://gateway.devthrottle.com/c/` opens signed in as the owner with the full left rail. | `cc-devthrottle browser start CenterConsulting`; a browser-harness screenshot. |
| A second, TEST Director beside the `devlinux` Director | Yes. The installed Director binary started from a scratch folder (`/home/soren/ReposFred/fi-test-director`, its own root AND its own home, so it cannot touch the owner's instance or relink `~/.local/bin`), detached from this terminal with `setsid`, on display `:0`. It registered as Director 81766668, connected its tunnel to `https://gateway.devthrottle.com`, and stopped at once on its own stop request (a request file holding an ISO time). `~/.local/bin/cc-devthrottle` still points at the real tools afterwards. Its display name is set to "Factory Inbox test" for step F, which builds it from main. | The instance registration file, the Director log ("GatewayStreamClient connected"), the process exit. |
| The factory area switch for the owner's account on the live Gateway | ON. | A session key may not read `/gateway/factory-agents/switch` (refused, out of scope), so it was settled by a route inside the factory gate: `GET /sessions/<this session>/factory-inbox` answered the route's own refusal ("This session is in no factory") instead of the bare 404 the gate gives when the area is off for the account. |
| Tailscale | Not used, not configured, not depended on. | Every test Gateway starts with `CC_GATEWAY_NO_TAILSCALE=1`. |

## Machine facts that change how the gate is run here

- Memory: 39 gigabytes, about 31 free. Disk: 304 gigabytes free. Inotify instances limit: 1024 (raised from 128
  earlier), so Gateway test runs are not starved of file watchers.
- `scripts/test-local.ps1` has no Linux handling. It is run here with `EnableWindowsTargeting=true` (or the build
  fails), `LOCALAPPDATA` pointed at a writable folder (or it cannot write its record), and
  `DOTNET_USE_POLLING_FILE_WATCHER=1`.
- The installed `cc-devthrottle` on devlinux is 1.3.0 and has no `factory inbox` or `inbox` commands yet. Step F runs
  the commands from main's `tools/cc-devthrottle`, or after the Director's tools update.

## Baseline gate on origin/main

Run on origin/main cbce2401f on 6 November 2026 with `pwsh -NoProfile -File scripts/test-local.ps1 -Parked`, with
`EnableWindowsTargeting=true`, `LOCALAPPDATA` set, `DOTNET_USE_POLLING_FILE_WATCHER=1` and `~/.dotnet` first on the path.
It took about 50 minutes, 31 of them in `CcDirector.Gateway.UnitTests` (17,032 tests, all passed).

The verdict on this machine is always NEVER RAN: `cc-director-setup.Tests` builds for Windows only and runs zero tests
here. Every other suite completed. Exactly 11 tests failed, and every one of them passes on GitHub's full Windows run of
the same code (CI run on 0641d53b4, success), so they fail on Linux only:

- `CcDirector.Core.Tests.Update.UpdateCheckOutcomeTests.ALinuxDownload_IsStagedRunnable`
- `CcDirector.Core.Tests.Utilities.ProcessesAreSpawnedNotForkedTests.ProcessRunner_StartsAHundredChildren_WithoutCallingForkOnce`
- `CcDirector.Gateway.Tests.FleetSpawnMissionAttachTests.Gateway_resolved_mission_attaches_directly_without_any_local_lookup`
- `CcDirector.Gateway.Tests.FleetSpawnMissionAttachTests.No_mission_on_the_create_attaches_nothing`
- `CcDirector.Gateway.Tests.FleetSpawnOriginTests.An_agent_spawn_records_who_asked_and_which_session_asked`
- `CcDirector.Gateway.Tests.FleetSpawnOriginTests.A_parent_named_on_a_human_origin_is_dropped`
- `CcDirector.Gateway.Tests.FleetSpawnOriginTests.A_spawn_that_names_no_surface_is_recorded_with_unknown_kind_and_no_parent`
- `CcDirector.Gateway.Tests.FleetSpawnOriginTests.Lineage_is_recorded_even_when_the_new_session_has_no_controller`
- `CcDirector.Gateway.Tests.HostedProcessControlDenyTests.Hosted_director_launch_is_refused`
- `CcDirector.Gateway.Tests.WorkflowSeatTests.A_gateway_resolved_seat_isStamped_andBriefsTheAgent`
- `CcDirector.Gateway.Tests.WorkflowSeatTests.An_unseated_spawn_isUnaffected_andCarriesNoSeatParagraph`

**The gate used for every pull request of this run:** the Delivery Lead's own gate worktree, trial-merged with
origin/main; every suite outcome=Completed except the Windows-only installer suite; the failed tests read from the run's
TRX files and compared with the 11 above; a pull request passes only when it adds no failure. Two tests outside this
mission failed once under load during these gates (a Pi session test and a terminal copy menu test); each passed five
times out of five alone on main, and each was handed to a Developer, who found the race in the test's own setup and
fixed it (pull requests 4667 and 4691). The command-line tool's Python suite runs with
`uvx --python ~/.local/share/cc-director/pyenv/bin/python3 pytest` and the installed tools' packages on `PYTHONPATH`
(the system Python has no pytest and no venv module).

## What changed later in the run

The test Director could not share the computer name `devlinux` with the owner's Director: the Gateway picks a factory's
Director by computer name, so the step F proof ran it in a container with its own host name, `fi-inbox-test` (see the
end-to-end report, and issue 4701).
