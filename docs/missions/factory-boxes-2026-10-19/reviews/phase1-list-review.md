# Review - Factory Boxes phase 1, the hosted-desktop list and GET /boxes (pull request #4271)

Mission: Factory Boxes (issue #4258), phase 1, item 1. Reviewer seat, 20 October 2026, on devlinux.
Reviewed at branch `boxes/phase1-hosted-desktop-list`, head `c4325802f`, against `origin/main` `91fa80891`.

## Scope

What I read:

- The mission document (`docs/missions/factory-boxes-2026-10-19/mission.md` on `origin/main`): section 5 item 1,
  and section 7's relay check and allow-list check.
- The spike (`phase1-spike-path-prefix.md` on `origin/main`), in particular "What the relay leg must do".
- The contract this change ships (`phase1-hosted-desktop-list.md` on the branch), every section.
- The whole diff, `origin/main...origin/boxes/phase1-hosted-desktop-list`, 22 files: the Director side
  (`HostedDesktopRegistry`, `CcStorage.HostedDesktopsFile`, `ControlApiHost`, `GatewayStreamClient`,
  `DirectorHubCalls`), the shared contract (`HostedDesktopDtos`: `HostedDesktopDto` and `BoxIdRule`), and the Gateway
  side (`PushedHostedDesktopStore`, `DirectorHub.PushHostedDesktops` and its Hello and disconnect hooks,
  `BoxesSwitch`, `BoxesGate`, `BoxesEndpoints`, `AccountStatusEndpoint`, `GatewayHost` wiring).
- Code on `origin/main` the change depends on but does not edit: `AuthMiddleware` (which paths are public),
  `HostedTenantBoundary.ResolveRequestTenant`, `GatewayEndpoints.ResolveReadTenant`, `TenantId`, the hub's
  capability advertisement (`DirectorHub.CapabilitiesFor` and the reflection that builds `HubMethods`), the reseed body
  and re-push tick in `GatewayStreamClient`, and `MailDeskGate` which the Boxes gate copies.
- All five new test files, and the repository's project instructions file and `docs/CodingStyle.md`.

What I ran, in my own worktree (`../devthrottle-review-list`, .NET 10, Debug, Linux):

| Project | Filter | Result |
|---|---|---|
| Core.UnitTests | `FullyQualifiedName~Boxes` | 48 passed, 0 failed |
| Avalonia.Tests | `FullyQualifiedName~Boxes` | 8 passed, 0 failed |
| Gateway.UnitTests | `FullyQualifiedName~Boxes` | 47 passed, 0 failed |

Also checked: the branch is nine commits behind `origin/main`; the only file both sides touch is `GatewayHost.cs`, and a
trial merge (`git merge-tree`) is clean. The nine commits are the API mode mission's close and Factory Definition work,
none of it in the Boxes files.

What I could not reach: a Windows run; the full local gate script (`pwsh` is not on devlinux, as the pull request says);
the parked `Gateway.Tests` beyond the Boxes filter; and anything about the relay leg or the Cockpit screen, which are not in
this pull request. The relay check and the allow-list check of mission section 7 need the relay leg and a phone, so they
are not runnable against this change on its own; what this change contributes to them (the one lookup and its logged
refusals) is covered below.

## The security questions, each answered from the code

**Can anyone other than the box's owner reach a desktop?** Nothing in this change reaches a desktop; it only lists. The
list is held per account (`PushedHostedDesktopStore._byTenant`, keyed by `TenantId`, a record struct compared by value),
and `ListFor` and `TryLocate` read inside one account only. `GET /boxes` resolves the account from the authenticated
device or session identity the authentication middleware placed on the request (`ResolveReadTenant`, which answers null
when there is no boundary), never from the request. Signed out: `/boxes` is in neither `PublicPaths` nor
`IsPublicShellSurfaceRequest`, so the production middleware answers 401 before any route runs; behind it the gate answers
404 for a request bound to no account. The test for the cross-account case (`GetBoxes_AnotherAccountsBoxes_NeverAppear`)
runs the real store under the real gate on an in-memory server with the hosted boundary.

**Can the Director be made to connect to a port or host it does not list?** The port is private to
`HostedDesktopRegistry`; `Desktops` and the pushed `HostedDesktopDto` carry box id and display name only (pinned by
`Desktops_NeverCarryAPort`, which reads the type's properties). `HostedDesktopResolution.Allow` is internal. The target
address is the constant `127.0.0.1`; the file format refuses any field it does not name (`JsonUnmappedMemberHandling.Disallow`
on the serializer options, so it applies to the entry type as well as the file type), so a `host`, `url` or `address` key
refuses the whole file. A port written as text, or out of 1 to 65535, refuses the whole file. Whether the relay leg
actually calls `Resolve` rather than taking a port from the Gateway is the relay review's question, not this one's.

**Does anything bypass the allow-list?** `Resolve` refuses before any lookup when `BoxIdRule.WhyInvalid` says the value
is not a box id: empty, over 63 characters, containing a dot, colon or slash, all digits, or outside
`^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$`. The match is ordinal, so a different case is refused. While the file is refused the
list is empty, so every box id is refused with the file's reason.

**Are refusals logged?** Every refusal goes through `Refuse`, which writes
`[HostedDesktopRegistry] Resolve REFUSED: box=<id>, requester=<who>: <reason>` with both values cleaned of control
characters and cut to 80 characters (`BoxIdRule.ForLog`). The exact line is pinned by
`Resolve_UnknownBoxId_IsRefusedAndTheRefusalIsLoggedWithTheBoxIdAndTheRequester`, and the control-character case by
`Resolve_ControlCharactersInTheAskedId_DoNotReachTheLogLine`. On the Gateway, a refused box id in a push is logged
with the account's short form and the Director id, and a refused push (wrong connection, stale sequence) is logged
or answered with its reason.

**Do tenants stay apart?** On the push side the account comes from the Hello binding (`RequireBoundTenant`), never the
payload, and a push before Hello throws (`PushHostedDesktops_BeforeHello_IsRefused`). The same box id in two accounts is
allowed and located separately (`ApplySnapshot_TheSameBoxIdInTwoAccounts_IsAllowed`). The flag is a set of exact
account ids, off for everyone when unset; the gate and `GET /account/status` ask the same `BoxesSwitch` instance.

## Correctness, leaks and the tests

- Ownership follows `PushedRepositoryStore`: only the connection bound at the latest Hello may push, a superseded
  connection's Hello is ignored by epoch, a sequence must rise within a connection, and the baseline resets when ownership
  moves. A late disconnect from a superseded connection changes nothing (`UnregisterConnection` compares the active
  connection id first). A list is dropped on the current connection's disconnect, which is what the single `Online` state
  rests on. All of this is under one lock, and the lists are a handful of entries every ten seconds.
- The Director pushes the list on every reseed, which is the first connect, every reconnect, and the ten-second re-push
  tick (`ReseedAsync` is what the tick, the `Reconnected` handler and the first connect all call), in its own try/catch
  after the session and repository legs, and only when the Hello answer listed the method. The hub advertises the method
  by reflection over its public methods, and `Capabilities_TheHubAdvertisesPushHostedDesktops` asks the real `Hello` for it.
- A file that cannot be read, cannot be parsed, or fails any rule is replaced by an empty list with the reason kept in
  `LoadError`, and `Reload` never throws, so a mistake in the file cannot take the tunnel down. The lambda that re-reads
  it runs inside the push's try/catch as well.
- No resource is opened and held: the file is read whole and closed, the store is memory only, and the gate and endpoint
  are closures over singletons. The store keeps one small `Entry` per Director ever seen in an account, exactly as the
  repository store does.
- The tests test what they claim. The endpoint tests boot the real gate, the real store and the real account status
  endpoint on an in-memory server over the hosted boundary, with a stand-in for the authentication middleware that sets
  the same request items the real one does. The hub tests drive the real `DirectorHub` against the real store. The
  registry tests hand the registry its text and its log and touch no disk. Nothing waits on a clock, a port or a process.

## Observations that are not findings

These prove no harm in this change. They are written down so the relay leg's Developer and reviewer have them.

1. `BoxesGate` logs `ctx.Request.Path` as received on a refusal (copied from `MailDeskGate`). For `GET /boxes` the path
   is fixed. The relay routes will carry the box id in the path, so when they are mapped into this group the refused
   path should go through `BoxIdRule.ForLog` or the same cleaning, or the relay's own refusal should log the cleaned box
   id instead of the path.
2. `DirectorHub.CapabilitiesFor` removes `PushLicenseUsage`, the approval methods and `PushApiModeEvents` from the
   advertised list when their store is absent, but not `PushHostedDesktops` when `_hostedDesktops` is null. In the
   product it is never null (`GatewayHost` registers the store as a singleton), so a Director is never sent to a method
   that drops its list; only a hand-built hub without the store would advertise it and ignore the push, and that case logs.
3. The pull request's gate was run by hand with `dotnet test`, project by project, on Linux, because the script needs
   `pwsh`. The Boxes tests pass here too. A Windows run of the default gate has not happened; whether that is required
   before merge is the Tech Lead's call under rule 5a.

## Verdict

No finding proves harm: the change does what the contract says, the port never leaves the Director, every refusal is
logged, tenants stay apart, and the 103 new tests pass here. Fit to merge as far as this review reaches; the relay leg
and the Cockpit screen are separate reviews.
