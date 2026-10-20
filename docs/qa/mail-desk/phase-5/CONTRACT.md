# Mail Desk, phase 5: backup machines - the contract

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 5 and section 5.9 (D2: "If one
is down, we could have a backup"). Run as one Developer task under the phase 3 Tech Lead; it ships in phase 3's pull
request. Written 18 October 2026, before any code. **Gateway-only: no Director change, so no desktop release.**

## 1. What changes

| Point | Decision | Why |
|---|---|---|
| How a start names its backups | A start with a check may carry `backupMachines: [<machine>, ...]`, in order of preference, after the seat's own machine (`machine`, or `defaults.machine`). The seat's machine is always first; a backup may not repeat it or another backup (case-insensitive). Allowed only on a start with `every` and a `check`. | "A start runs where its seat runs" stays true: the backups are where its CHECK runs while the seat's machine is away. One list in order, written once. |
| Where it is kept | Two new columns on the trigger row (both databases, one migration pair): `BackupMachinesJson` (a JSON array, empty by default) and `ClaimedByMachine` (the machine of the Director holding the claim). | A claim must know which machine holds it to hand it back. |
| Who takes the claim | A Director on a machine at position `p` in the list (0 = the seat's machine) takes the claim when it is free, lapsed (`TriggerStore.ClaimLapse`, two intervals and a minute, unchanged), **or held by a Director on a machine at a later position**. | The next machine takes over when the first has lapsed; the first takes it back as soon as it asks for its assignments again. |
| What the Director that lost the claim sees | Its next report is refused `HeldByAnotherDirector`, nothing recorded, exactly as today between two Directors on one machine. It is not handed the trigger at its next assignment poll. | No Director change. |
| Where a session starts while a backup holds the claim | On the machine of the Director that ran the check (the claim holder), not the seat's own machine, which is away. Unchanged when the seat machine's own Director ran it. The seat then needs its repository and agent on the backup; when it cannot start there, that is a failed start naming the machine. (Raised by the phase 5 Developer, 18 October 2026, accepted by the Tech Lead.) | Mission 5.7: a seat is started "on the machine that ran the check". A start sent to a machine that is away cannot succeed. |
| The memory | Unchanged: it is on the Gateway (`factory_check_state`), keyed by the trigger, so whichever machine holds the claim continues from it. Phase 3's per-channel merge keeps each channel's cursor across machines. | Mission 5.9. |
| A backup machine with no sign-in | Its check fails per mailbox with phase 2's sentence naming that machine and the fix (`<address> is not signed in on <machine>: run cc-outlook -a <nickname> auth`), as phase 3 section 6 makes it: the problem names the machine, so the owner knows which machine needs the sign-in. | "A machine without a sign-in fails its check naming the fix." |
| Publish | The publish's machine questions (`FactoryMachineQuestions`) ask every machine on the list, backups included, for the check's secrets, Python and uv, so a backup missing uv is said at publish time. Changing a start's backups on a republish keeps the claim when its holder is still on the list, and releases it when not (`WriteDefinitionTrigger`'s existing rule for a moved trigger). | A backup that cannot run the check should be known before it is needed. |
| The template | The Mail Desk template is NOT changed: it names `devlinux` only. Adding SOREN_NORTH as a backup is the owner's edit of the account's own draft before publishing (question 2: "SOREN_NORTH is the backup once phase 5 is in"). | The template is for every account; a machine name of the owner's does not belong in it. |
| Every machine named | Shown wherever a trigger's machine is shown today (`trigger list`/`show`, the factory page's start line, the export's sentence "It runs every ... on <machine>"): "on devlinux, then SOREN_NORTH if devlinux is away". Folded on the Gateway. | Rule 7. |

## 2. Tests (all required, with an injected clock, no real Director, no port)

1. **Handover:** the first machine's Director claims and reports; it goes quiet; before the lapse the backup's
   Director is handed nothing; after the lapse it is handed the trigger, its report is recorded, and it is handed the
   memory the first one left (the cursor continues).
2. **Hand-back:** with the backup holding a live claim, the first machine's Director asks for its assignments and is
   handed the trigger; the backup's next report is refused `HeldByAnotherDirector` and records nothing; the backup's
   next assignment poll hands it nothing.
3. A machine on no list is never handed the trigger, held or not.
4. Three machines: the second takes over from the first, the first is still away when the second goes quiet, the third
   takes over, and the second taking it back from the third while the first is away.
5. Two Directors on the SAME machine keep today's behaviour exactly (the existing tests stay green unchanged).
6. The definition check refusing: `backupMachines` repeating the seat's machine, repeating itself, on a start with no
   check, on a cron start, or blank.
7. Publish: the machine questions include the backups; a republish removing the holder's machine releases the claim;
   a republish keeping it does not.
8. The migration: both databases, proved with `scripts/test-database.ps1` (needs Docker).
9. The words shown for a trigger with and without backups.
10. A backup machine whose mailbox sign-in is missing: phase 3's per-mailbox problem names the backup machine (a
    `MailDeskService` test with a stand-in report; this one waits for phase 3 task F and is added there if it is not
    possible earlier).

## 3. What is not in this phase

- No Director change: a Director still runs what it is handed. Telling a Director that it is a backup is not needed.
- No live failover test on two real machines; that is phase 6's, if the owner's second machine is online.
- No automatic sign-in on the backup: the owner signs in there with cc-outlook, once.

## 4. Example data

Machine names in tests are invented (`machine-a`, `machine-b`, `machine-c`), never the owner's. Addresses follow phase
3 section 12.
