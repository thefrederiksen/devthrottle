## Problem / Value

The phone app currently calls `session.TailnetEndpoint` (the owning Director's direct URL) when submitting and polling voice turns. This must change to the Gateway URL once the Gateway async endpoints are live (issue #NNN). The phone should never address individual Directors directly for voice: the Gateway is the stable, always-available entry point, and its address does not change when a session moves between Directors.

## Scope

**In:**
- `phone/CcDirectorClient/TalkPage.xaml.cs` -- `RunVoiceTurnAsync`: change `SubmitVoiceTurnAsync` and `PollVoiceTurnAsync` base URL from `session.TailnetEndpoint` to the Gateway URL
- `phone/CcDirectorClient/Voice/VoiceConversation.cs` -- `SpeakTurnAsync`: same URL swap
- `phone/CcDirectorClient/Voice/DirectorVoiceClient.cs` -- `NewClient()` or equivalent: ensure the Gateway URL is used as the `HttpClient.BaseAddress` for submit/poll calls
- Rebuild phone app: `dotnet build` (MAUI/Android)
- Deploy to test device via `scripts/deploy-phone.ps1`
- Live end-to-end verification: record voice, submit to Gateway, poll to completion, hear reply

**Out:**
- Gateway endpoint implementation (issue #NNN -- must be merged first)
- Director async endpoint removal (issue #NNN)
- Any changes to the Director's SSE endpoint
- Bug fixes (#366, #367, #368)

## Acceptance Criteria

- [ ] Phone submits to `<gateway-url>/sessions/{sid}/voice-turn/submit` -- verified by Director log showing the request arriving from the Gateway (not directly from the phone's IP)
- [ ] Phone polls `<gateway-url>/sessions/{sid}/voice-turn/{turnId}` -- verified by Gateway log showing repeated GET requests from the phone
- [ ] A complete voice turn succeeds end-to-end on the test device: microphone -> spoken summary plays back on phone
- [ ] `dotnet build` (MAUI Android) succeeds with 0 errors
- [ ] Phone app is deployed and tested on the physical Android device via `scripts/deploy-phone.ps1`

## Affected Containers

- `phone/CcDirectorClient`

## Proof Target

Screenshot of the phone making a successful voice turn, alongside a Gateway log line showing `POST /sessions/{sid}/voice-turn/submit` from the phone's IP -- confirming the request went to the Gateway, not the Director directly.

## Assumptions

- The session DTO received by the phone includes a field carrying the Gateway URL (e.g. `GatewayUrl` or equivalent). If this field is missing from the session DTO the phone receives, this issue must also add it -- flag as a blocker if found.
- The Gateway is reachable from the phone over Tailscale at the same URL the phone already uses for other Gateway calls (session list, wingman, etc.).
- This issue depends on Gateway async endpoints (issue #NNN) being merged and deployed before live testing.
