# Answers to the review of pull request 4114

The review is `review-4114.md` in this folder. Head reviewed: 386447adf. Answers are against the head
rebased onto origin/main.

## Finding 1 (low): on Linux, `process_started_at` can raise `FileNotFoundError`

**Accepted and fixed.** The Linux reader is now its own function, `_linux_process_started_at(pid,
proc_root)`. It reads `<proc_root>/<pid>/stat` in one call, and a `FileNotFoundError` answers None,
which means "not running". The earlier check-then-read on `/proc/<pid>/stat` is gone, so there is no
window left between the two. `process_started_at` picks the Linux reader when `/proc/stat` exists and
the `ps` reader otherwise, as before.

Tests in `test_license_usage.py`:

- `test_linux_process_started_at_TheProcessExitedBeforeItsStatWasRead_IsNotRunning` - a proc folder
  with no folder for the process answers None. With the `try` taken out, this test fails with
  `FileNotFoundError`; with it back, it passes.
- `test_linux_process_started_at_ACommandNameWithSpacesAndParentheses_ReadsField22` - field 22 is read
  after the last closing parenthesis. It needs `os.sysconf`, so it is skipped on Windows. I ran the same
  function on Linux (Ubuntu under the Windows Subsystem for Linux) against a fake proc folder, against
  the real `/proc` for its own process, and for a process number that does not exist. The answers were
  the expected time, a start time within a second of now, and None.

All license Python tests: 441 passed, 1 skipped (the Linux-only test above), on Windows.

## Notes for the record

The scope notes (a reader of the credentials file that does not register under `sessions/`, and a
session starting between the registry check and the write) are agreed as written. No change.
