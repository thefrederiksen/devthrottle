# Factory Boxes, phase 1 - the hosted-desktop list (the contract)

Mission issue: #4258. This is what the desktop relay Developer and the Cockpit Developer build against. Every
name and shape below is pinned by a test; the test is named beside it.

## 1. The Director's configuration file

**Where:** `<shared root>/instances/<instance>/config/director/hosted-desktops.json` - for the default instance,
`<shared root>/instances/default/config/director/hosted-desktops.json`. `CcStorage.HostedDesktopsFile()` is
`config/director/hosted-desktops.json` under the storage root, but at start the Director sets `CC_DIRECTOR_ROOT` to its
instance home, `<shared root>/instances/<instance>` (`Program.cs`, `InstanceContext.InstanceHome`), so that is the root
it reads under. The shared root is `CC_DIRECTOR_ROOT` as the Director was started with it, or the platform default
(`~/.local/share/cc-director` on Linux). A file at `<shared root>/config/director/` is never read. (Corrected 20 October
2026; it said `config/director/hosted-desktops.json` under the storage root, which a test Director on a scratch root
read as `<root>/config/director/` - see `test-director-on-devlinux.md`.) Absent on a Director that hosts no desktop,
which is nearly every Director - absent is not an error.

**An absent file logs nothing today** - nor does a file in the wrong folder, which looks the same to the Director:
`HostedDesktopRegistry.Reload` writes a line only when the list changes, and it starts empty, so there is no
`[HostedDesktopRegistry]` line at all. A known gap for phase 2: the only sign the file was found is the line
`Reload: this Director hosts N desktop(s): ...`.

**Format:**

```json
{
  "desktops": [
    { "boxId": "demo-desktop-soren", "displayName": "Soren's demo desktop", "port": 3001 }
  ]
}
```

- `boxId`: 1 to 63 characters, lowercase letters, digits and hyphens, starting and ending with a letter or digit
  (`BoxIdRule` in `CcDirector.Gateway.Contracts`, the one rule the Director and the Gateway both read). It can never
  hold a dot, a colon or a slash, so it can never be read as a host, an address, a port or a path.
- `displayName`: required, 1 to 100 characters after trimming.
- `port`: a number, 1 to 65535. The desktop is always reached on `127.0.0.1`; there is no host field and there never
  will be one.
- Comments and trailing commas are allowed.

**An invalid file is refused whole, loudly.** A bad box id, a port out of range or written as text, a missing display
name, a box id or a port listed twice, or any field the format does not name (`host`, `url`, `address`, anything) -
the whole file is refused, the reason is logged as
`[HostedDesktopRegistry] Reload: the hosted-desktop list was REFUSED, so this Director hosts no desktop until it is fixed: <reason>`,
and the Director hosts **no** desktop until the file is fixed. An entry is never skipped silently and an older list is
never kept. Tests: `HostedDesktopRegistryTests` (Core.UnitTests).

**When it is read:** at Director start, and again on every tunnel reseed (the first connect, every reconnect, and the
re-push every ten seconds). An edit to the file therefore reaches the Gateway within ten seconds, with no restart.

## 2. The lookup the relay must call

```csharp
// CcDirector.Core.Boxes.HostedDesktopRegistry, held by the Director as ControlApiHost.HostedDesktops
HostedDesktopResolution Resolve(string? boxId, string requester);

// HostedDesktopResolution
bool    Allowed   // true only for a box id this Director lists
string? BoxId     // the box id that resolved, null on a refusal
int     Port      // the local port on 127.0.0.1 (HostedDesktopRegistry.LoopbackAddress), 0 on a refusal
string? Refusal   // why, in a sentence, null when allowed
```

- **This is the only way to get a port.** The registry keeps ports private; `Desktops` hands out box id and display
  name only, and that is all that goes up the tunnel. The relay leg must take the box id from the Gateway's request and
  call `Resolve` - it must never accept a port or a host from the Gateway.
