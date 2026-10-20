# Mission document - Factory Boxes

Status: APPROVED by the owner, 19 October 2026. Written by the Architect seat (session
"cc-consult / 5d95", devlinux), which hands over to the Delivery Lead and shuts down. The owner answered
every question in section 10 through two dev reports - "Factory Boxes"
(gateway.devthrottle.com/r/ea9049ca-2892-4c04-813b-9d7fe06fa0af) and "Factory Boxes Mission"
(gateway.devthrottle.com/r/83a48768-e8f0-4221-aa40-109b0fd1d93c) - taking the Architect's
recommendation on all eight. His first answer was "Yes, that's it. Go ahead and turn it into a
mission." He did not reword the goal in section 3; if a detail of it turns out to matter, ask him -
do not treat his go as a ruling on a detail he was never asked about on its own.

Mission issue: #4258.

Product repository: `thefrederiksen/devthrottle`. Read at `origin/main` 93cd0c4c0 (v2.25.0 merge).
The first thing the Delivery Lead does is check what changed on `origin/main` since then in the files
named in section 5.

---

## 1. The mission

DevThrottle hands out small Linux computers - Factory Boxes - each with a desktop, a browser, Claude
Code and a running Director, and you open one from the Gateway in any browser, on any network, with
nothing to install and no port open anywhere; where the box physically runs does not matter.

## 2. The why

In the owner's words (18-19 October 2026):

> I would like to run small vm so i can allow people to connect to the DevThrottle factory but have
> their own computer [...] when the factory basically needs it, we can spin it up because that way
> there's a lot more security.

> I give them their own computers so they can't do anything bad in our repos or in anything else and
> everything they could do is just isolated to either their own repo or their own local drive.

> I want the gateway route to work without using Tailscale. I want to be able to route through the
> gateway.

> I want DevThrottle to be able to service these, small linux computers with ui just like [Grok's
> product] does. [...] So it just needs a browser, a Linux box and a Cloud Code terminal [...] And it
> should be running DevThrottle director on it. So I can remote into this box and it's basically my
> remote session into a machine I can start using on my factory.

Three reasons. **Security:** a person or a factory job works in a box of its own instead of on a
machine that holds the owner's keys and repos. **Reach:** the owner, testers and later customers use
a full desktop from a phone or any laptop, through the Gateway they already sign into. **Product:**
Managed Factory customers have no hardware of ours; a box that can run anywhere is how the factory
gives them a seat.

## 3. The goal

The Architect's wording; the owner has not yet reworded it.

From a phone on mobile data (not the house network, no Tailscale running on it), signed into the
Gateway as the owner:

1. The owner opens the Cockpit, goes to **Boxes**, presses **New box**, names it, and picks where it
   runs: **devlinux** or **the cloud provider**.
2. Within the time allowed (devlinux: 2 minutes; cloud: 5 minutes) the box shows **Running**, and
   its Director appears in the owner's fleet without anyone having signed in on the box.
3. **Open** shows the box's desktop live in the browser. He can click, type, open Chromium, sign in to
   a site, and see a Claude Code session on the box that he can also drive from the Cockpit like any
   session.
4. He closes the tab, comes back an hour later: the site is still signed in.
5. **Stop** stops the box (it costs nothing on devlinux, and only disk at the provider); **Open** on a
   stopped box starts it again; **Delete** asks for confirmation and removes it.
6. The box cannot reach the house network, the tailnet, or devlinux's own services.

And the failure cases, each with a clear message in the Cockpit, never a blank or a hang: the host
Director offline; the provider refusing (quota, bad credentials, no capacity); the box failing to come
up in time; a second person trying to open a box that is not theirs; the box's Director losing its
connection mid-use.

## 4. Decisions

From the owner, through the dev reports of 18-19 October 2026, in his own words where he wrote them:

- **D1. Reach:** through the Gateway. "I want the gateway route to work without using Tailscale."
  Tailscale is not part of the design.
