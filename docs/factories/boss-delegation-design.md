# A Boss can really delegate - design

The owner's test: a Boss hands work to the right seat or to a session of its own, that session has what it needs,
it comes back to the Boss when finished, and the Boss stays free to talk.

## The one new ability

A Boss session may start another seat of its own factory:

    cc-devthrottle factory seat start --seat <seat> --name "<what it does>" --prompt "<the whole job>"

This calls a new route, `POST /gateway/factory/seats/{seat}/start`, reachable with a session key. The factory is
read from the caller's own record and is never named in the body.

The session it starts is the seat as a schedule would start it. `FactorySeatStart.Build` builds it from the PUBLISHED
definition: the seat's agent, model, machine and repository, and a full seat kit with its brief, grants, files, skills
and secret names. This is what closes the rules-only limit: work that needs a seat's grants goes to that seat. A child
of a Boss stays rules-only and stays the way to get general work done.

The Boss owns the new session. It finishes with `cc-devthrottle session handback`, which arrives as one doorbell line,
and the Boss carries on in the meantime. The Gateway adds a standing paragraph to the prompt that tells the seat so.

## Why a route and not a new arm of the spawn doors

The seat's machine comes from the definition, and it may not be the Boss's machine. The spawn doors go to a Director
the caller chose, and they accept agent, model, folder and kit from the body. The route accepts only a seat, a name
and a prompt, so there is nothing else for a Boss to set, and the Gateway picks the Director as the talk does.

## The limits

- **Inside the factory.** The factory is the caller's recorded one. Nothing names another.
- **Only a real Boss.** The caller must hold a seat that is a Boss in the published definition
  (`FactorySeatStart.IsABoss`) and must not have inherited it. A Boss's child carries the Boss seat with a rules-only
  kit and is refused, as it is for a publish. So is a seat that is no Boss, a session with no seat, and a Boss that
  was started rules-only at the spawn door, because it was given no grants to hand on.
- **No wider authority.** A Boss chooses no agent, model, machine, kit, grant or version. It can start only what the
  owner's published definition already says. It cannot start a Boss seat, itself included, so it cannot make a second
  Boss with publish rights. The new session's children inherit that seat's rules, as any seat's do.
- **Archived factories, and the factory switch.** An archived factory starts nothing. The route is mapped only while
  the factory agents switch is on.
- **Recorded twice.** The session's history row holds the seat, the version, the kit, and the Boss as parent and owner.
  A row in the factory activity record says which Boss session started which seat, in which session, under which
  name.

## What stays as it is

The Boss's publish rights do not change. A Boss may already change every seat but its own on its own authority (the
owner, 21 October 2026): its agent, model, machine, repository, grants and secret names, and a schedule for it. So it
may publish such a change and then start that seat. Both steps are recorded. "What the published definition says" is
therefore a limit on the start, not on the Boss, and this change adds no new path to any of those.

The refusal of a Boss seat is checked at the start. A Boss could start a seat and then publish a version that gives it
the role Boss; the running session would then pass the check by role and could start seats. It still could not publish,
because a publish matches `bossSeat` alone. The owner's approval guards that field, so there is still one holder of the
right to publish.

## The other limits

- **The Boss has ended when a seat finishes.** The seat is told to close itself after its handback, so a finished seat
  does not go red for the owner. The handback waits unread, and nothing starts a new Boss to read it. A scheduled Boss
  is told to wait for what it started, so this is the case of a Boss that was stopped.
- **A scheduled Boss still open at the end of its shift** is still recorded as a problem. This is unchanged: the rule
  is what catches a hung run. Work longer than a shift belongs in a talk, or in a seat's own schedule.
- **A permission prompt in a seat the Boss started** still stops it until someone reads its screen. This is unchanged
  and it is the owner's decision. A scheduled run of the same seat never prompts (the owner, 26 October 2026), so the
  recommendation is that a started seat is treated the same way: same seat, same limits, no owner watching. It needs a
  marker on the start that survives to the Director, because the Director decides this from the session's origin.

The proof is in [boss-delegation-proof/README.md](boss-delegation-proof/README.md).
