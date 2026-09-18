# How a mission runs

A mission is a body of work too big for one session and too long for one sitting. This document is
the shape of one run: its five steps, who sits in each, who reviews it, and what proves each one
done.

**The rules are not in here.** What each seat is and how it works - what an Architect settles, what
a Delivery Lead drives, what a Tech Lead checks, what a Developer may and may not do, and the laws
that bind all four - is the DevThrottle Method. Fetch it before you start:

    cc-devthrottle skill get devthrottle-method

## The five steps

| Step | Doer | Reviewer | Done when |
|---|---|---|---|
| Settle the design | Architect | none | The mission document exists with its required sections, the why and the goal are stated, and the owner has said go. |
| Drive | Delivery Lead | none | Every phase is merged and the mission's own check passes. |
| Build | Developer | Tech Lead, or the Delivery Lead when there is no Tech Lead | A merged pull request with its proof. Committed and pushed is still in progress. |
| Land the record | Delivery Lead | none | The mission's record is merged to the main branch. |
| Report | Delivery Lead | none | The owner has one page to read. |

## Where the human is bothered

**Once, at the report, from the Delivery Lead.** There is no per-phase approval and no
per-pull-request approval. The exception is never guessing: something genuinely undecidable goes to
him when it turns up, with a recommendation.

## One worktree per workstream

One worktree per concurrent workstream, cut from `origin/main`, never the shared checkout. Two
workstreams never share a tree.

## Messages

- A session may message only the session that started it and the sessions it started, at most six an
  hour.
- Messages queue. Read them with `cc-devthrottle message inbox`.
- Nobody waits for an answer: ask with `cc-devthrottle message send --reply-wanted`, answer with
  `cc-devthrottle message reply`.
- Never broadcast to the whole fleet.
