# cc-director Architecture Documentation

Authoritative architecture documentation for `cc-director`, organized by topic. Modelled on the mindzieWeb `Documentation/Architecture/` convention - one subdirectory per topic, each with its own `README.md`.

---

## How to navigate

| Topic | Subdirectory | Status | Covers |
|---|---|---|---|
| Gateway / Director split | [`gateway/`](gateway/) | CURRENT + PLANNED | Where the Director ends and the Gateway begins (CURRENT). Phase 1 PLANNED: thin Gateway = directory page + Director discovery; Director is canonical. Cross-machine, aggregated views, fan-out, and event push are deferred to later phases. |

More topics will land here as we extract them from the loose docs in `docs/`.

---

## Document status convention

| Status | Meaning |
|---|---|
| `CURRENT` | Describes code that exists today. Claims are verifiable against code. |
| `PLANNED` | Describes intended design. Not yet implemented. |
| `HYBRID` | Mixed. Each section labels its own status. |

Every `CURRENT` doc carries a "Verified against" date and pins a commit / working-tree snapshot.

---

## Rendering diagrams

D2 (https://d2lang.com/) is the source-of-truth format. Source `.d2` files and the rendered `.png` are both committed.

**Tool location:** `D:\Tools\d2\d2.exe`

**Re-render after editing a `.d2` source:**

```powershell
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway/gateway-director-overview.d2        gateway/gateway-director-overview.png
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway/gateway-director-detail.d2          gateway/gateway-director-detail.png
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway/gateway-director-target-overview.d2 gateway/gateway-director-target-overview.png
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway/gateway-director-target-detail.d2   gateway/gateway-director-target-detail.png
```

---

## Conventions

### Subdirectory structure

Each topic gets its own subdirectory:
- `README.md` index in the subdir
- Markdown docs (UPPER_SNAKE_CASE.md)
- D2 source + rendered PNG (`lower-kebab-case.d2` / `.png`)

### Document skeleton

```markdown
# Title

**Status:** CURRENT | PLANNED | HYBRID
**Date:** YYYY-MM-DD
**Verified against:** commit abc1234 / YYYY-MM-DD   <- CURRENT docs only
**Audience:** Who this doc is for

## Related documents
(cross-references, with relative paths)

## Sections (numbered)
...

## Document History
| Date | Author | Change |
```

### No line numbers

Reference code by **method, class, or symbol name** - never by line number. Line numbers drift on every refactor.

### Pre-handoff diagram check

Render every `.d2` to PNG and **look at the PNG** before handing back. Reject if boxes are too small to read, arrows crisscross, labels overlap, or a fresh reader can't trace the main story. Iterate on the `.d2` until the diagram is presentable.