- **D2. Who creates a box:** "You or the factory, never the user." The owner presses New box, or the
  factory creates one when a seat needs one; a user only ever receives a ready box.
- **D3. Where the first boxes run:** "devlinux first, then one cloud provider." devlinux proves the
  relay; one cloud provider (Hetzner proposed) proves "anywhere".
- **D4. Claude on a box:** "Ours, handed in by the Gateway at start-up [...] the key is never stored in
  the box image. You can see and cap usage for each box."
- **D5. Containers, not VMs, on devlinux:** the owner accepted a locked-down container as the box on
  devlinux (18 October); the network wall is required before anyone but the owner uses one.
- **D6. The USB radio board rule** (owner, 2026-10-07) is unrelated and untouched.

- **D7. Deploy grant (law 18), for this mission only:** "Yes, behind the flag. Every other account sees
  nothing until the QA passes." This mission may deploy the Gateway to production, with Boxes shown
  only to the owner's tenant behind a feature flag.
- **D8. Claude credentials on a box:** "An Anthropic API key for each box, from your Anthropic
  account. The Gateway hands it in when the box starts and revokes it when the box is deleted, with a
  monthly cap per box. Usage is visible per box." Not the owner's subscription login.
- **D9. Spending limit (law 18), for this mission only:** 100 euros a month in total, ask before
  going over. The owner added, in his words: "I dont car about spend so far, just want to prove the
  concept". Read it as: spending is not the thing to economise on - proving the concept is the point -
  but the 100-euro line he chose still stands, and going past it is a question for him.
- **D10. Other people's boxes:** out of this mission; "that's the next mission". This mission ends
  with the owner's own boxes on devlinux and one cloud provider.

## 5. Design

### The shape

```
 browser (anywhere) --HTTPS--> Gateway <==outbound SignalR tunnel== Director on the HOST
                                  |                                   |  (devlinux, or a cloud box host)
                                  |                                   v
                                  |                          local desktop port of the box
                                  +<==outbound tunnel== Director INSIDE the box (joins the fleet)
```

Every arrow into the Gateway starts at the machine. Nothing connects in to a house or a provider.

### What exists (read at 93cd0c4c0)

- The Director holds one outbound SignalR tunnel to `/director-stream`
  (`src/CcDirector.ControlApi/GatewayStreamClient.cs:450-470`, `GatewayHost.cs:4695`).
- A two-way stream already rides it: the Cockpit terminal. Browser WebSocket to
  `GET /sessions/{sid}/stream` (`Api/SessionWsProxyEndpoints.cs:67`), the Gateway sends
  `open-terminal-stream` down (`Api/TunnelStreamLegs.cs:81`), frames come up
  (`Gateway.Contracts/DirectorUpStreamMessages.cs`, 48 KB frames), keystrokes go down as
  `terminal-input`. This is the pattern the desktop relay copies.
- The Gateway's generic HTTP pass-through was **deliberately removed** (`TunnelCatchAllDispatch.cs:11`).
  The relay below is NOT a way back to it.
- A locked-down browser desktop runs on devlinux today as a container: `lscr.io/linuxserver/webtop:ubuntu-xfce`
  (Xfce, Chromium, Selkies streaming over HTTP + WebSocket), all capabilities dropped but six,
  `no-new-privileges`, memory/CPU/pid caps, own volume, public DNS. Proven 18 October; see the
  dev report "Browser Desktops".

### What is built

1. **Desktop relay (Gateway + Director).** The Director keeps a list of the desktops it hosts (box id
   -> `127.0.0.1:<port>`), and only those. A new stream leg relays HTTP requests and WebSocket frames
   for one box between the Gateway and that local port. The Gateway exposes it to the browser under a
   box-scoped route, behind the normal sign-in, only to the box's owner. A box id that the Director
   does not list is refused at the Director, not just the Gateway.
   *Open design point for phase 1's first task:* Selkies may assume it is served at `/`. Either it
   works under a path prefix (`/boxes/{id}/desktop/`), or each box gets its own host name. The first
   task is a spike that settles this with evidence before the leg is built.
