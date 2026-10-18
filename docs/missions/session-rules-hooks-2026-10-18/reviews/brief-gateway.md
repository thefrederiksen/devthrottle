Read `brief-template.md` beside this file and follow it. For this review:

- **PIECE:** Developer A, the Gateway - `kind`, `appliesTo` and the hook fields on rules, the firing
  columns, the one migration for Sqlite AND Postgres, the kind-aware write gate (P4, P7), hook rules kept
  out of the screen evaluator (P8), the effective rules route (W1), the hook firings route (W5), and
  `GitRemoteIdentity` (P6) in Contracts.
- **Questions for this piece:** does every write path to `session_rules` and the firings table (store,
  routes, and the `SaveChanges` gate) refuse what P4 and P7 refuse - can a hook rule be made live, a
  repo-scoped screen rule be stored, a hook rule carry the September `scope` filter? Does the migration
  turn every existing row into `screen` and `everywhere` on BOTH providers without touching what the
  September rules already do, and is it additive (phase 2 adds to this store in parallel)? Is W1 right for
  every `appliesTo` kind including `exceptMachines`, case-insensitive machines, an empty repo, and does it
  leak another tenant's rules? Can W5 record a firing against another tenant's rule, a screen rule, or
  twice for one `decisionId`; what does it do with a malformed or oversized batch? Can a hook rule ever
  reach the screen evaluator or its model call? Do the old screen-rule create bodies still work unchanged?
  Who can call W1 and W5 - a session key must not.
- **Write the review to:**
  `/home/soren/ReposFred/devthrottle-session-rules-p1/docs/missions/session-rules-hooks-2026-10-18/reviews/phase-1-gateway-review.md`
