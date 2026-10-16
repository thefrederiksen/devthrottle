# Mobile session truth

Status: active; both slices are deployed and the roster is verified in production. The final live
Wingman check is blocked because the supplied `testing pi` session closed before deployment finished.

## Why

The mobile app must tell the owner what a session is actually doing. Wingman currently can repeat an obsolete answer after a newer request has failed in the terminal, and the main roster card omits the agent tool that is running. The finished experience narrates the current terminal failure and shows the agent tool on every roster card.

## Decisions

- A newer terminal failure outranks an older completed reply for Wingman narration.
- The roster displays the agent tool, such as Pi or Codex, independently from the selected model.
- Existing session cards keep their current hierarchy; the tool is added as compact metadata.
- The supplied `testing pi` session remains unchanged during verification.
- The Gateway, Cockpit, and mobile app deploy together through the hosted Gateway workflow.

## Work

1. Land the Wingman terminal-failure narration fix and its regression tests.
2. Add the agent tool to the main mobile roster card and prove the value travels from the live session contract to the rendered card.
3. Obtain an adversarial inspection from a different agent family for each slice.
4. Merge the reviewed slices, deploy through the hosted Gateway workflow, and verify both behaviours against the live mobile app.
5. Land this mission record and report the durable evidence.

## Out of scope

- Changing a session's agent tool or model.
- Repairing the provider credential that caused the existing Pi terminal failure.
- Redesigning the full roster card.

## Conduct

Follow `cc-devthrottle workflow instructions mission --version 14`.
