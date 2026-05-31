# Settings Ownership: Gateway-central vs Director-local (PLANNED)

**Status:** PLANNED (framework only - per-setting classification deferred)
**Date:** 2026-05-31
**Audience:** Anyone deciding where a given setting should live.

## Related documents

- [GATEWAY_KEY_VAULT.md](GATEWAY_KEY_VAULT.md) - the first concrete centralized case (API keys)
- [GATEWAY_DIRECTOR_ARCHITECTURE.md](GATEWAY_DIRECTOR_ARCHITECTURE.md) - today's per-Director `GET/PUT /settings`

---

## The principle

Settings split into **two tiers**:

- **Director-local** - machine / OS / user-specific; must live on each Director and be read on that box. *Example: the **screenshots directory** (an OS-dependent path that differs per machine).*
- **Gateway-centralized** - fleet-wide; set once on the Gateway and shared with every Director (pulled the same way the Key Vault hands out keys). *First instance: the **Key Vault** (API keys).*

## Rule of thumb

A setting is **Director-local** if it depends on the machine, OS, or user, or only makes sense on that one box (paths, OS folders, per-machine ports). Otherwise it's a **candidate for centralization** (fleet-wide preferences, shared endpoints, keys).

## Not classifying yet

This doc records the **two-tier framework only**. We are deliberately **not** enumerating which settings go where - classify each one when it comes up. Today `GET/PUT /settings` is per-Director; centralized settings would be served by the Gateway and pulled by Directors, reusing the Key Vault handout pattern.

## Known so far

| Tier | Examples |
|---|---|
| Director-local | screenshots directory (OS path); per-Director allocated port |
| Gateway-central | API keys (Key Vault); *others TBD* |

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-31 | claude (cc-director assistant) | Initial PLANNED: two-tier settings ownership (Gateway-central vs Director-local). Framework only; per-setting classification deferred. |
