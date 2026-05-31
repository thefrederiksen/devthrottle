# Gateway / Director

How the two `cc-director` processes split responsibility today, and where they are going.

| Document | Status | Covers |
|---|---|---|
| [GATEWAY_DIRECTOR_ARCHITECTURE.md](GATEWAY_DIRECTOR_ARCHITECTURE.md) | CURRENT | Today's two processes, file-watch discovery, division of labor, end-to-end flows, state ownership, known limitations |
| [GATEWAY_DIRECTOR_TARGET.md](GATEWAY_DIRECTOR_TARGET.md) | PLANNED (Phase 1) | Thin Gateway = directory page + Director discovery (register / heartbeat). Director is canonical. Browser deeplinks from Gateway to Director. Aggregated views / event hub / fan-out are deferred to later phases. |
| [GATEWAY_DIRECTOR_RESPONSIBILITIES.md](GATEWAY_DIRECTOR_RESPONSIBILITIES.md) | PLANNED | The feature-by-feature decision matrix with Phase 1 home + Phase 2+ sketch columns. Use when deciding where a new feature lives. |
| [GATEWAY_KEY_VAULT.md](GATEWAY_KEY_VAULT.md) | PLANNED | Central API-key vault on the Gateway (OpenAI, Anthropic, ...). Directors pull keys on demand over the tailnet instead of each holding their own. |
| [SETTINGS_OWNERSHIP.md](SETTINGS_OWNERSHIP.md) | PLANNED | The two-tier framework: which settings are Gateway-central (fleet-wide) vs Director-local (machine/OS-specific, e.g. screenshots dir). Framework only; per-setting classification deferred. |
| `gateway-director-overview.d2` / `.png` | CURRENT | One-glance topology diagram of today |
| `gateway-director-detail.d2` / `.png` | CURRENT | Today's topology with every component's feature list |
| `gateway-director-target-overview.d2` / `.png` | PLANNED (Phase 1) | One-glance topology of the Phase 1 target (thin Gateway, canonical Director) |
| `gateway-director-target-detail.d2` / `.png` | PLANNED (Phase 1) | Phase 1 target with feature lists; `[NEW]`, `[CHANGED]`, `[REMOVED]` tags identify the diff from CURRENT |

**Read order for someone new:**
1. `gateway-director-overview.png` - see today
2. `GATEWAY_DIRECTOR_ARCHITECTURE.md` - read today's full doc
3. `gateway-director-target-overview.png` - see where we are going
4. `GATEWAY_DIRECTOR_TARGET.md` - read the target doc

## See also

- `../../CC_Gateway_Design.md` - the original design / intent doc (predates Recap + rename)
- `../../Gateway_Dashboard.md` - operator-facing dashboard notes
- `../../HowTerminalsWork.md` - ConPty and terminal internals underneath each Session
- `../../CcDirector.Engine-Design.md` - the in-process Engine co-hosted in each Director
