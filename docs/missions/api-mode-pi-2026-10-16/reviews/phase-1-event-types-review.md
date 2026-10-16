# Review: phase 1, the session event model types (pull request #4074)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Reviewed on 16 October 2026 against
commit e7675a1d0, the head of pull request #4074, which sits directly on origin/main (84866de8a, the
phase 0 merge).

## Scope

What I read:

- The whole diff of the pull request: `src/CcDirector.Gateway.Contracts/ApiMode/SessionEvents.cs`,
  `src/CcDirector.Gateway.Contracts/ApiMode/SessionEventValues.cs` and
  `src/CcDirector.Core.UnitTests/Contracts/SessionEventJsonTests.cs`. Three files added, none changed.
- The contract, `docs/missions/api-mode-pi-2026-10-16/event-model.md`, section 2 in full and sections
  1, 3.3, 3.5 and 3.6 for the cases the fields have to carry.
- Issue #4070 and the pull request description.
- On origin/main: the Contracts project file, the existing polymorphic and enumeration conventions in the
  Contracts project, how the Director and the Gateway serialise today (System.Text.Json with web defaults,
  no Newtonsoft, no source-generated serializer context), and `docs/architecture/hosted-gateway-qa.md` on
  how the Gateway stores owned JSON.

What I ran, on devlinux with the .NET 10.0.301 toolkit:

- `dotnet test src/CcDirector.Core.UnitTests --filter FullyQualifiedName~SessionEventJsonTests`:
  25 passed, 0 failed, 0 skipped.
- The whole `CcDirector.Core.UnitTests` suite on the pull request head: 1,873 passed, 5 failed, 23
  skipped. The five failures are the ones the pull request description names: three
  `CcWorktreesPoolSlotsTests` cases asserting Windows drive paths, `RepositorySourceIndexTests`
  platform separators, and `WorktreePoolSettingsTests`. All five assume Windows paths and none touches a
  file this change adds. This agrees with the description; I did not take the description's word for it.
- A throwaway console program in my scratch folder, referencing the Contracts project, to see how the
  types behave on three inputs the tests do not cover: a document whose keys are not in the written
  order, an integer in place of an enumeration word, and two equal events compared with `==`. The
  results are under the findings below.

What I could not reach:

- The local gate script (`scripts/test-local.ps1`) is Windows-bound and was not run here; the pull
  request says the same.
- The Gateway side (phase 2) does not exist yet, so I could not check these types against a real reader.
  Finding 1 is about what that reader will meet.

## Verdict

The types match section 2 of the event model exactly: all eleven events, every field under the name the
note writes, every enumeration with exactly the note's words in the note's order, the three common fields
on every event and `turnId` on the six events inside a turn, a `type` discriminator carrying the event's
name, and refusals for an unknown event, a missing required field and an unknown enumeration word. The
tests pin all of that and use a fixed clock value, no process, no port and no sleep. Test names follow
Method_Scenario_Result. There are no methods with behaviour, so there is nothing to log. I found no
abbreviation outside the note's own field name `stderrTail`, whose summary spells it out.

Two findings. The first is worth settling before phase 2 stores its first event; the second is a
one-line amendment to the note. Neither is a mismatch with section 2. Three observations follow, which
are not findings because I cannot show something that breaks today.

## Findings

### 1. A stored event cannot be read back: the discriminator must be the first key, and the Gateway's JSON storage does not keep key order

Place: `src/CcDirector.Gateway.Contracts/ApiMode/SessionEvents.cs`, lines 13 to 16 (the summary's
warning and the `JsonPolymorphic` attribute). No shared `JsonSerializerOptions` is offered anywhere in the
change.

What breaks: with default serializer options, System.Text.Json reads a polymorphic record only when the
`type` discriminator is the first key. The pull request knows this and says so in a comment: "a reader
of another origin must keep it first, or set `AllowOutOfOrderMetadataProperties`". Phase 2's job is to
"store receipts" (mission document, section 5). The Gateway keeps owned JSON in PostgreSQL `jsonb`
columns (`docs/architecture/hosted-gateway-qa.md`: eight such columns today), and `jsonb` does not keep
key order: it returns keys shortest first, then bytewise. On every one of these events `at` is two
characters, so it comes out ahead of `type`. I fed exactly that document to the types:

    {"at":"2026-10-16T12:00:00+00:00","type":"PromptRefused","reason":"busy", ...}

