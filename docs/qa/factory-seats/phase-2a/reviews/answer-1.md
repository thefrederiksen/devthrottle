# Factory Seats, phase 2a - the answer to review 1

Date: 2026-10-13. Answered by a Developer opened by the Implementation Lead for this one item; the Developer who
built the branch is gone (law 11). Branch: `factory-seats/phase-2a-brief-store`. The code that answers the finding
is commit `f4f7002ed`.

Review 1 returned one finding. It is answered below. Its four observations are not findings and need no answer;
two of them are touched by this change and are noted at the end.

## The finding

> Medium. Nothing bounds the growth of one seat's brief history, every session of the factory can grow it, and the
> history route returns all of it.

**Accepted.** The harm is real and the review proved it: a session's key is a credential nobody is watching, a save
adds a whole version of up to 65,536 bytes, and the history route answered with every version's whole text. A
session that saved on every turn, or one stuck retrying, grew both the table and the answer without end, on a
database several accounts share.

The mission's goal 5 - every version is kept - is not given up. Nothing is pruned and nothing is deleted, by this
change or by any path in the store. What is bounded is the writing and the reading.

### The bound on writing

**A session may add a draft only while fewer than 20 drafts wait above the seat's published version.** With nothing
published yet, every version is a waiting draft. The save that would cross the limit is refused with status 409,
the outcome `TooManyDrafts`, the newest version under `current`, and this sentence:

> the brief of 'boss' has 20 drafts waiting that nobody has published (versions 1 to 20), and a session may add a
> draft only while fewer than 20 are waiting. Nothing was saved, and no version was removed. Ask the owner to read
> the drafts and publish one: publishing makes room for more.

Why this bound, and not the two others the review offered:

