# The Factory Manager is retired; the Fleet Manager sits above everything

Proof run by `rig/shoot.py` against a throwaway Gateway built from this tree, with auth on, the stand-in Director, and
the Cockpit's dev server. It writes three things straight into the rig's tables because the product no longer makes
them: the problem, the old mark, and a factory with no boss (registration now refuses one).

| File | What it shows |
| --- | --- |
| `1-factories-tab-with-no-factory-manager.png` | Three tabs and no card, for an account that still holds the old mark |
| `2-factory-with-no-boss-handle-goes-to-the-fleet-manager.png` | The factory page says where the problem goes |
| `3-with-no-fleet-manager-it-says-why.png` | No Fleet Manager and no computer for one: the service's sentence, nothing kept |
| `4-handle-the-issue-reached-the-fleet-manager.png` | The press opened the Fleet Manager, with the event it was sent and the command line |
| `problem-event.json` | The one event, after two presses |
| `command-line-transcript.txt` | Run as the Fleet Manager: `fleet events`, `factory name-check`, `factory manager show` (gone), `run resolve` |
| `gateway-problems-after.json` | The resolver is recorded as the Fleet Manager |
| `old-mark-rows-after.json` | The four stored rows unchanged, and both retired routes answering 404 |

What this rig does not prove: the press STARTING a Fleet Manager on a real Director. The stand-in offers no computer the
placement service accepts, so here the start refuses. That branch is held by
`Handle_AFactoryWithNoBoss_WithNoFleetManagerRunning_StartsOneThenTellsIt`, against a faked start.

The older folders under `docs/qa/factory-control/` are the record of what was built and stay as they were.