Default options: `NotSupportedException: The JSON payload for polymorphic interface or abstract type
'CcDirector.Gateway.Contracts.ApiMode.SessionEvent' must specify a type discriminator.` Note the
exception type: it is not a `JsonException`, so a reader that catches `JsonException` for a malformed
record does not catch this one either. With `AllowOutOfOrderMetadataProperties = true` the same document
reads back as `PromptRefused`.

Why it must change: the Director writes and the Gateway reads, in separately deployed processes, and
the rule they both need (which options to read with) lives today in a code comment on the writer's side.
A comment is not a contract; the first reader built without seeing it fails on the first receipt that
went through storage, and the receipt is the whole point of the mission. The Contracts project is the
one place both sides compile against, so the rule belongs there, with a test that reads a document whose
`type` is not first. How (a shared options object, or a ruling that events are never stored in a
key-reordering column, with a test that holds the ruling) is the Developer's call.

Developer's answer:

### 2. Three fields may be null, and the note that phase 2 and phase 3 build from does not say so

Place: `src/CcDirector.Gateway.Contracts/ApiMode/SessionEvents.cs`, lines 150 (`durationMilliseconds`),
178 (`reason` on `TurnEnded`) and 182 (`agentStopReason`). The pull request description lists this
under "choices the event model left open".

What breaks: the brief for this review and for phase 2 is that section 2 of the note is the contract,
and phase 2 is built in parallel from the note, not from this pull request. The note writes
`durationMilliseconds` and `agentStopReason` (verbatim) with no mention of absence; only `reason` is
marked "required unless done". A phase 2 fold or a phase 3 client typed from the note takes a number
and a string, and meets `null` the first time a turn is cancelled with a tool running (rule 3 of the
note closes that tool with no measured duration) or Pi exits mid-turn (section 3.5: no stop reason
exists). Each null is the honest value, and the C# types are right to carry it; the harm is that the
contract document does not.

Why it must change: one sentence in section 2 of the note, naming the three fields that may be null and
when, keeps the note the single contract. Which document moves (the note, or the types) is the
Developer's call; they must agree.

Developer's answer:

## Observations (no finding: nothing shown to break today)

- **An integer is accepted in place of an enumeration word.** `JsonStringEnumConverter<T>` allows
  integer values by default, so `"outcome":7` on a `TurnEnded` reads as `TurnOutcome` 7, which is not a
  defined value (checked with the throwaway program: accepted, `Enum.IsDefined` false). The file header
  says "never the enum's number" and the test class says an unknown word is refused; both are true of
  words only. No writer sends a number today, so nothing breaks. If the Gateway's fold ever switches on
  these values with a default branch, an undefined value would be rendered as something plausible, which
  is what rule 7 of the project instructions (the client is dumb) forbids. Refusing integers is a converter subclass with
  `allowIntegerValues: false`.
- **Events with a list field do not compare equal by value.** `TurnStarted`, `PermissionRequested` and
  `ConnectionEnded` hold `IReadOnlyList` members, and C# records compare those by reference: two
  `TurnStarted` built from the same values compare unequal (checked). The round-trip test already notes
  this and compares JSON instead. A phase 2 test that uses `Assert.Equal` on two such events, or a
  de-duplication that relies on equality rather than `sequence`, would be wrong. The note says to
  de-duplicate by `sequence`, so this is a note for the next Developer, not a defect.
- **`position` on `PromptQueued` has no stated origin.** The note and the types say "where the prompt
  stands in the Director's queue" and the test uses 1. Whether the first held prompt is position 0 or 1
  is undefined until the dialect writes one; the Gateway reader needs to know. A word in the note or the
  summary settles it.
