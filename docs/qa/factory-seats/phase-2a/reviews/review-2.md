# Factory Seats, phase 2a - review 2: the answer to review 1's finding

Date: 2026-10-13. Reviewer: a Reviewer seat opened by the Implementation Lead (a different agent from the Developer who
answered). Branch reviewed: `origin/factory-seats/phase-2a-brief-store`, at the commit that answers review 1. This review
is NARROW by instruction: it reads only what commit `f4f7002ed` changed, and asks whether that commit broke anything review
1 found sound. The whole of the brief store was reviewed three days ago in review 1 (`review-1.md` beside this file) and
is not re-reviewed here. The review lives on `factory-seats/review-2a-second`, cut from that branch in a worktree; the
shared checkout was never touched.

## Verdict

Nothing found. The bound on a session's waiting drafts, the page on the history, the new route that reads one version's
text, and the size column are built as the answer says they are, and the places where harm would be real held up under
reading: I could not find a path by which a session gets past twenty waiting drafts, a reader makes a page return
everything, or the new route reads anything but this account's, this factory's, this seat's one version. A review may
return nothing, and this one does. The observations at the end are not findings.

## Scope

**What I read, in full:** the commit's changes to the store (`src/CcDirector.Gateway/Factory/Briefs/FactoryBriefStore.cs`,
including the new page, the single-version read, the limit check and its place in the save), the routes
(`src/CcDirector.Gateway/Api/FactoryBriefEndpoints.cs`, including the new `GET /versions/{version}` route and the query
parsing of `limit` and `before`), the entity's new size column, the session key guard's new entry, the answer bodies
(`src/CcDirector.Gateway.Contracts/FactoryBriefDtos.cs`), the two hand-written migrations, the command line module
(`tools/cc-devthrottle/src/factory_brief_ops.py`) and its two commands in `cli.py`, the command reference section, and
all four changed test files (store, routes through handlers, routes through the mapped endpoints, and the command line
tests). I also read the Developer's answer (`answer-1.md`, written beside review 1) so the review judges what the answer
claims, not a strawman of it. Around the change, I read the whole current store, the whole current routes file and the
guard's whole brief section, so the commit is judged in context and not as isolated lines.

**What I verified rather than read line by line:** the two generated migration Designer files. As in review 1, I did not
read several thousand generated lines; I verified instead that both migrations, both snapshots and the entity all agree on
the new column - it is `TextBytes`, an integer that cannot be null, added to the same position in the table in the local
and the PostgreSQL migration, and present in both providers' snapshots in the same shape.

**What I ran:** the brief tests of the Gateway unit test suite, filtered, on this machine
(`dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoryBrief"`): one hundred and four passed,
none failed - thirty-four more than review 1 ran, all of them new since. The command line tests for the brief
(`tools/cc-devthrottle/tests/test_factory_brief_ops.py`): twenty-nine passed and fifteen failed, and every failure is
this machine's `click` being 8.1.8 where the tool declares a floor of 8.2.1, the first version that hands back standard
error separately from standard output - each failure dies on "stderr not separately captured" inside the test harness
before it can judge the command. The already-merged factory memory command tests fail the same way on this machine, so
this is the machine and not the branch, exactly as review 1 recorded. The refusals the failing tests pin still showed
their non-zero exit codes in the output I could see.

**What I could not reach:** the full Gateway unit test suite, the hosted Gateway test suite, the database test suite
against a real PostgreSQL (the Developer reports it green; I did not repeat it), a live two-replica race on one database,
and the Developer's proof (`README.md`), which is the OTHER commit on the branch (`065f6b864`, documentation only) and so
outside this review's scope; I read its diff's file list only, to confirm nothing else changed.

## The places where harm would be real, and what I found

**A session getting past twenty waiting drafts by any path.** Three paths add a version: a draft save, a publish (which
marks, never adds) and a bring back. A draft save by a session is the only path a session can take, and it is refused
while twenty drafts wait above the published version. The limit counts every draft above the highest published version,
whoever wrote it, so no amount of writing by another session, or by the person, can hide a waiting draft from the count.
A session cannot take the other two paths: the route lets a session key reach them on purpose, and the store refuses a
publish or a bring back from any author that is not a person, in words. Two replicas cannot both slip past: both must be
writing the same next version to pass the stale check, and the key lets only one of them exist - the same arbitration
every save already relies on, unchanged. The nineteenth draft goes through, the twenty-first does not, and a hundred
retries by two sessions add nothing; all pinned by tests I read and ran. Nothing found.

