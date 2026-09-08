import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { setVoiceModeAllSessions, type SessionDto } from "@devthrottle/client-core/api/client";
import { getAutoSpeak, queueTouchMs, setAutoSpeak } from "@devthrottle/client-core/voice/queueTouch";
import { useVoiceModeAll } from "@devthrottle/client-core/voice/useVoiceModeAll";
import { getSessionsEnvelope } from "@devthrottle/client-core/fleet/fleetClient";
import { emptyRetentionCache, mergeRosterRetention, type RosterSessionMark } from "@devthrottle/client-core/fleet/rosterRetention";
import { classify, contextLine, deletionReason, dotHex, inDesktopOrder, inWaitingOrder, isWorking, machineCanBeActedOn, needsYouBadgeCount, pendingDeletion, repoLeaf, snoozeCountdown, snoozeExpired } from "@devthrottle/client-core/sessions/ordering";
import { DELIVERY_BADGE_TEXT, hasUndeliveredPrompt, promptDeliveryTitle } from "@devthrottle/client-core/sessions/delivery";
import { applyFilter, filterIsActive, filterSummary, machineName, pruneFilter } from "@devthrottle/client-core/sessions/filter";
import { useDictationStatusFor } from "@devthrottle/client-core/dictation/status";
import { useNow, waitingLabel } from "@devthrottle/client-core/sessions/waiting";
import { supervisionStats } from "@devthrottle/client-core/sessions/supervision";
import { useNow as useSharedNow } from "@devthrottle/client-core/polling/useNow";
import { playClip, playingSid, rowVoiceInputs, stopPlayback, syncVoiceSessions, useVoiceClips } from "@devthrottle/client-core/voice/clips";
import { voiceRowState } from "@devthrottle/client-core/voice/voiceRowState";
import { voiceQueueFor } from "@devthrottle/client-core/voice/voiceQueue";
import { NavDrawer } from "../components/NavDrawer";
import { AccountSwitcher } from "@devthrottle/client-core/auth/AccountSwitcher";
import { RestartRequestsPanel } from "@devthrottle/client-core/restart/RestartRequestsPanel";
import { SessionFilterPanel } from "../components/SessionFilterPanel";
import { useSessionFilter } from "../hooks/useSessionFilter";
import {
  enablePush,
  notificationPermission,
  pushSupported,
  reconcileBadge,
  type EnablePushResult,
} from "@devthrottle/client-core/push/register";

// Home / roster. A "needs you" group first (when any session wants attention), then an "other
// sessions" group with everything that is NOT waiting on you - so a session appears in exactly one
// group and is never listed twice. Both use the live Gateway /sessions data and the shared triage
// ordering. Tapping a row opens the session-detail placeholder bound to that session id.
//
// The roster reads the /sessions ENVELOPE (per-Director reachability), not the flat list, and runs it
// through the keep-and-mark merge (mobile-resilience mission, Phase 2): a session whose owning machine
// is unreachable STAYS on the roster, grayed and marked. It leaves for one of TWO reasons - its Director
// answered ONLINE without it, or the envelope stopped naming that Director at all and the client
// retention horizon then elapsed. This comment claimed the first reason was the only one, which
// contradicted the merge's own contract; see rosterRetention.ts, which owns the rule. The retention cache
// is held in a ref so it survives across polls (and navigating into a session and back) without churning
// React state.
const POLL_INTERVAL_MS = 5000;

// Voice-mode queue flow: how long the roster sits still before auto-speak jumps into the next
// waiting session, and how recently-listened a session must be to be passed over for now (it comes
// around again on a later pass).
//
// THE DELAY IS THE WAY OUT OF THE LOOP (owner, 2026-07-24). Respond and Snooze return to this tab,
// and auto-speak then takes the next session by itself - so this pause is the only moment there is
// to reach the Auto-speak checkbox and stop. At 1.5 seconds it was not enough time to get a thumb
// there, and the queue could not be left. Three seconds costs nothing while the queue is genuinely
// being worked, and it is long enough to tap the box off.
const AUTO_SPEAK_DELAY_MS = 3000;
const AUTO_SPEAK_COOLDOWN_MS = 20000;

/** The roster's two lenses: the full roster, or only the sessions that can speak to you right now. */
type RosterTab = "all" | "voice";

