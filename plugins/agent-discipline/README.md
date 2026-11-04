# Agent Discipline

Three skills about evidence. They name no product, they need no setup, and they work on
the first repository you point them at.

```
/plugin marketplace add thefrederiksen/devthrottle
/plugin install agent-discipline@devthrottle
```

## What is in it

**`checks-that-fail-open`** - a check whose pass condition is an ABSENCE certifies a
run that never happened. Doing nothing at all satisfies "no errors found". Doing
nothing produces the wrong value against a specific presence. The skill is the repair:
restate the check as a derived enumeration with a per-item assertion, and treat an
empty result as a broken instrument rather than a clean run.

**`proof-covers-the-wrong-thing`** - the harder sibling. Here the evidence WAS
gathered and the argument IS sound, and it is about something adjacent to what you
changed: the wrong surface, the wrong reader, a different caller than the code, or a
premise nobody tested. It reads exactly like a proof, because it is one - of a
different claim.

**`destructive-sweeps-lean-to-keep`** - a destructive operation acts only on what it
can positively prove is disposable. Enumerate what to DELETE, never what to skip: an
exclusion list fails open for every shape nobody thought to list, and a false delete
is unrecoverable while a false keep is merely disk.

## Where they came from

They were written from post-mortems, not from first principles. Each one is a defect
that was hit repeatedly by a fleet of coding agents running unattended, in different
media each time, until somebody put the instances side by side and recognised them as
one shape. The war stories are still in the skill bodies, because the shape is easier
to recognise from the examples than from the rule.

They are published by [DevThrottle](https://devthrottle.com), which is the tool the
fleet runs on. The skills themselves mention no product and depend on nothing - if
you never look at DevThrottle, they still work.

## Portability

Each skill carries only the fields in the [agentskills.io](https://agentskills.io)
specification - `name`, `description`, `license` - and no host-specific extensions, so the
same files load under any agent that implements that standard, not only Claude Code.
