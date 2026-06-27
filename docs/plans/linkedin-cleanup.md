# cc-linkedin Cleanup Plan

cc-linkedin CLI has been removed (issue #71), replaced by cc-browser connections + LinkedIn navigation skill. This document tracks all references that were cleaned up.

## Status: COMPLETE

## Active Code (updated to use cc-browser)

| # | File | Action | Done |
|---|------|--------|------|
| 1 | `src/CcDirector.CommunicationManager/ViewModels/MainViewModel.cs` | Replaced cc-linkedin.exe with cc-browser.exe | [x] |
| 2 | `src/CcDirector.Wpf/AddConnectionDialog.xaml` | Removed cc-linkedin ComboBoxItem | [x] |
| 3 | `scheduler/cc_director/dispatcher/linkedin_sender.py` | Rewritten to use cc-browser | [x] |
| 8 | `tools/cc-vault/src/cli.py` | Updated source labels from "cc-linkedin enrich" to "linkedin-enrich" | [x] |
| 9 | `tools/cc-director-setup/Models/InstallProfile.cs` | Removed from install list | [x] |
| 10 | `tools/cc-browser/test/unit/connections.test.mjs` | Updated tool binding test data | [x] |

## Scripts (updated)

| # | File | Action | Done |
|---|------|--------|------|
| 11 | `scripts/enrich-contacts.py` | Updated all cc-linkedin references (script is deprecated) | [x] |
| 12 | `scripts/linkedin-auto-connect.py` | Already updated in #71, no cc-linkedin calls remain | [x] |

## Files Deleted

| # | File | Reason | Done |
|---|------|--------|------|
| 13 | `cc_underscore_findings.csv` | Historical audit from underscore-to-dash rename. Stale. | [x] |

## Migration Scripts (kept as-is)

References are to the old `.cc-linkedin` config directory for storage migration. They stay as-is.

| # | File |
|---|------|
| 14 | `scripts/migrate-storage.py` |
| 15 | `scripts/backup-before-migration.py` |
| 16 | `tools/cc_storage/migrate.py` |

## Documentation (updated)

| # | File | Action | Done |
|---|------|--------|------|
| 17 | `docs/Engine-Handover.md` | Updated LinkedInSender description | [x] |
| 18 | `docs/LINKEDIN_CONTACT_ENRICHMENT_PLAN.md` | Replaced all cc-linkedin refs | [x] |
| 19 | `docs/handover-multi-platform-dispatch.md` | Replaced all cc-linkedin refs | [x] |
| 20 | `docs/handover-comm-dispatch.md` | Replaced all cc-linkedin refs and updated rules | [x] |
| 21 | `docs/plans/installer-wizard.md` | Removed cc-linkedin from dependency list | [x] |
| 22 | `tools/cc-browser/docs/PRD-browser-connections.md` | Updated tool binding examples | [x] |
| 23 | `tools/cc-reddit/test/fixtures/README.md` | Removed cc-linkedin comparison | [x] |

## Global CLAUDE.md (updated)

Updated global `~/.claude/CLAUDE.md` LinkedIn section to reference cc-browser with LinkedIn connection instead of cc-linkedin.

## Notes

- cc-linkedin was a Python CLI that wrapped cc-browser with human-like delays
- It was removed in commit fcd7992 (closes #71)
- The LinkedIn navigation skill in cc-browser now handles the same functionality
- LinkedIn post/comment/message dispatch via cc-browser requires LLM agent orchestration (multi-step browser interaction)
