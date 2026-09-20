# Proof That Covers The Wrong Thing

**A proof can be completely valid and still be silent about the thing you changed.**

[[checks-that-fail-open]] is about evidence that was never gathered. This is the harder sibling: the
evidence WAS gathered, the argument IS sound, and it covers something adjacent to the question. It
reads exactly like a proof, because it is one - of a different claim.

Four shapes, from a single mission on 2026-07-20. Each cost real work: a role change, a restart, and a
hotfix aimed at a bug the system never had.

## 1. The premise nobody tested - a ruling is not evidence

**A ruling settles what to BUILD. It does not establish the FACTS it rested on.**

A family of routes was ruled a hosted deny at both doors. The ruling rested on a premise - that
launchers are not a hosted feature - which was *asserted*, never proven. If something hosted genuinely
depended on them, the ruling was wrong, and that needed to surface in the analysis rather than after it
shipped.

The reason this needs to be a rule rather than good manners: **a ruling handed over as settled context
is precisely the thing that stops being tested.** The worker is not careless, it is obedient - and
obedience to a premise is indistinguishable from verification of it in the written output.

So, at the moment work is delegated, not in a review afterwards:

1. **Name the premise** the work rests on, separately from the instructions.
2. Require an answer per premise: **VERIFIED against the source**, or **ASSUMED**.
3. Contradicting evidence is **led with**, never softened into a closing caveat.

**An assumed premise is not a defect. Carrying one silently is.**

The architect who set this ruled it standing with: *"I would rather be corrected twice in an hour than
have my assumptions calcify into the record."*

## 2. The reader who cannot see it - you cannot verify from inside

**The author is the last person able to see their own prose false.** A comment is the author's INTENT
written down, and the author reads that intent back out of it no matter what the code says. The reader
sees the words; the author sees what they MEANT.

This is structural, not a competence gap, and it unifies things that look unrelated: a comment whose
guarantee the code never delivered; a census exhaustive over its population and blind to what it did
not ask; a seat that could not step outside the frame it was reasoning inside; a warning whose writer
felt covered by having written it for other people.

**The verifier and the verified must not be the same reader, the same question set, the same frame, or
the same author.** Reading your own work harder cannot fix it, because the failure is precisely that
reading from inside cannot see it. What works is a different reader, made mechanical where possible:

- A comment asserting a guarantee is an ASSERTION. Make the code deliver it, or change the comment.
- Where the guarantee is load-bearing, write the test that REDDENS when the code stops delivering it.
- The independent review seat and the audited question set are the same move at larger scale.

This law landed in its own author's lane the night it was written. The class does not spare the person
naming it, which is the strongest evidence it is structural.

## 3. The surface you did not touch - and skipped is not covered

A value converter was added inside `if (Database.IsNpgsql())` - a Postgres-only change. An unrelated
test flaked in the same run and was dismissed by a by-construction argument: the change is local to the
Postgres branch, the default suite runs SQLite, so that model is byte-identical with and without the
change; a regression is impossible.

**The argument was correct, and it covered the wrong provider.** It proved the SQLite surface was
unchanged. The change lived on the Npgsql surface, where the proof said nothing - and that is exactly
where the real regression sat. Two opt-in real-Postgres facts broke. Both continuous-integration jobs
were green, because neither RUNS those facts without a real Postgres.

> **A skipped test looks like coverage and runs nothing.** A green suite with the relevant facts
> skipped is a green over a blind spot.

The mechanism that closes it: the facts must be seen to EXECUTE-AND-PASS. **The skip count must drop**
by the number un-skipped, and each must appear **by name in the passed list**. "I ran them" is refused.

Generally: when a change is provider-, config- or platform-specific, its proof must run on the surface
it CHANGES. A by-construction argument about the other surface is valid for that surface and must never
stand in for coverage of the changed one.

## 4. The caller that is not your code - a re-derivation reproduces its own error

A hosted enrolment was returning 503. The cause was diagnosed as an unqualified SELECT resolving via
`search_path`, reproduced by running an unqualified `psql` query that returned
`42P01 relation "entitlements" does not exist`. A role change and a container restart were applied, and
a hotfix was written.

**Every step chased an error the gateway never emits.** Its data layer already qualified the read, so
it ran `FROM gateway.entitlements`, not the bare name the reproduction used. The reproduction produced
*a* plausible, related-looking 42P01 - not the system's.

> **A hand-run query, a psql session, a second client - each is a DIFFERENT caller than the code, and
> the error it reproduces is its own.** Reproducing a matching symptom is not observing the system
> produce it.

What it forces: **when a failure's cause is not directly observed in the failing component's own
output, the first deliverable is the INSTRUMENT that makes it observable - not a fix.** Plan for a
READING, not a fix, until the instrument has spoken. And label a guard that changes no behaviour
honestly: it is a regression guard on a property that already holds, not the fix.

## How to use this

Before calling something proven, answer in one sentence each:

- **What premise is this resting on, and did anyone check it?**
- **Who read this who is not me?**
- **Which surface did I change, and did the evidence run there - or was it skipped?**
- **Did I watch the system produce this, or did I reproduce something that looked like it?**

The useful output is not "it is proven". It is **the sentence naming what your evidence does NOT
cover.** If you cannot write that sentence, you have not finished looking.
