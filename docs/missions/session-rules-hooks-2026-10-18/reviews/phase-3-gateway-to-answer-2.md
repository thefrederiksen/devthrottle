# For Developer A: second list, from the pages review (`reviews/phase-3-pages-review.md`)

Merge `mission/session-rules-hooks-p3` again (the plan has Amendment 2). Answer these in the same
`reviews/phase-3-gateway-answers.md`, with tests:

1. Pages finding 2: `overrodeLabel` is blank whenever a firing overrode nothing, hook or screen.
2. Pages finding 3: every rule reference carries `deleted` (true for "(deleted rule <id>)").
3. Pages finding 4: `GET /gateway/rules/<not a guid>/page` answers the same 404 `{ "error" }` sentence as an
   unknown id, not the Cockpit's index page.
