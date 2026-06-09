# New Session dialog: Type dropdown + 4-member Product group

**Status:** Proposal (issue only - nothing built yet)
**Mockup:** [docs/plans/new-session-dialog-groups.html](new-session-dialog-groups.html) (open locally; before/after + interactive Single/Group toggle)
**Related:** #211 (session types), #225 (session groups), #236 (one-click bug session)

## Why

Two problems with today's New Session dialog:

1. The **Type** picker is a cramped radio row that only surfaces 3 of the existing
   session types ([I] Implement, [D] Discuss, [B] Bug Report). QA is not selectable for
   a single session at all.
2. The **group** concept is bolted on as a single radio ("Group: Product") on the
   Create line, and the Product group only spins up 3 sessions (Submitter, Implementer,
   QA). Real product work needs a fourth role - **Support**.

We also collapse the type list to the five we actually want.

## What changes (two linked changes)

### 1. Dialog cleanup

- Replace the `Create: (o) Single session  ( ) Group: Product` radios with a clean
  **Single session | Group** toggle.
- When **Single** is selected, show a **Type dropdown** (not radio buttons) listing the
  **five** session types (Developer, Product, QA, Support, Discuss), each with its badge
  chip + a one-line description of the role.
- When **Group** is selected, show a **Group dropdown** (only "Product" today, designed
  to grow), plus a preview card listing exactly which sessions will be created.
- Convert the **Agent** radios to a dropdown too, for visual consistency (Claude Code
  default). *Secondary - can be dropped if it widens scope.*
- Repo picker, checkboxes (Bypass / Remote Control / Wingman), and Start/Cancel are
  unchanged. The selected repo "decides the product."

### 2. The five session types

Collapse to exactly five, renaming two and adding one:

| Type (display) | Enum value | Badge | Behavior |
|----------------|------------|-------|----------|
| **Developer**  | `Implement` (renamed) | none (default) | Write code, fix bugs, ship. The default working type; no rail badge. |
| **Product**    | `BugReport` (renamed) | `[P]` magenta | Scope the work, file issues for the developer to implement. Never codes. |
| **QA**         | `QA`        | `[Q]` violet  | Verify against what was asked; report findings, never fix. |
| **Support**    | `Support` (**NEW**) | `[S]` emerald | Triage incoming questions/support, answer what it can, file issues for real bugs. Never edits code. |
| **Discuss**    | `Discuss`   | `[D]` cyan    | Talk only - never edits or commits. |

- `Implement` is renamed **Developer**, `BugReport` is renamed **Product** (display +
  enum member rename; the underlying integer values are unchanged so existing sessions
  still deserialize).
- `IssueSubmitter` is **dropped** from the picker and the Product group. Keep the enum
  value present (legacy/hidden) so any persisted IssueSubmitter session still loads, but
  it is no longer offered. Dropping it frees the **`S`** badge for Support.

### 3. Product group = 4 members

Per the user's screenshots, the Product group becomes **Product, Developer, QA, Support**:

| Role label | Session type | Member name (in repo `cc-director`) |
|------------|--------------|--------------------------------------|
| Product    | `BugReport`  | `cc-director - product` |
| Developer  | `Implement`  | `cc-director - developer` |
| QA         | `QA`         | `cc-director - qa` |
| Support    | `Support`    | `cc-director - support` |

Role labels are display-only (this is already how groups work). The session **type**
drives each member's playbook.

## New session type: Support

Add `SessionType.Support = 5` (append-only; existing values 0-4 unchanged for
serialization back-compat).

- **Playbook (triage + file, never fix):** Handle incoming user questions and support
  requests. Answer what can be answered directly. For real bugs or feature gaps, file a
  GitHub issue complete enough for a Developer session to pick up cold. **Never edits
  code.**
- **Badge:** letter **`S`** (free now that Issue Submitter is dropped), color emerald
  `#10B981`.

## Data model / code touch-points (for the implementer)

- `src/CcDirector.Core/Sessions/SessionType.cs`
  - Add `Support = 5` with the playbook doc comment + seeded pre-prompt text.
  - Rename enum members `Implement` -> `Developer` and `BugReport` -> `Product`
    (keep integer values 0 and 2). Update the playbook text for `Product` to the
    scope-and-file-issues behavior.
  - Mark `IssueSubmitter` legacy/hidden (kept for deserialization, not offered).
- `src/CcDirector.Core/Sessions/SessionGroupDefinition.cs`
  - Update the built-in **Product** group to 4 members:
    ```
    new(SessionType.Product,   " - product",   "Product"),
    new(SessionType.Developer, " - developer", "Developer"),
    new(SessionType.QA,        " - qa",        "QA"),
    new(SessionType.Support,   " - support",   "Support"),
    ```
- `src/CcDirector.Avalonia/SessionViewModel.cs`
  - Update `SessionTypeLabel` / `SessionTypeBadgeBrush` / `SessionTypeTooltip` for the
    renamed/added types; Product keeps magenta `[P]`, Support gets emerald `[S]`,
    Developer carries no rail badge.
- `src/CcDirector.Avalonia/NewSessionDialog.axaml` + `.axaml.cs`
  - `Create` radios -> Single/Group toggle.
  - Type radios -> `ComboBox` listing the five offered types (badge + description item
    template). `SelectedSessionType` reads the ComboBox.
  - Group radio -> `ComboBox` over `SessionGroupDefinition.BuiltIn`; show the member
    preview card. `SelectedGroupDefinition` reads the ComboBox.
  - (Optional) Agent radios -> `ComboBox`.
- `src/CcDirector.Core.Tests/SessionTypeTests.cs`
  - Support playbook is seeded; Product group has 4 members in fixed order; back-compat
    deserialization of older sessions still lands on the right types.

## Behavior

- **Single + Type** -> one session of the chosen type (existing `CreateSession` path,
  now reachable for all 5 offered types).
- **Group + Product** -> 4 tied sessions via the existing `CreateGroupSessionAsync`
  (one `GroupId`, fixed member order, adjacent sort). No new orchestration.
- Group creation stays **desktop-only** for now; the Gateway deserializes
  `NewSessionRequest` but has no group contract (noted in prior work). Cockpit's
  new-session flow is out of scope for this issue.

## Out of scope

- Inter-agent automation between the four members (mesh / hand-offs) - separate effort.
- User-defined custom groups (this ships the built-in Product group only).
- Cockpit (web) new-session parity.