2. **Boxes in the Cockpit.** A Boxes screen (list, state, where it runs, New, Open, Stop, Delete) and
   a box view (the desktop, and the box's sessions beside it). Routes in `apps/cockpit/src/routes.tsx`
   (model: `/directors`, lines 173-174), nav in `apps/cockpit/src/AppShell.tsx:112`.
3. **The box image.** The webtop desktop plus the Director (installed at image build; the Director is
   an Avalonia app and runs on the box's own desktop - `src/CcDirector.Avalonia/Program.cs:254`) plus
   Claude Code. No credential of any kind is in the image.
4. **Unattended enrollment.** Today every enrollment needs a person in a browser
   (`HostedEnrollmentEndpoint.cs`, `SignedInEnrollmentEndpoint.cs` - "remote headless Directors are a
   follow-up"). New: when a box is created, the Gateway issues a **one-time, short-lived box token**
   bound to that box id; the box's Director exchanges it on first start for its device key. The token
   is handed to the box at start (environment of the container / provider user-data), is single-use,
   and expires in minutes.
5. **Providers.** One small interface - create, start, stop, delete, status - with two adapters:
   **devlinux** (Docker, driven by devlinux's own Director, so the Gateway reaches it over the tunnel it
   already has) and **one cloud provider** (phase 3). On a cloud provider the box is a small server whose
   own Director dials out; the relay runs through the box's own Director, so no host Director is needed
   there.
6. **Lockdown on devlinux.** The container settings proven on 18 October, plus the network wall: the
   box network reaches the internet only - not 192.168.0.0/16, 10.0.0.0/8, 172.16.0.0/12,
   100.64.0.0/10, nor new connections to the host. Needs one sudo from the owner (iptables
   `DOCKER-USER` + `INPUT`), made persistent.
7. **Claude on the box (D4, D8).** "Included AI" today hands out only DevThrottle's own `dt_live_` key for
   voice and Wingman (`AccountInferenceKeyProvisioner.cs`); Claude sessions use the machine's own
   Claude login and nothing hands an Anthropic credential to a Director. So this is new: one Anthropic
   API key per box, handed in at start (environment, never the image), revoked at delete, capped.
   Phases 1-2 boxes are the owner's own and may be signed in to Claude by hand until phase 3 lands.
8. **Idle stop.** A box with no viewer and no running session for N minutes (default 30) is stopped.

### What is out

- Boxes for people other than the owner (Eric, customers): see question 8. A Director's device key
  today reaches the **whole tenant** (`DeviceEnrollmentEndpoint.cs:36-49`, tenant-checked routes in
  `GatewayEndpoints.cs`), so a box's Director could message or act on the owner's other sessions.
  Fine for the owner's own boxes; not for anyone else's.
- Full VMs on devlinux (KVM is not loaded there; containers suffice for D5).
- GPU acceleration of the desktop stream.
- Billing customers for boxes.

## 6. Phases

**Phase 1 - Desktop through the Gateway (devlinux, existing desktop).** Spike on path vs host name;
the Director's hosted-desktop list; the relay leg; a minimal Boxes screen that lists the desktops a
Director hosts and opens one. Done when the owner's existing `demo-desktop-soren` opens from a phone
on mobile data through the Gateway. Tech Lead: yes (Gateway + Director + Cockpit, three areas).

**Phase 2 - Boxes on devlinux end to end.** The box image; the devlinux provider (create, start,
stop, delete, status); New / Stop / Delete in the Cockpit; unattended enrollment by one-time box
token; idle stop; the network wall. Done when goal steps 1-6 pass on devlinux. Tech Lead: yes. The
image and the enrollment token can run in parallel with the provider and the Cockpit work.

**Phase 3 - One cloud provider and Claude keys.** The cloud adapter; the box as a server whose own
Director carries its desktop; an Anthropic API key per box from the owner's account, handed in at start, revoked at delete, capped
monthly, usage visible per box (D8). Creating keys in the owner's Anthropic account may need the owner
to sign in once or to provide an admin key: ask him for that when phase 3 reaches it, with what exactly
is needed.
Done when goal steps 1-6 pass with "the cloud provider". Tech Lead: yes.

**Phase 4 - QA and record.** The full goal and every failure case from section 3, from a phone on
mobile data, published as a QA report; the record landed.

## 7. The check

What counts as proven: the goal of section 3, run from outside the house network, with screenshots,
plus each failure case producing its message. One success run is not the proof.

Commands an agent runs by itself, without the owner:

- Unit and integration tests of the new legs: `dotnet test` on the Gateway, ControlApi and Contracts
  test projects; Cockpit tests `npm test` in `apps/cockpit` (incl. `routes.test.tsx`).
- **Relay check (phase 1+):** from a machine that is NOT on the house network or the tailnet (a cloud
  shell, or a phone screenshot), `GET https://gateway.devthrottle.com/<box route>` signed in returns
  the desktop page, its WebSocket upgrades, and a screenshot shows the desktop. The same request
  signed out, or as another account, is refused.
- **Allow-list check:** asking the Director to relay a port or box id it does not host is refused, and
  the refusal is logged.
- **Network wall check:** from inside a box, `curl` to 192.168.1.1, a tailnet address, and devlinux's
  host ports all fail; `curl https://example.com` succeeds. (Baseline on 18 October without the wall:
  router 401, Soren North 404, host 200 - all reachable.)
- **Lifecycle check:** create, open, stop, open-starts-it, delete, each via the Cockpit API, timed
  against the limits in section 3.

## 8. Merge plan

Merge each task to `main` as it passes review, behind a feature flag that shows Boxes only to the
owner's tenant. Safe because the relay leg is new code beside the terminal leg, not a change to it,
and the flag keeps the screen hidden from every other account until phase 4.

## 9. Where it ends

Merged to `main`, and **deployed to the live Gateway** behind the flag (the goal can only be proven
through the real Gateway; there is no staging slot since 2026-09-20 - `docs/deploy/hosted-gateway-ci.md:19`).
The owner granted that deploy for this mission (D7).

## 10. Questions

Answered (owner, dev report "Factory Boxes", 19 October 2026):

1. Is this what you are asking for? **Yes** - "Go ahead and turn it into a mission."
2. Who creates a box? **The owner or the factory, never the user.**
3. Where do the first boxes run? **devlinux first, then one cloud provider.**
4. Whose Claude account? **Ours, handed in by the Gateway at start-up.**

Answered (owner, dev report "Factory Boxes Mission", 19 October 2026) - all as recommended:

5. **Deploy grant.** ANSWERED: yes, behind the flag (D7). May this mission deploy the Gateway to production, behind a flag that shows
   Boxes only to the owner's tenant? *Recommended: yes* - there is no staging slot, and the goal can
   only be proven through the real Gateway.
6. **Claude credentials on a box.** ANSWERED: an API key per box (D8). *Recommended: an Anthropic API key per box from the owner's
   Anthropic Console, handed in by the Gateway at start and revoked at delete, with a monthly cap per
   box* - not the owner's Claude subscription login: a subscription is for one person's own use, cannot
   be capped per box, and would be shared across machines and later customers. Phase 1 and 2 boxes
   are the owner's own, so he may sign Claude in by hand on them until phase 3 lands.
7. **Spending limit** ANSWERED: 100 euros a month, plus the owner's comment (D9). For the mission (cloud servers + Claude API on boxes). *Recommended: 100 euros a
   month in total, stop and ask beyond it.*
8. **Other people's boxes.** ANSWERED: next mission (D10). *Recommended: out of this mission.* The device key reaches the whole
   tenant; giving Eric or a customer a box safely needs a box-only key or their own tenant, which is the
   next mission. This one ends with the owner's own boxes on devlinux and one cloud provider.
