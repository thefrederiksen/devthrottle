# Cockpit

The Cockpit is the planned single UI for driving every Claude session on the tailnet: a Blazor Server desktop app that reaches dumb, long-lived Director "runners" over the network so sessions never have to be killed.

| Document | Status | Covers |
|---|---|---|
| [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) | PLANNED (v1) | The idea, the driver, v1 scope, the connection model, where the smarts run, project shape, hosting, build order |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | PLANNED | Status snapshot of the built MVP + the ordered phase plan (two parallel tracks; Phase 0 -> Phase 5 retire the desktop) |
| [HANDOVER.md](HANDOVER.md) | ACTIVE | Hand-off for a fresh session: status, the **launch gate** (verified Director REST inventory + the only gaps), and cross-machine rollout to Mac-mini + Windows-2 |
| [BUILD_CHECKLIST.md](BUILD_CHECKLIST.md) | ACTIVE | The unambiguous #3-#6 Director build checklist (endpoint + behavior + done-criteria) to implement before cutting the final build |
| `cockpit-topology.d2` / `.png` / `.svg` | PLANNED (v1) | Fleet topology: outside access layer (Gateway + Cockpit) reaching into the Tailscale network of Director runners |

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