- **It is a hard bound for the credential the finding is about.** Only a person publishes (the mission's P3), and
  publishing is the only thing that lowers the count. So sessions alone can add at most 20 versions to one seat
  between two acts of a person, however many sessions there are and however fast they try.
- **A limit on how often a session may save was not chosen.** It slows the growth and never stops it: a session
  saving once a minute still adds 1,440 versions a day. It also needs a clock and a record of each writer, which
  the store does not have and the limit on waiting drafts does not need.
- **A page on the history route alone was not chosen either.** It bounds the read and leaves the table growing.
  It is done as well, below, because the read needs it whatever the writing does.
- **Two replicas cannot both slip past it.** Both would be writing the same next version, and the primary key lets
  only one of them exist - the same arbitration every save already relies on.

**Twenty** is far more than a seat's sessions leave between two readings by its owner, and it makes the most that
sessions can add to one seat between two acts of a person 20 versions of at most 65,536 bytes: about 1.3
megabytes. It is one constant, `FactoryBriefStore.MaxWaitingDrafts`.

**What counts** is every draft above the published version, whoever wrote it. Publishing the newest draft leaves
none waiting. Publishing an older one leaves the drafts above it waiting, and the refusal names which versions
those are. Bringing an older version back publishes a copy as the newest version, so it makes room too.

**A stale save at the limit is told it is stale**, not that too many wait: the stale refusal is the one that writer
can act on, and it is checked first.

### Is a person bound the same way? No, and why

**A person is not bound.** The task asked for this to be decided and said.

The limit exists for a credential nobody is watching. A person is the one who makes room, by publishing. A person
who was bound could correct twenty bad drafts only by first publishing one of them - making a text they did not
want the one every talk and run is given, in order to be allowed to write the one they do want. That is worse than
the growth it would prevent.

A person's drafts still wait like any other and count toward the limit, so a session is refused until the person
publishes. Twenty drafts nobody has published are twenty drafts nobody has published.

What this leaves open is named plainly under "What this does not prove" in the proof: a person's device key is not
bound, so a script holding one could still add versions without end.

### The bound on reading

**The history route answers with one page of versions, newest first, without any version's text.** Each version
carries its number, its state, who wrote it, when, when and by whom it was published, and how many bytes its text
takes (`textBytes`). The page also says how many versions the seat has in all (`total`), its newest and published
versions, and where the next, older page starts (`nextBefore`, null on the last page). The Gateway decides whether
there is more; a reader never works it out.

- `limit` is how many versions a page holds, 1 to 100, and 50 when it is not sent. `before` is the version the
  page starts below.
- **A value that is not one is refused with status 400 and a sentence, never quietly replaced.** A reader that
  asked for 500 and was quietly given 100 would believe it had seen them all.
- **The text is read one version at a time**, from a new route: `GET /factory-briefs/{factory}/{seat}/versions/{version}`.
  It is guarded exactly as the other reads are, and the session key guard knows it.
- So no read under `/factory-briefs` now answers with more than one version's text, and the largest history answer
  is 100 small rows whatever the history's length.

**The size of a version's text is stored with it** (`TextBytes`, written once by the save). Without it the history
would have to read every text on the page to measure it, which is the cost the page exists to remove. The column
is added to this branch's own migration, in place, in both the local and the PostgreSQL migrations and both
snapshots. That is safe only because the table has never been merged or deployed - see the proof for the one case
where it is not.

### The command line and its reference

- `cc-devthrottle factory brief history` takes `--count` / `-n` (1 to 100) and `--before`. It prints one line a
  version with the size of its text, says `count: 50 of 130.` so one page is never taken for the whole history,
  and when older versions follow it says so and names the command that reads the next page.
- `cc-devthrottle factory brief get` takes `--version N` for one version's text. `--draft` and `--version`
  together are a usage error and call nothing.
- `cc-devthrottle factory brief set`, refused because too many drafts wait, prints the Gateway's own sentence and
  points at the history. It does not try again and prints nothing that tells its caller to.
- `docs/cli-reference.md` says all of it under "Factory brief".

### The tests, at the points where it is hard

Law 17: a review that finds a problem means the tests were not good enough. These are new.

| What is hard | Test |
|---|---|
| The save that crosses the limit: the twentieth goes through, the twenty-first does not, with the exact sentence, and nothing is saved or removed | `A_SESSION_IS_REFUSED_THE_DRAFT_THAT_WOULD_CROSS_THE_LIMIT_and_the_refusal_says_what_to_do` |
| The loop the finding describes: two sessions retrying two hundred times at the limit add nothing | `A_SESSION_THAT_KEEPS_RETRYING_AT_THE_LIMIT_ADDS_NOTHING_however_often_it_tries` |
| Publishing frees it, and removes nothing | `PUBLISHING_MAKES_ROOM_and_removes_nothing` |
| Publishing an older draft frees only what no longer waits above it | `Publishing_an_OLDER_draft_makes_room_only_for_as_many_as_no_longer_wait_above_it` |
| Bringing a version back frees it too | `BRINGING_AN_OLDER_VERSION_BACK_MAKES_ROOM_TOO_because_it_publishes_the_newest_version` |
| A person is not bound, and a session stays refused until the person publishes | `A_PERSON_IS_NOT_BOUND_because_the_person_is_the_one_who_makes_room` |
| A person's drafts count as waiting | `A_PERSONS_DRAFTS_COUNT_AS_WAITING_whoever_wrote_them` |
| The limit is one seat's | `The_limit_is_one_seats_and_another_seat_has_its_own_room` |
| Stale is told first | `A_STALE_SAVE_AT_THE_LIMIT_IS_TOLD_IT_IS_STALE_first_because_that_is_what_it_can_act_on` |
| Every version is still kept: 130 versions, the first read back with its text | `EVERY_VERSION_IS_KEPT_however_many_there_are` |
| A long history read page by page, every version seen once, none twice | `A_LONG_HISTORY_IS_READ_PAGE_BY_PAGE_and_every_version_is_seen_exactly_once` |
| A page that ends exactly on the oldest version does not promise another | `A_page_that_ends_exactly_on_the_oldest_version_does_not_promise_another` |
| The size is bytes, not characters | `A_page_carries_what_each_version_is_and_HOW_BIG_ITS_TEXT_IS_in_bytes` |
| A page can never carry a text | `A_PAGE_OF_THE_HISTORY_NEVER_CARRIES_A_VERSIONS_TEXT`, and through the mapped route `THE_HISTORY_COMES_BACK_AS_ONE_PAGE_WITHOUT_ANY_TEXT_and_one_version_is_read_for_its_text` |
| The refusal through the route, by status, outcome and sentence | `A_SESSIONS_DRAFT_PAST_THE_LIMIT_IS_A_CONFLICT_THAT_SAYS_TO_ASK_THE_OWNER_TO_PUBLISH` |
| A page size that is not one is refused, not replaced | `A_page_size_or_a_starting_version_that_is_not_one_is_REFUSED_in_words_never_quietly_replaced` |
| A session of another factory cannot read one version either | `A_SESSION_OF_ANOTHER_FACTORY_MAY_NOT_READ_OR_WRITE_THIS_FACTORYS_BRIEF` |
| The command line reads a long history by running the command each page names | `test_history_ALongHistory_IsReadPageByPage_ByRunningTheCommandEachPageNames` |
| The command line does not retry a refused save | `test_set_WhenTooManyDraftsWait_IsNotSaved_PrintsTheGatewaysSentence_AndPointsAtTheHistoryNotARetry` |

## Two things the Implementation Lead should know before phase 2b

1. **Until phase 2b exists, a seat takes 20 drafts from sessions and then no more.** Review 1 observed that no
   person can publish before the brief panel exists. With this limit that has a consequence: nothing can make
   room, so the twenty-first draft of a seat is refused until phase 2b ships. The refusal is correct and says what
   to do; there is simply nobody who can do it yet.
2. **The history answer changed shape.** `versions` no longer carries `text`, and the answer gained `total`,
   `newestVersion` and `nextBefore`. Nothing but the command line read it. The brief panel of phase 2b reads a
   version's text from the new route.

## The review's observations that this change touches

- "The history route answers with every version's whole text" - no longer true; see above.
- The Developer's decision 2 ("growth is bounded only by the cap on one version") and decision 6 ("history
  carries every version's whole text") in the proof are replaced, and the proof says so.
