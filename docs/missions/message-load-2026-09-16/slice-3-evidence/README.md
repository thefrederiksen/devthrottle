# Slice 3 evidence - replies without blocking

Two files, one per side. Each row is one guard broken on purpose: the named change was made to the
product file, the named suites were run, the failing tests were recorded, and the file was restored
with `git checkout`. A row counts only if tests FAILED. A mutation that did not compile proves nothing,
so the three that first failed to compile were rewritten to compile and run again; only the compiling
runs are kept.

- `gateway-guards-watched-failing.json` - 26 breaks, all red. The suites are
  `CcDirector.Gateway.UnitTests` (filter `Messaging` and `SessionKeyGuard`) and
  `CcDirector.Gateway.Tests` (filter `FleetMessageRouteTests`, a real Gateway host with a recording
  tunnel Director).
- `cli-guards-watched-failing.json` - 20 breaks, all red. The suite is `tools/cc-devthrottle`
  (`test_message_queue.py`, `test_help_and_errors_axi.py`, `test_axi_step_6c_help_and_errors.py`).

A test name appears once per row even when a theory failed on several cases, so the counts are counts
of distinct test methods.
