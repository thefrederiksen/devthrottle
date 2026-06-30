import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { listSessions, type SessionDto } from "../api/client";
import { classify, contextLine, dotColor, effectiveColor, inBucket, inDesktopOrder, repoLeaf } from "../sessions/ordering";
import { useNow, waitingLabel } from "../sessions/waiting";
import { getClipState, playClip, syncVoiceSessions, useVoiceClips } from "../voice/clips";

// Home / roster. A "needs you" group first (when any session wants attention), then the full
// session list, both using the live Gateway /sessions data and the shared triage ordering.
// Tapping a row opens the session-detail placeholder bound to that session id.
const POLL_INTERVAL_MS = 5000;

export function Home() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadedOnce = useRef(false);

  // Re-render the roster when a voice clip finishes downloading (a card flips from the yellow
  // working state to the play-triangle the moment its audio is phone-ready).
  useVoiceClips();

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const data = await listSessions(signal);
      setSessions(data);
      setError(null);
      loadedOnce.current = true;
      // Pull each gateway-ready voice session's clip down to the phone so the triangle can appear
      // (phone-ready, the issue #850 rule). Fire-and-forget; it updates the clip store as bytes land.
      void syncVoiceSessions(data);
    } catch (err) {
      if (signal?.aborted) return;
      // Keep the last-known roster on screen (offline shell); only show the error banner.
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

  const needsYou = sessions ? inBucket(sessions, "needsYou") : [];
  const all = sessions ? inDesktopOrder(sessions) : [];

  return (
    <div className="screen">
      <header className="app-bar">
        <h1>DevThrottle</h1>
        <span className="app-bar-sub">Mission Control</span>
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {loadedOnce.current ? "Offline - showing last-known roster" : error}
        </div>
      )}

      {/* "+ New session" entry (issue #812): opens the add-session flow (machine -> repo -> create),
          a faithful translation of the Android NewSessionPanel. */}
      <Link className="new-session-entry" to="/new">
        <span className="new-session-plus" aria-hidden="true">+</span>
        New session
      </Link>

      {sessions === null && error === null && <p className="status-line">Loading sessions...</p>}

      {sessions !== null && all.length === 0 && (
        <p className="status-line">No sessions running.</p>
      )}

      {needsYou.length > 0 && (
        <section className="group">
          <h2 className="group-title group-title-attention">Needs you</h2>
          <ul className="roster">
            {needsYou.map((s) => (
              <SessionRow key={`needs-${s.sessionId}`} session={s} />
            ))}
          </ul>
        </section>
      )}

      {all.length > 0 && (
        <section className="group">
          <h2 className="group-title">All sessions</h2>
          <ul className="roster">
            {all.map((s) => (
              <SessionRow key={s.sessionId} session={s} />
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function SessionRow({ session }: { session: SessionDto }) {
  const color = effectiveColor(session);
  const name = session.name && session.name.trim().length > 0 ? session.name : "(unnamed session)";
  const repo = repoLeaf(session);
  const attention = classify(session) === "needsYou";
  // Issue #844: the session's short three-digit number (SessionDto.Number, #820) read from the
  // regenerated typed client. Null on sessions/Directors without a number - then no prefix shows.
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  return (
    <li className={`row${attention ? " row-attention" : ""}`}>
      <Link className="row-link" to={`/session/${encodeURIComponent(session.sessionId ?? "")}`}>
        <span className="dot" style={{ backgroundColor: dotColor(color) }} aria-hidden="true" />
        <span className="row-body">
          {/* The name uses the full card width and WRAPS (no truncation) - issue #838. A muted
              three-digit number prefix sits before the bold name, matching the desktop SessionRail
              (issue #844); when the session has no number, no prefix is rendered. */}
          <span className="row-name">
            {hasNum && <span className="row-num">{num}</span>}
            {name}
          </span>
          {/* The status / what-is-happening text and the repo share one line BELOW the name,
              separated by a thin divider, with the repo kept visually secondary. On a needs-you
              card the live "waiting <dur>" is pinned to the right of this same line (issue #844). */}
          <span className="row-meta">
            <span className="row-context">{contextLine(session)}</span>
            {repo && <span className="row-divider" aria-hidden="true" />}
            {repo && <span className="row-repo">{repo}</span>}
            {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
          </span>
        </span>
        <VoiceIndicator session={session} />
      </Link>
    </li>
  );
}

// Issue #850: the trailing voice control on a voice-mode card. A play-triangle appears ONLY once
// the clip's audio is on the phone (clip phase "ready"); while the Wingman is generating on the
// Gateway or the phone is still downloading, a yellow spinner shows instead. Non-voice sessions
// render nothing here. Tapping the triangle plays the locally-stored clip with no download wait;
// preventDefault/stopPropagation keep the tap from also following the row's link.
function VoiceIndicator({ session }: { session: SessionDto }) {
  if (!session.voiceMode) return null;
  const sid = session.sessionId ?? "";
  const clip = getClipState(sid);

  if (clip.phase === "ready") {
    return (
      <button
        type="button"
        className="row-tri-btn"
        aria-label="Play voice message"
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          playClip(sid);
        }}
      >
        <span className="row-tri" aria-hidden="true" />
      </button>
    );
  }

  // Voice on but no phone-ready clip yet: generating on the Gateway, or downloading to the phone.
  if (session.voiceGenerating || session.voiceAudioReady || clip.phase === "downloading") {
    return <span className="row-spin" aria-label="Preparing voice" />;
  }

  return null;
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
