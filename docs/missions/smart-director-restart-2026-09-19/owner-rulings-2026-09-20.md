# The owner's rulings of 20 September 2026

Made after he ran the feature himself on the rig and photographed the restart offer. They win over the
mission document and over any mandate where they differ. Recorded here by the Delivery Lead (session
150) so that a later seat cannot quietly reverse them.

## 1. The restart offer appears ONCE

His words:

> I think as soon as the restart history as soon as we have used a restart It should no longer be
> offered on startup We can keep the history around for 7 days But then the user would have to go to
> a menu in the file system and saying open from old restart So this means that automatically we
> should only see this restart message once if we use it The user should also be able to clear it if
> they don't want it right it could be that they shut down but they don't want to use it and they
> don't want to see it on every upstart

What it settled, built in pull request 3248:

- A record stops being offered the moment anything has been brought back or reopened from it - NOT
  "once every seat is dealt with", which is what the Delivery Lead had specified in error. Seats that
  were never touched stay reachable in Restart history. That consequence is deliberate.
- A third action clears the offer for good without bringing anything back and without deleting
  anything: "Don't ask again".
- "Not now" keeps its meaning: ask me again next start.

## 2. The File menu item keeps its name

Asked whether "Restart history..." should be renamed to something closer to his own words ("open from
old restart"), he chose to leave it as **"Restart history..."**, with the reason given: it also covers
the shutdowns that were never restarted from, which "old restart" does not describe.

## 3. Nothing is ever deleted

Asked whether "keep the history around for 7 days" meant deleting records older than seven days, he
ruled **never delete**: after seven days a record stops offering itself at start-up, and it stays in
Restart history for good.

So the seven day rule is about what is OFFERED, never about what is KEPT. No seat on this mission
deletes a record, and a later mission that wants to should ask him again rather than read this as
permission.

## 4. Where the demonstration happens

On his own machine, when he says he is free, with him watching - not unattended, and not in a virtual
machine. Unattended runs never touch his screen: they go through the command line door instead of
clicking the window.
