# Remote terminal-route byte echo check

Run on 24 October 2026 in the visible `Terminal Parity - Worker - 4420 run` RawCli session, from this pull request's worktree.

The delivery qualification rig started its byte-echo agent in a real Director terminal. It sent Down once through the shipped desktop terminal control and once through the remote `POST /keys` dispatch. It then sent the same generated paste once through the desktop paste control and once through `POST /paste` dispatch.

The committed [hex-dump JSON](terminal-parity-4382-remote-route-echo-2026-10-24.json) records both routes' bytes. They are identical:

| Input | Desktop hex | Remote dispatch hex | Result |
| --- | --- | --- | --- |
| Down key | `1B5B42` | `1B5B42` | identical |
| Bracketed paste | `1B5B3230307E72656D6F746520726F7574652070617374652051453534453244354438371B5B3230317E` | `1B5B3230307E72656D6F746520726F7574652070617374652051453534453244354438371B5B3230317E` | identical |

The rig exits nonzero when either pair differs. This run exited zero and printed `PASS: desktop and remote bytes are identical.`
