# How the hosted deploy's check gate treats a commit whose continuous integration run was cancelled

Written 20 September 2026, as part of package 1 of the fast continuous integration mission. Everything
below comes from the workflow file in this repository and from this repository's own run records, read
with `gh run list` and `gh api`. Nothing here is inferred from a comment.

## The short answer, in two halves

1. **A cancelled run is NOT accepted.** The gate refuses it. That specific worry is not a fail-open.
2. **But the gate is a fail-open anyway, and it is operating right now**, because its pass condition is an
   absence - "nothing has FAILED" - and a commit with no finished checks satisfies it. Every hosted Gateway
   deploy in the sample below passed the gate with no continuous integration verdict in existence at all.

## What the gate does

The step is `Refuse to deploy a commit whose checks have FAILED` in
`.github/workflows/deploy-hosted-gateway.yml`. It asks GitHub for the shipping commit's check runs,
excludes this workflow's own runs, and then:

```
failed  = status == "completed" && conclusion not in (success, neutral, skipped)
pending = status != "completed"
```

A non-empty `failed` refuses. A non-empty `pending` prints a note and carries on. No check runs at all
prints `has no check runs of its own yet` and carries on.

`cancelled` is not in the allowed set, so a cancelled check run lands in `failed` and the deploy is
refused. That is the first half of the answer.

## What a cancelled run actually leaves behind

Checked on seven commits on main whose continuous integration run ended `cancelled`, superseded by a
newer push:

| Commit | `Build & Test (.NET)` | `CI result` |
|---|---|---|
| `640a00189` | completed / cancelled | completed / failure |
| `0b282aafc` | completed / cancelled | completed / failure |
| `a8884d65f` | completed / cancelled | completed / failure |
| `a6b03bf48` | completed / cancelled | completed / failure |
| `7e9a026f7` | completed / cancelled | completed / failure |
| `e0b637117` | completed / cancelled | completed / failure |
| `caa56f36f` | completed / cancelled | completed / failure |

Two things are worth naming. A cancelled job reports `cancelled`, never `skipped` - including on runs
cancelled within a minute, where the job had barely started. And `CI result` reports `failure`, because
its own check is `flag:result` and `true:cancelled` is not `true:success`. So a cancelled run puts **two**
refusable check runs on the commit, not one.

Running the gate's own logic against `640a00189` and `e627eb467` today reproduces this exactly:

```
REFUSED: these checks did not pass on the commit being shipped:
  CI result (failure)
  Build & Test (.NET) (cancelled)
```

## The fail-open, which is the part that matters

The gate's verdict depends entirely on **when it is asked**, and in practice it is always asked before
there is anything to see. Deploys are started seconds after the merge, while the check runs do not yet
exist or have not finished. The gate then passes on an absence.

The last fifteen hosted Gateway deploy runs, with what their gate step printed and what the commit's
`Build & Test (.NET)` check eventually concluded:

| Deploy run | Commit | What the gate saw | What `Build & Test (.NET)` ended as |
|---|---|---|---|
| 35474750965 | `74485174f` | pending only | cancelled |
| 35411147193 | `c1766b5bc` | pending only | cancelled |
| 35406585169 | `e627eb467` | no check runs at all | cancelled |
| 35404821028 | `8b728fad8` | pending only | cancelled |
| 35374305812 | `837d58d46` | no check runs at all | failure |
| 35366050647 | `0b7469278` | no check runs at all | cancelled |
| 35311870122 | `9b0a2bf37` | pending only | failure |
| 35298570991 | `1fd50e192` | pending only | cancelled |
| 35297895761 | `7412016c8` | pending only | cancelled |
| 35297570007 | `44d5b6613` | pending only | cancelled |
| 35266985035 | `6b5060a87` | pending only | failure |
| 35262371039 | `00e184a94` | no check runs at all | failure |
| 35256631108 | `42677ed07` | pending only | cancelled |
| 35245041847 | `0b8e11f6c` | no check runs at all | **success** |
| 35242400021 | `ef411aa06` | no check runs at all | cancelled |

Fifteen of fifteen passed the gate with no finished check. One of the fifteen shipped a commit whose
.NET tests ever went green, and that one was not gated on it either - the gate had already passed before
the answer existed.

The clearest single record is deploy run 35406585169. The push landed at 23:39:51Z on 18 September 2026;
the deploy started at 23:40:02Z, eleven seconds later, and its gate printed:

```
NOTE: commit e627eb46707a08c602eec17b98926dc28567f383 has no check runs of its own yet.
Check gate passed: nothing has failed on e627eb46707a08c602eec17b98926dc28567f383.
```

The same commit's continuous integration run was cancelled by the next push. The same gate logic run
against that commit today refuses it. **One commit, two opposite verdicts, decided by nothing but the
moment the question was asked** - and the verdict that shipped is the one taken before any evidence
existed.

So the gate can only ever refuse a deploy that was started late. It cannot refuse the one started
immediately, and every deploy is started immediately.

## Why this was not fixed in package 1

The obvious repair is to restate the pass condition as a presence: the shipping commit must carry a
`Build & Test (.NET)` check run whose conclusion is `success`. That is a small edit to the step.

It is not a small change, because of what it would do today. The .NET job takes about 100 minutes
(103 minutes 9 seconds in run 35461379953, 19 September 2026) and most runs on main are cancelled by
the next push - 150 of 300 in the mission's frozen baseline, with only 21 of 97 runs on main finishing.
Requiring a green .NET check would therefore refuse almost every deploy, and the ones it allowed would
have had to wait out an hour and a half. The owner has ruled twice against exactly that wait, and the
step's own comment records the ruling.

It also runs ahead of the mission's own plan. Ruling 7 of the brief is that a green result becomes a
required check on main **once the run has held the twenty-minute budget for a week**. The honest order is:
make the run fast (packages 1 and 2), watch it hold, and only then make the deploy gate demand a finished
green verdict. Doing it now would mean either an unusable deploy or a gate that has to be bypassed, and a
gate people bypass is worse than the one we have, because it teaches everybody to wave it through.

## What fixing it would take, when the time comes

1. Change the step's pass condition from "nothing has FAILED" to a named presence: the commit must have a
   check run called `CI result` (the only check that speaks for the whole run - see the comment on the
   `result` job in `.github/workflows/ci.yml`) with conclusion `success`.
2. Decide what a deploy does while that check is still running. Either wait for it, which is only tolerable
   once the run is inside the twenty-minute budget, or refuse and tell the operator to come back - which
   is honest but needs somebody to be watching.
3. Decide what a deploy does when the run was cancelled by a newer push. The commit will never get a
   verdict of its own, because ruling 3 keeps the newest-push-wins cancellation. The options are to deploy
   the newest commit instead, or to re-run the cancelled run for that commit. This one genuinely needs an
   answer from the owner; it is a question about what "tested" means for a commit that was superseded.
4. Keep the existing exclusion of this workflow's own runs. Without it the gate refuses itself.

Step 3 is the reason this is not a self-contained edit. Steps 1 and 2 are a few lines; step 3 is a
decision.

## What this note does not cover

- It reads the deploy workflow and this repository's run records. It does not prove what any future run
  will do.
- The fifteen-deploy sample is the most recent fifteen runs of the hosted deploy workflow as of
  20 September 2026. It is not the whole history.
- It says nothing about the rollback or provisioning workflows, which have their own gates.