**The count using the right "published version" when an older version was brought back.** The count is taken against the
HIGHEST published version, not a pointer. Bringing an older version back publishes a copy of it as the newest version,
so it becomes the highest published one and empties the waiting count at a stroke. Publishing an older draft in place
moves the highest published version up to it, and only the drafts above it keep waiting - the refusal then names which
versions those are, and the test pins the exact sentence for publish-fifteen-of-twenty. A draft below the published
version cannot be published and never counts as waiting, which is right: it blocks nothing and never blocks anyone.
Nothing found.

**The limit locking a seat so that even a person cannot make room.** A person is never refused by the limit - the check
applies only to a session's drafts - and a person always has a move that lowers the count: the newest draft is always
above the published version, so the person can always publish it, and a bring back always works. Publishing removes
nothing; every version stays. A person's own drafts wait and count like any other's, so twenty bad drafts by a person
still shut the sessions out until the person publishes one - decided, argued and pinned by a test, and I find the
decision sound. Nothing found.

**The page: limit or before made to return everything, or to read another seat's or another account's versions.**
`limit` is capped at one hundred at the route and the store throws on any caller that hands it more, so no reader can
ask for a whole history in one answer; a value that is not a whole number from one to one hundred is refused with a
sentence rather than quietly replaced, and the test pins the sentence for zero, one hundred and one, and words.
`before` only moves a page's starting point downward; it cannot widen what the query returns. The account is taken from
the request's own credential and the factory and the seat from the path, exactly as before, and the page's rows come
from the same account-scoped context; the two-account test still pins that one account holds nothing under the other's
identical ids. Nothing found.

**The new route behind the same gate and the same account scoping, and in the session key guard at the right width.**
`GET /factory-briefs/{factory}/{seat}/versions/{version}` walks the same three steps as the other reads: the account
from the credential, then `FactoryNaming.TryAct` - the one gate the factory's schedules and triggers pass - then the
registered-seat check; only then is the version parsed, and a version that is not a whole number from one is refused in
words. The guard's entry is exactly this route: five segments under the brief prefix, `versions` in the fourth place,
reads only. It is neither missing - a session of the factory can read a version, pinned through the mapped route - nor
wider: a write to it, `.../versions` without a number, and `.../versions/7/text` are all refused, and the two tests that
make the guard's list and the mapped routes agree now cover all seven routes. A session of another factory is refused
by the gate on this route like every other, and the machine token may read it like the published brief. Nothing found.

**The migration edited in place.** The local and the PostgreSQL migration, both snapshots and the entity agree on the
new column, as described under scope. The table has never been merged or deployed, so editing this branch's own
migration in place is safe; the one case where it is not - a local development database already created from this
branch's earlier commits would lack the column - is named in the Developer's proof, and nothing in the merged world
can hit it. Nothing found.

**Whether the answer broke what review 1 found sound.** The stale-save refusal still comes first: a session saving
against an old version at the limit is told it is stale, not that too many wait, because stale is the thing that writer
can act on - pinned by a test. The publish mark is still set once: the update still matches the row only while it is a
draft, unchanged. The bring back is unchanged, and the copy it writes now carries the size of its text, recomputed from
the text itself, so it cannot disagree with the stored size. The composite key and the second-writer arbitration are
untouched. The one behavioural change to what review 1 read - the history answer no longer carrying any version's text,
and `versions` changing shape - was review 1's own finding, is named in the answer, and has no reader but the command
line, which was changed with it. Nothing found.

## Observations, which are not findings

- **A `before` above the newest version returns the newest page, not an empty one.** A reader who hand-sends
  `before=999` to a seat with a hundred and thirty versions gets the newest fifty with a correct `nextBefore`. The
  Gateway hands every reader its `nextBefore` and the command line follows it, so no real reader reaches this; the
  behaviour below the oldest version (an empty page, not an error) is pinned by a test, and this corner above the
  newest is merely unpinned. Harmless, recorded so nobody mistakes it for a defect later.
- **A person's device key is still unbounded.** The limit binds a session's key; a script holding a person's device key
  can still add versions without end. The answer names this itself under what it does not prove, and it is a residual
  risk accepted with eyes open, not a defect in what was built.
- **Until phase 2b ships, a seat that takes twenty drafts is full.** Nothing can make room, because no person can yet
  publish: the command line holds only a session key. The refusal says exactly what to do and there is nobody yet who
  can do it - a consequence of the phase order that review 1's observation, the answer's note and this review all
  agree on.
