# Terminal state event contract (#4381)

Adapters submit facts; shared core resolves activity. The Director stamps the process with
`CC_STATE_EVENT_DIR` and `CC_STATE_EVENT_TOKEN`. The directory is
`<pointer-directory>/state-events/<session-guid>.<32-character-token>`. Each event is one atomic
JSON file named `<sequence>.state.json` inside that directory. The token is already represented in
the directory capability; the separate token variable is available to adapters that validate their
launch context.

```json
{
  "version": 1,
  "eventId": "2a25e77d-2a10-4f65-aecf-43df2bce32a4",
  "sessionId": "d48526d6-9d7f-4f23-a5dc-12bbd73fb86d",
  "sequence": 1,
  "occurredAtUtc": "2026-10-23T12:00:00Z",
  "event": "turn_started",
  "source": "agent",
  "agent": "claude",
  "detail": null,
  "cliVersion": "2.1.302"
}
```

`eventId` and `sessionId` are GUIDs. `sequence` is a positive, strictly increasing integer per
session across every emitter. An adapter with multiple processes obtains the next value from one
counter file inside `CC_STATE_EVENT_DIR`, updating that counter under a cross-process file lock before
writing its event. A hook process must not restart the sequence at one. `occurredAtUtc` is UTC ISO-8601.
`source` is `agent`; `agent` is one
of `claude`, `codex`, `pi`. `detail` is optional, bounded text. Event vocabulary:

| Event | Resolved fact |
|---|---|
| `turn_started` | Agent is working |
| `turn_ended` | Agent stopped and is waiting for input |
| `permission_requested` | Agent is waiting for permission |
| `menu_opened` | A user menu is open |
| `menu_closed` | Menu closed; agent is waiting for input |
| `unsupported` | Diagnostic only. The adapter received an event or notification type it does not support; session state does not change. |

`cliVersion` is the command-line program version reported by the adapter. For `unsupported`, `detail`
is required and contains the raw event or notification type, and `cliVersion` is required. The Director
retains and logs the diagnostic beside state disagreements so both the harness and a person can see it.
An `unsupported` diagnostic participates in sequence and duplicate validation but never resolves activity.

Malformed JSON, unsupported versions, unknown vocabulary, mismatched session or agent, mismatched
capability token, duplicate/out-of-order sequence, events over 30 seconds old or over 5 seconds in
the future, oversized detail, or an `unsupported` diagnostic without both its raw type and command-line
program version are rejected and logged. Rejected drops are removed. Event files
are written atomically (temporary sibling then rename); one event per sequence-named file avoids
overwriting a faster event. The temporary name must not end in `.state.json`, so intake cannot mistake
a partial write for a complete event.

Merge priority is agent facts, then core screen facts, then the Wingman judge only for the meaning
of an already detected unclear stop, then the silence timer as last resort. A lower-priority fact
cannot replace a newer, still-current higher-priority fact. An agent-selected `Working` or `MenuOpen`
fact stops being current when a later positive idle-composer screen fact shows a recognised composer
and no working marker. This covers an interrupt, error stop, or answered menu for which the adapter
emits no closing event; it resolves `WaitingForInput` with `screen` as the deciding source. A composer
beneath a working marker is not idle evidence and cannot end the agent fact. Disagreements are
recorded with both source, state/fact, timestamp, and the selected source. State snapshots and changes
expose the winning source; a silence-timer result is marked as fallback. The Wingman source is
reserved for the unclear-stop interpretation and is not wired to a producer yet.
