# Cockpit

The Cockpit is the planned single UI for driving every Claude session on the tailnet: a Blazor Server desktop app that reaches dumb, long-lived Director "runners" over the network so sessions never have to be killed.

| Document | Status | Covers |
|---|---|---|
| [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) | PLANNED (v1) | The idea, the driver, v1 scope, the three connection lanes, where the smarts run, project shape, hosting fork, build order |
| `cockpit-topology.d2` / `.png` | PLANNED (v1) | Three-lane topology: terminal (browser->Director direct), discovery+reads (server->Gateway), writes (server->Director direct) |

**Read order for someone new:**
1. `cockpit-topology.png` - see the three lanes
2. `COCKPIT_DESIGN.md` - read the design

## See also

- `../gateway/` - the Gateway/Director split the Cockpit builds on (and the shipped fleet-wide `GET /sessions` it consumes)
- `../wingman/SESSION_VIEW_MERGE_PLAN.md` - the wingman/agent-feed view the Cockpit replaces
- `playground/wingman-briefing/` - the working prototype (cockpit.html + server.py + PLAN-v1-cockpit.md)

## Re-rendering the diagram

```powershell
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk cockpit-topology.d2 cockpit-topology.png
```