- **Refused at the Director:** a box id this Director does not list; anything that is not a valid box id - a bare port
  (`3001`), an address (`127.0.0.1`, `192.168.1.1`), a host and port (`localhost:3001`, `demo:3001`), a path - even when
  it is the port of a desktop it does host; a different case (`Demo-Desktop-Soren`); and every box id while the file is
  refused.
- **Every refusal is logged by `Resolve` itself**, so the relay does not have to remember to:
  `[HostedDesktopRegistry] Resolve REFUSED: box=<box id>, requester=<requester>: <reason>`. The box id is cleaned of
  control characters and cut to 80 characters before it is logged. Pass as `requester` what identifies the asker, for
  example the stream id and the account's `ToLogString()`.
- An allowed lookup logs `[HostedDesktopRegistry] Resolve: box=<box id> -> 127.0.0.1:<port>, requester=<requester>`.

## 3. The push message (Director to Gateway)

Hub method on `/director-stream`, invoked by the Director after the session roster on every reseed:

```
PushHostedDesktops(long sequence, HostedDesktopDto[] desktops)

HostedDesktopDto { string BoxId; string DisplayName; }      // CcDirector.Gateway.Contracts - no port, ever
```

- The whole list every time; an empty list is sent too, so a desktop removed from the file leaves the Gateway.
- Sent only when the Gateway's Hello answer lists `PushHostedDesktops` in its hub methods (an older Gateway is never
  sent it). A failure is logged and never fails the reseed. Tests: `HostedDesktopPushTests` (Avalonia.Tests).
- The account comes from the connection's Hello binding, never from the payload.

## 4. What the Gateway keeps

`CcDirector.Gateway.Streaming.PushedHostedDesktopStore` (`GatewayHost.PushedHostedDesktops`), in memory, account first,
then Director:

- Only the Director's current stream connection may push; a superseded connection never takes it back; a push must
  carry a higher sequence than the last accepted one (the `PushedRepositoryStore` discipline).
- **Dropped the moment the Director's current connection goes.** A desktop is only reachable through its Director's
  tunnel, so the list lives exactly as long as the tunnel.
- Every entry is checked again against `BoxIdRule`; a bad entry is refused and the rest kept.
- **A box id is unique within an account.** A second Director in the same account pushing a box id another connected
  Director holds has that one box id refused and logged
  (`[PushedHostedDesktopStore] box REFUSED: ... Director <id> in this account already hosts a desktop with this box id ...`);
  the holder keeps it until it stops listing it or disconnects, and the second Director's next push then claims it.
  Two accounts may use the same box id.
- **For the relay leg:** `LocatedHostedDesktop? TryLocate(TenantId tenant, string? boxId)` answers which of the
  account's connected Directors hosts the box: `BoxId`, `DisplayName`, `DirectorId`, `DirectorName`, `MachineName`.
  It answers null for another account's box and for anything that is not a valid box id.

Tests: `PushedHostedDesktopStoreTests`, `PushHostedDesktopsHubTests` (Gateway.UnitTests).

## 5. The flag

- **Setting:** environment variable `DEVTHROTTLE_BOXES_TENANT_IDS` on the Gateway - account (tenant) ids, separated by
  commas or semicolons, compared exactly. Unset or empty: Boxes is off for every account, which is the default.
  (`CcDirector.Gateway.Boxes.BoxesSwitch`; the startup log line says how many accounts it is on for and never names one.)
  The Gateway reads its settings from environment variables (the `StaffAccess` pattern), not from an `appsettings`
  section, which is why it is not `Boxes:EnabledTenantIds`.
- **The gate:** every Boxes route is mapped inside `BoxesGate.Group(...)`. For an account the flag is off for, or a
  request bound to no account, the gate answers **404 with no body**, exactly as an unmapped route does. The relay
  leg's Boxes routes - its one-time ticket endpoint and the desktop routes - must be mapped into the same group.