export function Home() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  // Per-session reachability marks from the merge - only unreachable (wobbly/offline) sessions have one.
  const [marks, setMarks] = useState<Map<string, RosterSessionMark>>(() => new Map());
  const retentionCache = useRef(emptyRetentionCache());
  const [error, setError] = useState<string | null>(null);
  // The roster filter (by machine and/or repo) and whether its full-screen panel is open. The filter
  // is persisted across navigations and restarts by the hook; the panel is transient UI state.
  const [filter, setFilter] = useSessionFilter();
  const [showFilter, setShowFilter] = useState(false);
  // The roster's two tabs. "All" is the roster as it has always been; "Voice" is the hands-free view -
  // only the sessions with narration ready to play THIS INSTANT, nothing else. When you are listening
  // rather than reading, a session that is working, executing, or has nothing to say is not a smaller
  // priority, it is noise, so the Voice tab does not rank it down - it leaves it out.
  //
  // Transient by design: it is a lens you pick up for a minute, not a mode you can get stranded in.
  // Persisting it (as the machine/repo filter is persisted) would mean coming back to the app hours
  // later, during an outage, to an empty roster and no memory of why.
  //
  // The one exception is router state: the Voice screen's Respond and Snooze return here with
  // { tab: "voice" } so the voice queue flow lands back on the queue it is worked from, not on All.
  // That state lives only in the navigation that carried it - a fresh open still starts on All.
  const location = useLocation();
  const navigate = useNavigate();
  const [tab, setTab] = useState<RosterTab>(() =>
    (location.state as { tab?: string } | null)?.tab === "voice" ? "voice" : "all",
  );
  // Keep the selected lens on THIS history entry. A row navigation followed by browser/system Back
  // must restore the Voice queue the owner was working, not remount the older default ("All") lens.
  // This remains transient across a fresh app open because it is router history, not durable storage.
  const selectTab = useCallback(
    (next: RosterTab) => {
      setTab(next);
      const current = (location.state as Record<string, unknown> | null) ?? {};
      navigate(location.pathname + location.search, {
        replace: true,
        state: { ...current, tab: next },
      });
    },
    [location.pathname, location.search, location.state, navigate],
  );
  useEffect(() => {
    const historyTab =
      (location.state as { tab?: string } | null)?.tab === "voice" ? "voice" : "all";
    setTab(historyTab);
  }, [location.key, location.state]);
  // Auto-speak (voice-mode queue flow): when checked, the Voice tab jumps into the oldest waiting
  // voice-ready session by itself and reads it aloud - the hands-free queue. Persisted per device.
  const [autoSpeak, setAutoSpeakState] = useState<boolean>(getAutoSpeak);
  const toggleAutoSpeak = () => {
    const next = !autoSpeak;
    setAutoSpeak(next);
    setAutoSpeakState(next);
  };

  // Re-render the roster when a voice clip finishes downloading (a card flips from the yellow
  // working state to the play-triangle the moment its audio is phone-ready).
  useVoiceClips();

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const envelope = await getSessionsEnvelope(signal);
      // Keep-and-mark: retained sessions of unreachable machines stay on the roster, marked. A session
      // leaves when its owning Director answered ONLINE without it, or when the envelope has stopped
      // naming that Director and the client retention horizon elapses (mergeRosterRetention owns both).
      const merged = mergeRosterRetention(retentionCache.current, envelope);
      retentionCache.current = merged.cache;
      setSessions(merged.roster.sessions);
      setMarks(merged.roster.marks);
      setError(null);
      // The app-icon "needs you" dot is counted over the MERGED roster - the same sessions this page
      // renders and builds the voice queue from. It used to be counted from the RAW envelope, and in a
      // wobbly fallback - a connected-but-quiet Director the Gateway names but serves no rows for - the
      // merged roster held the retained, still-reachable card while the envelope held nothing, so the
      // card nagged and could enter the voice queue while the badge was cleared (inspection 3, finding 3).
      // If these three ever read different lists again, that is the bug, not a tuning choice.
      //
      // The badge RULE is unchanged and still lives in client-core: a needs-you session whose machine
      // cannot be acted on stays VISIBLE in the group below and stays OUT of this number, so a laptop
      // asleep overnight with three red sessions does not leave the badge lit until morning.
      void reconcileBadge(needsYouBadgeCount(merged.roster.sessions));
      // The clip sync DOES read the envelope, and that is not the same mistake: it fetches bytes the
      // Gateway is serving this poll. A retained card has no new clip to pull - if its audio was already
      // downloaded it plays, and if it was not, the session is not phone-ready and the voice queue
      // excludes it, so nothing promises playback it cannot deliver.
      // Fire-and-forget; it updates the clip store as bytes land (phone-ready, the issue #850 rule).
      void syncVoiceSessions(envelope.sessions);
    } catch (err) {
      if (signal?.aborted) return;
      // Keep the last-known roster on screen (never clear good data on a bad connection). The global
      // ConnectionBanner - fed by the same failed contact through the shared health signal - is now the
      // single voice for "bad connection, showing last known", so this page no longer shows its own
      // offline strip. The error is kept only to stop the "Loading sessions..." line from lying after a
      // first-load failure.
      setError(err instanceof Error ? err.message : "Failed to load sessions");
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => {
      void load(controller.signal);
    }, POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  // Drop any filter selection whose machine or repo is no longer in the live roster, so a filter pinned
  // to a machine that went away cannot silently hide everything. pruneFilter returns the same reference
  // when nothing changed, so this only writes through (and re-renders) on a real change.
  useEffect(() => {
    if (!sessions) return;
    const pruned = pruneFilter(filter, sessions);
    if (pruned !== filter) setFilter(pruned);
  }, [sessions, filter, setFilter]);

  // The roster narrowed to the active machine/repo filter. Facet lists in the panel and the total
  // count in the app bar come from the FULL roster; only the displayed groups below are narrowed.
  const filtered = sessions ? applyFilter(sessions, filter) : sessions;
  // The Voice tab's roster: exactly the sessions whose row shows a play-triangle, by construction -
  // isVoiceReady IS "voiceRowState === ready", so the tab and the triangle can never disagree about
  // what "ready with voice" means. In waiting order, longest-waiting first, so it can be worked top to
  // bottom by ear.
  //
  // The reachability mark is fed in for the same reason the row feeds it: a retained session from an
  // unreachable machine keeps its last-known voiceAudioReady and its already-downloaded clip, so it
  // would otherwise sit in this tab claiming it can speak. This tab is the hands-free lens - the one
  // place a false "ready" is read out loud rather than looked at - so it must not list a dead machine.
  //
  // Ordered as a first-in-first-out QUEUE (voice-mode queue flow): oldest waiting at the top, new
  // arrivals at the bottom - and a session you listened to but did not respond to or snooze drops to
  // the BOTTOM (the device-local listened-touch counts as re-entering the line), so everything not
  // yet heard is read first and the unhandled one comes around again.
  // Inspection 1, finding 2: the whole selection lives in client-core now. This used to be assembled here,
  // and the reachability argument it assembled asked "is there a retention mark on this row?" - a question
  // about PUSH FRESHNESS, not about whether the machine can be reached - so a wobbly machine with its tunnel
  // UP silently left the queue and the owner stopped being told about work he could still act on. Passing
  // sessions and nothing else means there is no longer an argument here to get wrong.
  const voiceReady = filtered ? voiceQueueFor(filtered) : [];
  // The "Needs you" group is a waiting line: the session that has been waiting for you the longest
  // sits at the top, and a session that only just started needing you drops in at the bottom
  // (inWaitingOrder). This keeps the list from reshuffling under you as sessions change state, and
  // lets you work it top to bottom, dealing with the longest-neglected session first.
  const needsYou = filtered ? inWaitingOrder(filtered) : [];
  // The bottom group is "the rest": every session that is NOT waiting on you, still in your manual
  // desktop order. A needs-you session shows only once, at the top - never duplicated down here.
  const others = filtered ? inDesktopOrder(filtered.filter((s) => classify(s) !== "needsYou")) : [];
  const total = sessions ? sessions.length : 0;
  const shownTotal = filtered ? filtered.length : 0;
  const active = filterIsActive(filter);

  // AUTO-SPEAK (voice-mode queue flow): with the box checked and the Voice tab open, the roster
  // jumps into the next session in the queue BY ITSELF and reads it aloud - hands-free driving mode.
  // The next session is the oldest waiting one not listened to in the last few seconds (a session
  // just left unhandled sits out one beat rather than instantly re-trapping the listener; with
  // nothing else waiting it comes around on a later poll - deliberately, unhandled keeps coming
  // back).
  //
  // The countdown is keyed to the CHOSEN SESSION ID, not to the roster array. The array is a new
  // object on every render (each 5-second poll, and every voice-clip download event), so an effect
  // that depended on it restarted its timer each time - survivable at 1.5 seconds, but a 3-second
  // timer could be reset forever by a busy roster and never fire at all. Keyed to the id, the timer
  // is only ever restarted when the session it is about to open actually changes.
  const autoSpeakNextId =
    tab === "voice" && autoSpeak && !showFilter
      ? voiceReady.find((s) => Date.now() - queueTouchMs(s.sessionId ?? "") > AUTO_SPEAK_COOLDOWN_MS)
          ?.sessionId ?? null
      : null;
  // True only while the countdown is running, so the screen can say out loud that it is about to
  // move and that unchecking the box stops it - a silent pause reads as the app being slow.
  const [autoSpeakArmed, setAutoSpeakArmed] = useState(false);
  useEffect(() => {
    if (autoSpeakNextId === null || autoSpeakNextId.length === 0) {
      setAutoSpeakArmed(false);
      return;
    }
    setAutoSpeakArmed(true);
    const sid = encodeURIComponent(autoSpeakNextId);
    const timer = window.setTimeout(() => {
      setAutoSpeakArmed(false);
      navigate(`/session/${sid}/voice`, {
        state: { voiceMode: true, autoSpeak: true, fromTab: "voice" },
      });
    }, AUTO_SPEAK_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [autoSpeakNextId, navigate]);

  return (
    <div className="screen">
      <header className="app-bar">
        <NavDrawer />
        <h1>DevThrottle</h1>
        {/* Which account's fleet this roster belongs to (devthrottle_internal #1509). Renders NOTHING
            until a second account is enrolled, so a single-login phone sees the bar it always had. It
            is on the roster specifically: this is the screen that shows a list of sessions, and a list
            of the wrong account's sessions is indistinguishable from a list of yours. */}
        <AccountSwitcher onOpen={() => navigate("/account")} />
        {/* The spacer pushes the filter button to the right end of the bar. It used to be two spacers
            with the network status pill centred between them; the pill is gone (see ConnectionBanner). */}
        <div className="app-bar-spacer" />
        {/* The funnel opens the full-screen filter panel and doubles as the "filter active" indicator
            (a dot appears when a machine/repo filter is applied), so it is both the one-tap entry point
            and the status light - no separate menu item needed. */}
        <button
          type="button"
          className={`filter-btn${active ? " filter-btn-on" : ""}`}
          aria-label={active ? "Sessions filtered - edit filter" : "Filter sessions"}
          onClick={() => setShowFilter(true)}
        >
          <span className="filter-funnel" aria-hidden="true" />
        </button>
      </header>

      {/* When a filter is applied, a thin strip under the app bar names it and offers a one-tap Clear,
          so the filter can be seen and removed without reopening the panel. */}
      {active && (
        <div className="filter-strip" role="status">
          <span className="filter-funnel filter-strip-icon" aria-hidden="true" />
          <span className="filter-strip-text">{filterSummary(filter)}</span>
          <span className="filter-strip-count">{shownTotal} of {total}</span>
          <button type="button" className="filter-strip-clear" onClick={() => setFilter({ machines: [], repos: [] })}>
            Clear
          </button>
        </div>
      )}

      {showFilter && sessions !== null && (
        <SessionFilterPanel
          sessions={sessions}
          filter={filter}
          onApply={(next) => {
            setFilter(next);
            setShowFilter(false);
          }}
          onClose={() => setShowFilter(false)}
        />
      )}

      <EnableAlerts />

      {/* The roster's two lenses, in the same segmented control the session screen uses for its own
          views, so "Voice mode" means the same thing and looks the same wherever it appears. */}
      <div className="view-tabs" role="tablist" aria-label="Roster view">
        <button
          type="button"
          role="tab"
          aria-selected={tab === "all"}
          className={`view-tab${tab === "all" ? " active" : ""}`}
          onClick={() => selectTab("all")}
        >
          All
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === "voice"}
          className={`view-tab${tab === "voice" ? " active" : ""}`}
          onClick={() => selectTab("voice")}
        >
          {/* "Voice sessions", NOT "Voice mode" (owner, 2026-07-24). This tab is a LENS - the sessions
              that can speak to you right now. Voice mode is a state of the fleet, and auto-speak is a
              state of this phone; naming the lens after one of them made all three read as the same
              thing, which is exactly the confusion this screen has to stop causing. */}
          Voice sessions
        </button>
      </div>

      {/* "+ New session" entry (issue #812): opens the add-session flow (machine -> repo -> create),
          a faithful translation of the Android NewSessionPanel. Hidden in the Voice tab: starting a new
          session is a typing job, and the Voice tab is for the sessions that can talk right now. */}
      {tab === "all" && (
        <Link className="new-session-entry" to="/new">
          <span className="new-session-plus" aria-hidden="true">+</span>
          New session
        </Link>
      )}

      {/* Car Mode's entry point is the nav drawer (top-left), NOT a banner here. It used to be both.
          The roster is a list of sessions that need you; a permanent full-width call-to-action for a
          different screen sat above that list drawing attention it had not earned - loudest element on
          a page it is not about, and shown just as insistently on an empty roster. It is one line in
          the drawer alongside every other destination, which is what it is. */}

      {sessions === null && error === null && <p className="status-line">Loading sessions...</p>}

      {sessions !== null && total === 0 && (
        <p className="status-line">No sessions running.</p>
      )}

      {/* The one-tap fleet-wide voice switch (issue #1765): "as I leave the house, put my whole fleet on
          voice; when I get home, take it all off". It reads the roster to decide its own action - offer
          "turn all on" while no session is a voice session, "turn all off" once any is - so the same
          button covers both halves of the walk-out / come-home round trip.

          VOICE MODE AND AUTO-SPEAK ARE TWO DIFFERENT THINGS (owner, 2026-07-24), and this is the first
          of them: voice mode is about the FLEET - every session narrates its turns. Auto-speak, below,
          is about THIS PHONE - it also plays them to you one after another without a tap. Voice mode on
          with auto-speak off is the ordinary case: your sessions all speak, and you choose which one to
          listen to. That is why they are two separate controls and never one. */}
      {tab === "voice" && sessions !== null && total > 0 && (
        <VoiceAllControl sessions={sessions} />
      )}

      {/* Auto-speak (voice-mode queue flow): the hands-free switch, and a PHONE setting, not a fleet
          one. One checkbox; when on, the queue below is read to you session by session without a
          single tap on a row. */}
      {tab === "voice" && sessions !== null && total > 0 && (
        <label className="autospeak-row">
          <input type="checkbox" checked={autoSpeak} onChange={toggleAutoSpeak} />
          <span className="autospeak-label">
            Auto-speak
            <span className="autospeak-hint">
              This phone only - open each waiting voice session in turn and play it without a tap
            </span>
          </span>
        </label>
      )}

      {/* The three-second pause before auto-speak takes the next session, said out loud. This is the
          moment the loop can be left, so the screen names the way out rather than just going quiet. */}
      {tab === "voice" && autoSpeakArmed && (
        <p className="autospeak-countdown" role="status">
          Opening the next session in a moment - uncheck Auto-speak to stop.
        </p>
      )}

      {/* Honest feedback while auto-speak idles: the loop is armed, there is just nothing to read. */}
      {tab === "voice" && autoSpeak && sessions !== null && total > 0 && voiceReady.length === 0 && (
        <p className="status-line">Auto-speak is on. Waiting for a session to need you...</p>
      )}

      {/* The Voice tab, empty. Said plainly and without alarm: nothing is ready to listen to yet. The
          way out is one tap, so an empty voice roster is never a dead end. */}
      {tab === "voice" && !autoSpeak && sessions !== null && total > 0 && voiceReady.length === 0 && (
        <p className="status-line">
          Nothing to listen to right now.{" "}
          <button type="button" className="link-btn" onClick={() => selectTab("all")}>
            Show all sessions
          </button>
        </p>
      )}

      {tab === "voice" && voiceReady.length > 0 && (
        <section className="group">
          <h2 className="group-title">Ready to play</h2>
          <ul className="roster">
            {voiceReady.map((s) => (
              <SessionRow key={`voice-${s.sessionId}`} session={s} mark={marks.get(s.sessionId ?? "")} fromTab="voice" />
            ))}
          </ul>
        </section>
      )}

      {/* Sessions exist but the active filter hides them all: say so plainly and offer a way back,
          instead of an empty screen that reads like "no sessions running". */}
      {tab === "all" && sessions !== null && total > 0 && shownTotal === 0 && (
        <p className="status-line">
          No sessions match this filter.{" "}
          <button type="button" className="link-btn" onClick={() => setFilter({ machines: [], repos: [] })}>
            Clear filter
          </button>
        </p>
      )}

      {/* A Director restart a session has asked for (issue #2725): the owner's one accept, at the top of
          the roster where "Needs you" lives. The same shared component the Cockpit mounts; this shell
          only tunes its layout under .screen. Renders nothing while no request exists. */}
      {tab === "all" && <RestartRequestsPanel />}

      {tab === "all" && needsYou.length > 0 && (
        <section className="group">
          <h2 className="group-title group-title-attention">Needs you</h2>
          <ul className="roster">
            {needsYou.map((s) => (
              <SessionRow key={`needs-${s.sessionId}`} session={s} mark={marks.get(s.sessionId ?? "")} />
            ))}
          </ul>
        </section>
      )}

      {tab === "all" && others.length > 0 && (
        <section className="group">
          <h2 className="group-title">Other sessions</h2>
          <ul className="roster">
            {others.map((s) => (
              <SessionRow key={s.sessionId} session={s} mark={marks.get(s.sessionId ?? "")} />
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

// A one-time prompt to turn on the app-icon "needs you" dot. Shown only when the browser can do Web
// Push and the user has not decided yet; tapping requests permission (the gesture iOS/Android require)
// and subscribes.
//
// IT ALWAYS ANSWERS THE TAP. It used to throw the outcome away - it awaited enablePush(), swallowed any
// failure into console.warn, and then re-read Notification.permission to decide what to show. When the
// browser showed no prompt at all (Chrome's quiet permission user interface, or an in-app browser
// window that is not allowed to ask) the permission was unchanged, so this banner re-rendered
// identically and the button looked broken - the exact "I tap it and nothing happens" report. And when
// permission WAS granted but registering the device failed, the banner disappeared as if it had
// worked while no push could ever arrive. Now every outcome enablePush() names is rendered, with the
// sentence that outcome carries (the mobile rule: never fail silently).
function EnableAlerts() {
  const [granted, setGranted] = useState(() => notificationPermission() === "granted");
  const [outcome, setOutcome] = useState<EnablePushResult | null>(null);
  const [busy, setBusy] = useState(false);

  if (!pushSupported() || granted) return null;

  // A standing block is a dead end until the user lifts it in browser settings, so there is no button
  // to offer - just the reason and where to fix it.
  if (notificationPermission() === "denied" || outcome?.state === "blocked") {
    return (
      <div className="banner banner-info" role="status">
        {outcome?.state === "blocked"
          ? outcome.message
          : "Notifications are blocked for DevThrottle. Allow them in your browser's site settings for this site to see the app-icon dot when a session needs you."}
      </div>
    );
  }

  const onEnable = async () => {
    setBusy(true);
    setOutcome(null);
    // enablePush never throws - it returns the outcome, including its failures - so there is nothing
    // here to swallow and no path that leaves the tap unanswered.
    const result = await enablePush();
    setBusy(false);
    setOutcome(result);
    if (result.state === "granted") setGranted(true);
  };

  return (
    <div className="banner banner-info banner-action" role="status">
      <span>
        Get an app-icon dot when a session needs you.
        {outcome !== null && <strong className="banner-note">{outcome.message}</strong>}
      </span>
      <button type="button" className="banner-btn" onClick={() => void onEnable()} disabled={busy}>
        {busy ? "Enabling..." : outcome === null ? "Enable notifications" : "Try again"}
      </button>
    </div>
  );
}

// The fleet-wide voice switch at the top of the Voice sessions tab (issue #1765). It shows the state the
// GATEWAY reports and offers the opposite of it - on when off, off when on.
//
// It used to decide its own direction by reading the roster: "does any session have voice on?". That is a
// different question. It turns true the moment ONE session is on and stays true while nine others are off,
// so the button flipped permanently to "turn it all off" and a session created afterwards could never be
// switched on from here at all - it quietly never joined the voice queue, and the queue got the blame.
//
// Now the Gateway holds the intent, applies it to sessions as they appear, and answers "am I in voice
// mode?" itself. This renders that answer (CLAUDE.md rule 7: the client is dumb, the Gateway owns the
// verdict). The banner in the app shell and this control read the SAME shared state - one value, one poll -
// so a change made in either place shows in the other immediately rather than a poll later.
function VoiceAllControl({ sessions }: { sessions: SessionDto[] }) {
  const voice = useVoiceModeAll();
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // The action is always the opposite of the state the Gateway reports. Before the first read lands the
  // state is unknown, and the button says so rather than guessing a direction and acting on the guess.
  const enable = voice.enabled !== true;
  const count = sessions.length;

  const onClick = async () => {
    setError(null);
    setNote(null);
    try {
      const result = await setVoiceModeAllSessions(enable);
      await voice.set(enable);
      const changedLabel = `${result.changed} ${result.changed === 1 ? "session" : "sessions"} ${enable ? "on" : "off"}`;
      // Fail loud, the mobile rule: name what was passed over. A skipped session is not lost - the
      // Gateway's sweep picks it up when its computer comes back, because voice mode is a standing intent.
      setNote(
        result.skipped > 0
          ? `${changedLabel}, ${result.skipped} skipped (computer offline - they join when it is back)`
          : changedLabel,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not change voice mode for all sessions");
    }
  };

  const label = enable
    ? `Turn on voice mode for all ${count} ${count === 1 ? "session" : "sessions"}`
    : "Turn voice mode off for all sessions";
  const busyLabel = enable ? "Turning voice mode on..." : "Turning voice mode off...";

  return (
    <div className="voice-all">
      <p className="voice-all-line">
        Voice mode is about the fleet: every session narrates its turns, including ones started later.
        Auto-speak, below, is about this phone.
      </p>
      <button
        type="button"
        className={`voice-all-btn${enable ? "" : " voice-all-btn-off"}`}
        onClick={() => void onClick()}
        disabled={voice.busy || voice.enabled === null}
      >
        {voice.enabled === null ? "Checking voice mode..." : voice.busy ? busyLabel : label}
      </button>
      {note && <p className="voice-all-note" role="status">{note}</p>}
      {error && <p className="voice-all-error" role="alert">{error}</p>}
      {voice.error !== null && <p className="voice-all-error" role="alert">{voice.error}</p>}
    </div>
  );
}

// The plain-English note under a card whose Director did not answer, naming the machine and (when known)
// how long ago it was last seen. Wobbly reads as a soft "reconnecting"; a Director that was SHUT DOWN
// reads as not running; anything else as a firm "unreachable" - each honest about a different thing.
//
// The shut-down case is not a variation on unreachable, it is its opposite: nothing failed. This note
// used to call it "Unreachable" because the retention fold flattened every non-wobbly state into offline,
// so the phone announced an outage about a machine that was perfectly healthy - the same false alarm the
// Fleet Map was giving, one surface over. The word now comes from the Gateway (mark.stateLabel).
function unreachableNote(mark: RosterSessionMark): string {
  const machine = mark.machineName.length > 0 ? mark.machineName : "its machine";
  const base =
    mark.reachability === "wobbly"
      ? `Reconnecting to ${machine}`
      : mark.reachability === "stopped"
      ? `${mark.stateLabel.length > 0 ? mark.stateLabel : "Not running"} - ${machine}`
      : `Unreachable - ${machine}`;
  return mark.lastSeenLabel.length > 0 ? `${base} - ${mark.lastSeenLabel}` : base;
}

function SessionRow({ session, mark, fromTab = "all" }: { session: SessionDto; mark?: RosterSessionMark; fromTab?: RosterTab }) {
  const name = session.name && session.name.trim().length > 0 ? session.name : "(unnamed session)";
  const repo = repoLeaf(session);
  const machine = machineName(session);
  // TWO different questions, which this row used to answer with one flag - inspection 1, finding 2.
  //
  //  - DIMMED is display: "is this row's content last-known rather than current?" The retention mark is
  //    exactly that signal, so the graying, the dashed border and the dated note stay keyed to it. A
  //    wobbly machine's card SHOULD look stale, because it is.
  //  - ACTIONABLE is the nag rule: "can the owner actually do anything about this?" That is the Gateway's
  //    stamp and nothing else. Keying it to the mark meant a machine whose tunnel was UP, merely late with
  //    its pushes, stopped raising its hand - no attention treatment, no waiting clock, no voice - which
  //    is the opposite of what the branch claims and hid work the owner could have acted on immediately.
  //
  // The rule the whole branch rests on is two flags, not one: SHOW last-known work, but only NAG about
  // work on a machine that can be reached.
  const dimmed = mark !== undefined;
  const actionable = machineCanBeActedOn(session);
  const attention = classify(session) === "needsYou" && actionable;
  // Issue #844: the session's short three-digit number (SessionDto.Number, #820) read from the
  // regenerated typed client. Null on sessions/Directors without a number - then no prefix shows.
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  // The Gateway-owned hold time ("wakes in 3h 48m") and the winding-down flag, read the same way the
  // desktop rail and the Cockpit read them - never the raw onHold sensor (which can drift from the fold).
  const holdCountdown = snoozeCountdown(session);
  const windingDown = pendingDeletion(session);
  // Issue #948: a voice-mode session opens straight on its Voice tab (the surface it is meant to be
  // used from), not on the default Chat tab; every other session still opens on Chat.
  const sid = encodeURIComponent(session.sessionId ?? "");
  const to = session.voiceMode ? `/session/${sid}/voice` : `/session/${sid}`;
  return (
    <li className={`row${attention ? " row-attention" : ""}${dimmed ? " row-unreachable" : ""}`}>
      {/* Hand the known voice-mode state to the destination (issue #1015) so the Voice screen paints
          the right state on the first render instead of flashing OFF while its first poll resolves. */}
      <Link className="row-link" to={to} state={{ voiceMode: Boolean(session.voiceMode), fromTab }}>
        {/* The dot always paints the Gateway-stamped colour - even when the owning machine is unreachable,
            the dot keeps telling the truth about the session's last-known state. Unreachability is shown by
            the row treatment (the .row-unreachable dashed border + dimming opacity + the "Unreachable" note),
            never by overriding the dot with a locally chosen grey. */}
        <span className="dot" style={{ backgroundColor: dotHex(session) }} aria-hidden="true" />
        <span className="row-body">
          {/* The name uses the full card width and WRAPS (no truncation) - issue #838. A muted
              three-digit number prefix sits before the bold name, matching the desktop SessionRail
              (issue #844); when the session has no number, no prefix is rendered. */}
          <span className="row-name">
            {hasNum && <span className="row-num">{num}</span>}
            {name}
          </span>
          {/* The status / what-is-happening text sits on its own line below the name. On a needs-you
              card the live "waiting <dur>" is pinned to the right of this same line (issue #844). */}
          <span className="row-meta">
            <span className="row-context">{contextLine(session)}</span>
            {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
            {/* The hold time on a snoozed row ("wakes in 3h 48m"), from the Gateway-owned snooze clock,
                ticking each second - only mounted when there is a clock, so other rows keep no timer. */}
            {holdCountdown !== null && <HoldCountdown session={session} />}
          </span>
          {/* The supervision facts (internal#625) - started / open / idle / turns, the same shared
              formatter the Cockpit card maps over, so both surfaces say each fact the same way.
              Stats a Director does not report are omitted, never rendered as zero. */}
          <SupervisionLine session={session} />
          {/* Snooze Length mission: a distinct "Snooze ended" badge when this session just returned from
              an expired snooze on its own (the dead-man's switch fired) - so the reader knows this is a
              "go see why it went quiet" item, not a fresh turn-end. */}
          {snoozeExpired(session) && <span className="row-snooze-ended">Snooze ended</span>}
          {/* A prompt to this session was NOT delivered - the user's words never reached the agent (issue
              internal#811). Sticky and red, never a toast: the loss stays on the card until something
              actually lands. Two spoken prompts died in a log file on 2026-07-15 because no card said
              anything. The Gateway writes the sentence; this row only refuses to be quiet. */}
          {hasUndeliveredPrompt(session) && (
            <span className="row-not-delivered" title={promptDeliveryTitle(session) ?? undefined}>
              {DELIVERY_BADGE_TEXT}
            </span>
          )}
          {/* A session flagged for deletion wears a neutral "winding down" badge (a BADGE, never a colour):
              the dot keeps telling the truth about the work while this rides beside it. */}
          {windingDown && (
            <span className="row-winding-down" title={deletionReason(session) ?? "Marked for deletion"}>
              winding down
            </span>
          )}
          {/* The facts you navigate and filter by - the machine the session runs on and its repo - are
              a bottom row of small chips, so a fleet spread across several machines is legible at a
              glance without crowding the status line. The machine chip is accent-tinted; the repo chip
              is neutral. Either is omitted when the Gateway did not stamp it. */}
          {(machine || repo) && (
            <span className="row-chips">
              {machine && <span className="row-chip row-chip-machine">{machine}</span>}
              {repo && <span className="row-chip row-chip-repo">{repo}</span>}
            </span>
          )}
          {/* Mobile-resilience Phase 2: when the owning machine is unreachable, a short plain note says so
              and names the machine - the card is kept and grayed, never dropped, so the reader knows this
              is stale-but-preserved, not gone. */}
          {mark && <span className="row-unreachable-note">{unreachableNote(mark)}</span>}
          {/* A dictation started on this session's screen keeps showing here once the user walks back
              to the roster (#1139): in-flight while it uploads/transcribes, a sticky red pill if it
              failed - so a dropped transcription is visible from the list, never silent. */}
          <DictationRowBadge sessionId={session.sessionId} />
        </span>
        {/* The voice control reads REACHABILITY - the Gateway's stamp - and not the retention mark. A
            retained card from a machine nobody can reach kept its clip and its last-known voiceAudioReady,
            so it was the one element still promising something live, and it must not. But a wobbly machine
            can still be spoken to, so it keeps its triangle: the promise the triangle makes is "tapping
            this will speak", and that promise holds whenever the tunnel is up. */}
        <VoiceIndicator session={session} reachable={actionable} />
      </Link>
    </li>
  );
}

// Issue #850: the trailing voice control on a voice-mode card. What it is allowed to say is decided by
// the shared voiceRowState rule (client-core/voice/voiceRowState.ts), which this component only
// renders. That rule is shared with the Voice screen's own readiness test on purpose: the triangle is a
// promise that tapping through will speak, and the roster must not make that promise on weaker evidence
// than the screen it hands you to.
//
// It used to make exactly that mistake - it drew the triangle on `clip.phase === "ready"`, which asks
// "do I hold any bytes?" and never "which turn are they for?". A held clip from an older turn earned a
// green triangle; tapping it landed on "Voice service down". See voiceRowState.ts for the full account.
//
// Tapping the triangle plays the locally-stored clip with no download wait; preventDefault and
// stopPropagation keep the tap from also following the row's link. If this session's clip is playing
// when the agent resumes, it is stopped - a stale clip must not keep talking.
function VoiceIndicator({ session, reachable }: { session: SessionDto; reachable: boolean }) {
  const sid = session.sessionId ?? "";
  const working = isWorking(session);
  const state = voiceRowState(rowVoiceInputs(session, working, reachable));

  useEffect(() => {
    if (working && playingSid() === sid) stopPlayback();
  }, [working, sid]);

  if (state === "ready") {
    const isPlaying = playingSid() === sid;
    return (
      <button
        type="button"
        className="row-tri-btn"
        aria-label={isPlaying ? "Stop voice message" : "Play voice message"}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          if (isPlaying) stopPlayback();
          else playClip(sid);
        }}
      >
        <span className={isPlaying ? "row-stop" : "row-tri"} aria-hidden="true" />
      </button>
    );
  }

  if (state === "preparing") {
    return <span className="row-spin" aria-label="Preparing voice" />;
  }

  // The Gateway cannot make voice for this session and said so. Say it on the row, quietly: the owner
  // asked to be told there is no voice rather than be handed a play button that leads to a dead screen.
  // No call to action - the Gateway is already retrying, and out-of-credits has its own banner.
  if (state === "down") {
    return <span className="row-voice-down">No voice</span>;
  }

  return null;
}

// The per-row dictation status pill (#1139 follow-up, honest states for #1182/#1184, the dropped states for
// #1590). Reads the same shared store the on-screen status strip reads, so a Speak Send shows on the roster
// too: a muted progress label while it is in flight, a calm amber "saved - still sending" while it is held on
// a bad connection, a "saved - tap to retry" while it is parked after a permanent failure, a red "Not sent -
// tap to open" when the session moved on and the words were dropped, and a red "Dictation failed" for the
// rare hard failure. A just-"done" send shows nothing here; the brief "Sent" acknowledgement belongs on the
// session screen, not as roster noise.
//
// Every non-busy phase is named EXPLICITLY, because the fallback below is a spinner: a phase that falls
// through would sit on the roster spinning forever, claiming a finished dictation is still working.
function DictationRowBadge({ sessionId }: { sessionId: string | undefined }) {
  const status = useDictationStatusFor(sessionId);
  if (!status || status.phase === "done") return null;
  if (status.phase === "failed") {
    return <span className="row-dictate row-dictate-failed">Dictation failed</span>;
  }
  if (status.phase === "dropped") {
    // The words were NOT delivered and are waiting on the session screen to be sent or dismissed.
    return <span className="row-dictate row-dictate-failed">Not sent - tap to open</span>;
  }
  if (status.phase === "unheard") {
    return <span className="row-dictate row-dictate-parked">Nothing heard</span>;
  }
  if (status.phase === "held") {
    return <span className="row-dictate row-dictate-held">Saved - still sending</span>;
  }
  if (status.phase === "parked") {
    return <span className="row-dictate row-dictate-parked">Saved - tap to retry</span>;
  }
  return (
    <span className="row-dictate row-dictate-busy">
      <span className="row-spin" aria-hidden="true" /> {rowBusyLabel(status.phase, status.uploaded, status.total)}
    </span>
  );
}

// The compact roster label for an in-flight dictation, mirroring the on-screen strip's phases so the
// roster is honest about where the send is (saving / uploading N of M / transcribing).
function rowBusyLabel(phase: string, uploaded?: number, total?: number): string {
  if (phase === "saving") return "Saving...";
  if (phase === "uploading") {
    if (total && total > 1) return `Uploading... ${uploaded ?? 0} of ${total}`;
    return "Uploading...";
  }
  return "Transcribing...";
}

// The card's supervision line (internal#625): started / open / idle / turns. The wording and the
// amber/red thresholds live in client-core/sessions/supervision, shared with the Cockpit roster,
// so the two surfaces cannot say the same fact two different ways. Ticks on the app's ONE shared
// one-second clock; the re-render is scoped to this line.
function SupervisionLine({ session }: { session: SessionDto }) {
  const now = useSharedNow();
  const stats = supervisionStats(session, now);
  if (stats.length === 0) return null;
  return (
    <span className="row-stats">
      {stats.map((s) => (
        <span className="row-stat" key={s.key} title={s.title}>
          <span className="row-stat-k">{s.key}</span>
          <span className={`row-stat-v${s.tone === "normal" ? "" : ` ${s.tone}`}`}>{s.value}</span>
        </span>
      ))}
    </span>
  );
}

// Issue #844: the live elapsed-waiting label for a needs-you card, right-aligned on the status
// line. It ticks once a second by recomputing from the held needsYouSince (no roster refetch), and
// renders nothing while the value is empty/unparseable. Only mounted for needs-you cards, so the
// per-second re-render never touches working/other rows.
function WaitingTime({ since }: { since: string }) {
  const now = useNow(1000);
  const label = waitingLabel(since, now);
  if (label.length === 0) return null;
  return <span className="row-waiting">{label}</span>;
}

// The live "wakes in <dur>" hold time for a snoozed card, ticking each second from the Gateway-owned
// snooze clock (no roster refetch). Only mounted for snoozed cards that carry a clock, so the per-second
// re-render never touches other rows - the same pattern as WaitingTime.
function HoldCountdown({ session }: { session: SessionDto }) {
  const now = useNow(1000);
  const label = snoozeCountdown(session, now);
  if (label === null) return null;
  return <span className="row-hold-time">{label}</span>;
}
