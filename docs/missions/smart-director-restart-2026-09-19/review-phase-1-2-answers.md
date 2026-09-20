# Answers - review of phase 1, task 2: the restart cycle over the real drain (defect #3169)

Answered by the Tech Lead of phase 1, second seat (session 1c3174ba), on 20 September 2026. The
Developer that built task 2 and the first Tech Lead that opened it are both gone, so under law 11 the
answer falls to the seat that took over the phase. The work is merged as pull request 3181.

## Findings

The review returned none. There is nothing to accept or decline.

## The two candidates the Reviewer weighed and rejected

The Reviewer recorded them so they would be answered rather than rediscovered. Both are answered here.

**Candidate A - a drain that throws names no record in the abandoned request. DECLINED, no change.**
The Reviewer's reasoning holds. The only path that stores a record and then throws is the preflight
refusal, where nothing was messaged and nothing was closed, and the record carries the same refusal in
its own integrity block. The other throw paths are save failures, which mean the Gateway cannot be
reached, and then the abandoned report cannot land either. A pointer there would help nobody find
anything urgent. It is not taken into task 3 or task 4 either: the smart shutdown run reports its record
through `SmartShutdownSnapshot.WorkspaceId` and `SmartShutdownResult.WorkspaceId`, which is where a
screen looks.

**Candidate B - the record name is stamped to the minute. DECLINED, no change.** The rule is the
desktop's existing rule, moved and not changed, so that both doors mint a name one way (mission 5.3
item 9). Two runs inside one minute are already refused loudly, twice over: by the one-at-a-time gates,
and by the drain's preflight, which names the collision. A loud refusal is the right behaviour under
the no fallback rule. The smart shutdown run of task 3 mints its name through the same rule and
inherits the same refusal; if phase 5's QA run shows an owner can really hit it (cancel, then start
again inside the same minute), that is a finding for that phase with evidence behind it.

## What this answer does not cover

I did not re-run the mutation proofs of task 2. I ran the mission's check on `origin/main` at
`9f79e92dc`, which holds both task 1 and task 2: 487 passed, 0 failed.