- **What the Cockpit reads:** `GET /account/status` carries `"boxes": true | false` - the Gateway's ruling for the
  calling account. The Cockpit shows the Boxes screen only when it is true, and never decides by itself.

Tests: `BoxesSwitchTests`, `BoxesEndpointTests` (Gateway.UnitTests).

## 6. GET /boxes

Normal sign-in, inside the gate. The desktops hosted by the calling account's connected Directors, ordered by box id:

```json
{
  "count": 1,
  "boxes": [
    {
      "boxId": "demo-desktop-soren",
      "displayName": "Soren's demo desktop",
      "directorId": "4f6c2a3e-0000-0000-0000-000000000000",
      "directorName": "devlinux slot 1",
      "machineName": "devlinux",
      "state": "Online",
      "relativeDesktopPath": "/boxes/demo-desktop-soren/desktop/"
    }
  ]
}
```

- `state` is decided by the Gateway. Phase 1 has exactly one value, `"Online"` (its Director is connected), because a
  list is only held while its Director is connected. Later phases add values here; the Cockpit renders the string.
- `relativeDesktopPath` is the desktop's path RELATIVE to the desktop host, `/boxes/{boxId}/desktop/`. It is **not** an
  address to navigate to. The desktop is served on a separate desktop host (a Gateway setting; in production
  `devthrottle-gw.azurewebsites.net`), and **the Cockpit opens a box through the relay leg's one-time ticket endpoint**,
  which answers the full address to open. The spike (#4261, `phase1-spike-path-prefix.md`) confirmed the desktop works
  under this prefix with a prefix-stripping relay.
- `directorName` is the Director's display name from its Hello, empty when it has none.
- An empty list is `{"count":0,"boxes":[]}`.
- Signed out: refused (401 from the authentication middleware; the gate answers 404 behind it). An account without
  the flag: 404, no body. Another account's boxes never appear. The 401 is proven with the production authentication
  middleware in front of the gate (`BoxesAuthenticationTests`).

Test: `BoxesEndpointTests.GetBoxes_FlaggedAccount_GetsItsListInTheContractShape` pins the exact JSON.

## 7. Turning it on for the owner

1. Put the owner's account id in `DEVTHROTTLE_BOXES_TENANT_IDS` on the hosted Gateway (deploy through the
   `deploy-hosted-gateway` skill only).
2. On devlinux, write `instances/<instance>/config/director/hosted-desktops.json` under the shared root of the
   Director that hosts `demo-desktop-soren`, with the desktop's local port.
3. Within ten seconds `GET /boxes` lists it.

## Developer's answers

The two reviews are in this folder, unchanged: `reviews/phase1-list-review.md` and
`reviews/phase1-list-review-codex.md`.

**Codex, finding 1** (`BoxesEndpointTests.cs:129`: the signed-out test runs behind a fake identity and never the
production authentication middleware, so it would still pass if authentication stopped protecting `/boxes`).
**Accepted and fixed.** The new `Boxes/BoxesAuthenticationTests.cs` boots the real `AuthMiddleware.Run`, configured as a
hosted Gateway configures it, in front of the Boxes gate on an in-memory test server, over a hosted device registry in
a throwaway database file. No real port. It proves three things: a signed-out request gets 401, a request with an
unknown key gets 401, and a real browser key of a flagged account passes authentication and gets its list. The third
proves the 401 is authentication's answer and not a pipeline that answers nothing. With the middleware line taken
out, all three fail (404 instead), so the tests would catch the regression the review describes. The old test is
renamed `GetBoxes_NoAccountBound_GateAnswers404WithNoBody`, because what it proves is the gate's second line, not the
authentication refusal.

**Fable** had no findings. Its three observations are for the relay leg and the Tech Lead, and nothing in this change
answers them: the relay's refusal log should clean the box id in the path, a hub built by hand without the store would
advertise `PushHostedDesktops`, and the gate was run on Linux by hand.
