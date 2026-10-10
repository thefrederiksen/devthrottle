import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  deleteRecording,
  getRecording,
  listChunks,
  listRecordings,
  recordingStoreAvailable,
  recoverInterrupted,
  saveRecording,
  type LocalRecording,
} from "@devthrottle/client-core/recorder/recordingStore";
import {
  recordingSession,
  useRecordingSession,
} from "@devthrottle/client-core/recorder/recordingSession";
import {
  driveRecordingUpload,
  resumePendingRecordingUploads,
} from "@devthrottle/client-core/recorder/ingestUpload";
import {
  getRecordings,
  getTranscript,
  recordingAudioUrl,
  type RecordingListItem,
} from "@devthrottle/client-core/recordings/recordingsClient";
import { describeReadAndReport, reportShownError } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "mobile-recorder";
const NO_DURABLE_STORE =
  "This browser cannot store recordings durably (no IndexedDB), so recording is disabled here. " +
  "Open DevThrottle in a normal browser tab to record.";

/** The recorder cannot work in this browser at all: said on screen from the first paint, and reported once. */
function reportStoreUnavailable(): void {
  reportShownError(SURFACE, "open the recorder", NO_DURABLE_STORE);
}

// The Voice Recorder screen (issue #958) - the PWA successor of the retired native Android recorder,
// carrying its concepts over: rolling one-minute segments persisted durably AS THEY ARE CAPTURED (a
// crash loses at most the open segment), per-segment resume on upload, timestamped notes delivered on
// the complete call, a live level meter so you can see it is hearing you, a title editable until stop,
// and a library whose rows show TWO independent statuses - Uploaded and Transcribed - because a
// transcription failure never un-uploads the audio.
//
// The layout is the Android MainPage's, translated: one card with the big timer + state + segment
// count + level meter; the title entry; the big red Record button (Pause appears only while
// recording); the notes section; the library below with per-row determinate progress. Upload
// machinery stays out of the way of the recording controls.
//
// Uploading is AUTOMATIC, exactly like the Android recorder ("Kept on your phone and uploaded
// automatically" was its literal copy - issue devthrottle_internal#966): Stop finalizes the recording
// AND queues its upload in the same breath; there is no Send step. A failed upload parks the row
// saved-and-retryable with a manual Retry, and client-core's durable upload driver re-drives pending
// work on app load and when connectivity returns. Once the server acknowledges the complete call the
// local copy is deleted and the row is carried by the server's own list (GET /ingest/recordings),
// where its transcription state comes from - so the row reads the same before and after delivery.

const SERVER_POLL_MS = 5000;
const TICK_MS = 150;

/** hh:mm:ss, matching the Android recorder's big timer. */
function formatClock(ms: number): string {
  const totalS = Math.floor(ms / 1000);
  const h = Math.floor(totalS / 3600);
  const m = Math.floor((totalS % 3600) / 60);
  const s = totalS % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return `${two(h)}:${two(m)}:${two(s)}`;
}

function formatDuration(ms: number): string {
  const totalS = Math.round(ms / 1000);
  const m = Math.floor(totalS / 60);
  const s = totalS % 60;
  return m > 0 ? `${m} min ${s} s` : `${s} s`;
}

function formatWhen(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

/** One library row: a local (not yet delivered) recording OR a server-side one, never both - the
 *  local copy is deleted exactly when the server acknowledges it holds everything. */
interface LibraryRow {
  key: string;
  local?: LocalRecording;
  server?: RecordingListItem;
  startedAt: string;
}

/** SVG checkmark, drawn (not a text glyph) exactly like the Android recorder's Path. */
function Check({ on, tone }: { on: boolean; tone: "ok" | "dim" }) {
  return (
    <svg className={`rec-check ${on && tone === "ok" ? "rec-check-on" : ""}`} viewBox="0 0 13 11" aria-hidden="true">
      <path d="M1 5.5 L4.5 9 L12 1" fill="none" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}

/** SVG red X for a failed transcription (the audio itself is safe on the Gateway). */
function Cross() {
  return (
    <svg className="rec-cross" viewBox="0 0 11 11" aria-hidden="true">
      <path d="M1 1 L10 10 M10 1 L1 10" fill="none" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}

export function Recorder() {
  const storeOk = recordingStoreAvailable();
  useEffect(() => {
    if (!storeOk) reportStoreUnavailable();
  }, [storeOk]);

  // The capture lifecycle lives in the app-level recording session, NOT in this page: leaving this
  // page must not stop the recording (recorder-unlimited-capture mission). This page renders the
  // session's state and drives its controls; the live numbers are polled on a display tick.
  const session = useRecordingSession();
  const { phase, title, error } = session;
  const activeNotes = session.notes;

  const [elapsedMs, setElapsedMs] = useState(0);
  const [segmentCount, setSegmentCount] = useState(0);
  const [level, setLevel] = useState(0);
  const [noteText, setNoteText] = useState("");

  const [localRecordings, setLocalRecordings] = useState<LocalRecording[]>([]);
  const [serverRecordings, setServerRecordings] = useState<RecordingListItem[]>([]);
  const [serverError, setServerError] = useState<string | null>(null);
  // The library poll: its failure is reported when it first appears, not on every round.
  const serverReportRef = useRef<string | null>(null);

  const [playingId, setPlayingId] = useState<string | null>(null);
  const [playbackError, setPlaybackError] = useState<string | null>(null);
  const [transcriptFor, setTranscriptFor] = useState<{ id: string; text: string } | null>(null);
  const [confirmDiscard, setConfirmDiscard] = useState<string | null>(null);

  const audioRef = useRef<HTMLAudioElement | null>(null);
  const playTokenRef = useRef(0);

  const refreshLocal = useCallback(async () => {
    setLocalRecordings(await listRecordings());
  }, []);

  const refreshServer = useCallback(async (signal?: AbortSignal) => {
    try {
      const items = await getRecordings(signal);
      setServerRecordings(items);
      setServerError(null);
      serverReportRef.current = null;
    } catch (err) {
      if (signal?.aborted) return;
      setServerError(describeReadAndReport(SURFACE, "load the recordings on the Gateway", err, undefined, serverReportRef));
    }
  }, []);

  // Load: recover interrupted captures, list the library, resume any upload the user already asked
  // for (the durable resume-on-open pattern), and keep the server list fresh while the screen is up.
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      // The LIVE capture (if any) is excluded from recovery: the session survives navigation, so a
      // "recording" row may be the healthy capture running right now, not an orphan.
      await recoverInterrupted(recordingSession.getState().recordingId ?? undefined);
      await refreshLocal();
      await refreshServer(controller.signal);
      void resumePendingRecordingUploads(() => void refreshLocal()).then(() => {
        void refreshLocal();
        void refreshServer(controller.signal);
      });
    })();

    const poll = setInterval(() => void refreshServer(controller.signal), SERVER_POLL_MS);
    const onOnline = () => {
      void resumePendingRecordingUploads(() => void refreshLocal()).then(() => {
        void refreshLocal();
        void refreshServer(controller.signal);
      });
    };
    window.addEventListener("online", onOnline);
    return () => {
      controller.abort();
      clearInterval(poll);
      window.removeEventListener("online", onOnline);
      // Leaving the screen does NOT touch the capture - the session lives above the router and
      // recording continues (the global recording banner keeps it visible). Only playback stops.
      const audio = audioRef.current;
      if (audio !== null) audio.pause();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Re-read the library whenever the session finalized, queued, or progressed an upload.
  useEffect(() => {
    void refreshLocal();
    void refreshServer();
  }, [session.libraryVersion, refreshLocal, refreshServer]);

  // The live tick: timer, level meter, segment count - only while capturing.
  useEffect(() => {
    if (phase !== "recording" && phase !== "paused") return;
    const t = setInterval(() => {
      setElapsedMs(recordingSession.elapsedMs());
      setSegmentCount(recordingSession.segmentCount());
      setLevel(recordingSession.level());
    }, TICK_MS);
    return () => clearInterval(t);
  }, [phase]);

  // Reset the displayed numbers when the capture ends (the tick above stops running).
  useEffect(() => {
    if (phase === "idle") {
      setElapsedMs(0);
      setSegmentCount(0);
      setLevel(0);
    }
  }, [phase]);

  // Capture controls are thin wrappers over the app-level session, which owns the whole lifecycle
  // (durable shell, segment persistence, finalize-and-queue-upload, truncation surfacing).
  const startRecording = useCallback(() => recordingSession.start(), []);
  const pauseResume = useCallback(
    () => (phase === "paused" ? recordingSession.resume() : recordingSession.pause()),
    [phase],
  );
  const stopRecording = useCallback(() => recordingSession.stop(), []);
  const persistTitle = useCallback(() => recordingSession.persistTitle(), []);

  const addNote = useCallback(async () => {
    await recordingSession.addNote(noteText);
    setNoteText("");
  }, [noteText]);

  // Manual retry for a PARKED (saved-and-retryable) row only - the happy path uploads by itself.
  const retryUpload = useCallback(
    async (recordingId: string) => {
      const rec = await getRecording(recordingId);
      if (rec === null) return;
      rec.state = "queued";
      rec.lastError = undefined;
      await saveRecording(rec);
      await refreshLocal();
      void driveRecordingUpload(recordingId, () => recordingSession.notifyLibraryChanged()).then(
        () => recordingSession.notifyLibraryChanged(),
        () => recordingSession.notifyLibraryChanged(),
      );
    },
    [refreshLocal],
  );

  const discardRecording = useCallback(
    async (recordingId: string) => {
      await deleteRecording(recordingId);
      setConfirmDiscard(null);
      await refreshLocal();
    },
    [refreshLocal],
  );

  const stopPlayback = useCallback(() => {
    playTokenRef.current += 1;
    const audio = audioRef.current;
    if (audio !== null) {
      audio.pause();
      audio.removeAttribute("src");
    }
    setPlayingId(null);
  }, []);

  /** Play a recording's segments in order - local blobs before delivery, Gateway audio after. */
  const playRecording = useCallback(
    async (row: LibraryRow) => {
      stopPlayback();
      setPlaybackError(null);
      const token = ++playTokenRef.current;
      const audio = audioRef.current ?? new Audio();
      audioRef.current = audio;
      setPlayingId(row.key);

      const sources: string[] = [];
      const revoke: string[] = [];
      if (row.local !== undefined) {
        const chunks = await listChunks(row.local.recordingId);
        for (const c of chunks) {
          const url = URL.createObjectURL(c.blob);
          sources.push(url);
          revoke.push(url);
        }
      } else if (row.server !== undefined) {
        for (let i = 0; i < row.server.segments; i++) {
          sources.push(recordingAudioUrl(row.server.recordingId, i));
        }
      }

      const cleanup = () => {
        for (const url of revoke) URL.revokeObjectURL(url);
      };
      let index = 0;
      const playNext = () => {
        if (playTokenRef.current !== token || index >= sources.length) {
          cleanup();
          if (playTokenRef.current === token) setPlayingId(null);
          return;
        }
        audio.src = sources[index];
        index += 1;
        audio.onended = playNext;
        audio.onerror = () => {
          cleanup();
          if (playTokenRef.current === token) {
            setPlayingId(null);
            setPlaybackError(reportShownError(SURFACE, "play the recording", "Playback failed - this segment could not be decoded."));
          }
        };
        void audio.play().catch(() => {
          cleanup();
          if (playTokenRef.current === token) setPlayingId(null);
        });
      };
      playNext();
    },
    [stopPlayback],
  );

  const toggleTranscript = useCallback(
    async (recordingId: string) => {
      if (transcriptFor?.id === recordingId) {
        setTranscriptFor(null);
        return;
      }
      const text = await getTranscript(recordingId);
      setTranscriptFor({ id: recordingId, text: text ?? "The transcript could not be loaded." });
    },
    [transcriptFor],
  );

  // ===== library rows ====================================================================

  const serverOnly = serverRecordings.filter(
    (s) => !localRecordings.some((l) => l.recordingId === s.recordingId),
  );
  const rows: LibraryRow[] = [
    ...localRecordings
      .filter((l) => l.state !== "recording")
      .map((l) => ({ key: l.recordingId, local: l, startedAt: l.startedAt })),
    ...serverOnly.map((s) => ({ key: s.recordingId, server: s, startedAt: s.startedAt })),
  ].sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1));

  const recording = phase === "recording" || phase === "paused";

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Voice Recorder</h1>
      </header>

      {!storeOk && (
        <div className="banner banner-error" role="alert">
          {/* error-reported-by: reportStoreUnavailable */}
          {NO_DURABLE_STORE}
        </div>
      )}

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {/* error-reported-by: recordingSession */}
          {error}{" "}
          <button type="button" className="rec-row-btn" onClick={() => recordingSession.clearError()}>
            Dismiss
          </button>
        </div>
      )}

      {playbackError !== null && (
        <div className="banner banner-error" role="alert">
          {playbackError}{" "}
          <button type="button" className="rec-row-btn" onClick={() => setPlaybackError(null)}>
            Dismiss
          </button>
        </div>
      )}

      {/* Timer + state + segments + live level, one card - the Android recorder's layout. */}
      <section className="rec-card" aria-label="Recording status">
        <div className="rec-timer">{formatClock(elapsedMs)}</div>
        <div className={`rec-state ${recording && phase !== "paused" ? "rec-state-live" : ""}`}>
          {phase === "idle" && "Idle"}
          {phase === "starting" && "Opening microphone..."}
          {phase === "recording" && "Recording"}
          {phase === "paused" && "Paused"}
          {phase === "stopping" && "Saving..."}
        </div>
        <div className="rec-segments">
          {segmentCount} segment{segmentCount === 1 ? "" : "s"} captured
        </div>
        <div className="rec-level" aria-hidden="true">
          <div className="rec-level-fill" style={{ width: `${Math.round(level * 100)}%` }} />
        </div>
      </section>

      <input
        className="rec-title"
        type="text"
        placeholder="Title (optional, edit anytime until you stop)"
        value={title}
        onChange={(e) => recordingSession.setTitle(e.target.value)}
        onBlur={() => void persistTitle()}
        disabled={phase === "stopping"}
      />

      {recording && (
        <div className="rec-section-hint">
          There is no time limit - recording continues while you use the rest of the app, and the
          red bar up top shows it is live. Keep the screen on: if the phone locks or the browser
          suspends the page, the microphone is cut - the recording then stops, saves everything
          captured, and says here that it was cut short.
        </div>
      )}

      {!recording ? (
        <button
          type="button"
          className="rec-record-btn"
          onClick={() => void startRecording()}
          disabled={!storeOk || phase === "starting" || phase === "stopping"}
        >
          {phase === "starting" ? "Starting..." : "Record"}
        </button>
      ) : (
        <div className="rec-controls">
          <button type="button" className="rec-pause-btn" onClick={() => void pauseResume()}>
            {phase === "paused" ? "Resume" : "Pause"}
          </button>
          <button type="button" className="rec-stop-btn" onClick={() => void stopRecording()}>
            Stop
          </button>
        </div>
      )}

      {recording && (
        <section className="rec-notes" aria-label="Notes while recording">
          <div className="rec-section-title">Notes while recording</div>
          <div className="rec-note-row">
            <input
              className="rec-note-input"
              type="text"
              placeholder="Type a note, hit Add"
              value={noteText}
              onChange={(e) => setNoteText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void addNote();
              }}
            />
            <button type="button" className="rec-note-add" onClick={() => void addNote()} disabled={noteText.trim() === ""}>
              Add
            </button>
          </div>
          {activeNotes.length > 0 && (
            <ul className="rec-note-list">
              {activeNotes.map((n, i) => (
                <li key={i}>
                  <span className="rec-note-t">{formatClock(n.tMs)}</span> {n.text}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      <section className="rec-library" aria-label="Recordings">
        <div className="rec-section-title">Recordings</div>
        <div className="rec-section-hint">
          Kept on your phone and uploaded automatically when you stop, then transcribed on the Gateway.
          Each row shows its own progress; transcripts appear on the Cockpit&apos;s Voice Recorder page too.
        </div>

        {serverError !== null && (
          <div className="banner banner-error" role="alert">
            {serverError}
          </div>
        )}

        {rows.length === 0 && serverError === null && (
          <div className="rec-empty">No recordings yet. Hit Record, talk, stop.</div>
        )}

        {rows.map((row) => {
          const l = row.local;
          const s = row.server;
          const titleText = l?.title ?? s?.title ?? "";
          const when = formatWhen(row.startedAt);
          const durMs = l?.durationMs ?? s?.durationMs ?? 0;
          const segs = l?.segments ?? s?.segments ?? 0;
          const uploading = l?.state === "uploading";
          const uploadedOn = s !== undefined; // a server row exists only after full delivery
          const transcribedOn = s !== undefined && (s.state === "transcribed" || s.state === "filed");
          const transcribeFailed = s !== undefined && s.state === "error";
          const transcribing =
            s !== undefined && (s.state === "queued" || s.state === "transcribing" || s.state === "cleaning");
          const isPlaying = playingId === row.key;

          return (
            <div className="rec-row" key={row.key}>
              <div className="rec-row-title">{titleText}</div>
              <div className="rec-row-sub">
                {when}
                {durMs > 0 && ` - ${formatDuration(durMs)}`}
                {segs > 0 && ` - ${segs} segment${segs === 1 ? "" : "s"}`}
                {l?.recovered && " - recovered after the app closed while recording"}
              </div>

              {l !== undefined && (l.state === "queued" || l.state === "ready") && (
                <div className="rec-row-note">Waiting to upload...</div>
              )}
              {uploading && (
                <>
                  <div className="rec-row-note">
                    Sending segment {Math.min((l?.uploadCurrent ?? 0) + 1, l?.uploadTotal ?? 1)}/{l?.uploadTotal ?? 0}
                  </div>
                  <div className="rec-progress">
                    <div
                      className="rec-progress-fill"
                      style={{
                        width: `${(l?.uploadTotal ?? 0) > 0 ? Math.round(((l?.uploadCurrent ?? 0) / (l?.uploadTotal ?? 1)) * 100) : 0}%`,
                      }}
                    />
                  </div>
                </>
              )}
              {l !== undefined && l.state === "uploaded" && !l.completed && (
                <div className="rec-row-note">Audio uploaded - finishing delivery...</div>
              )}
              {l !== undefined && l.state === "retry" && (
                <div className="rec-row-err">
                  {l.lastError ?? "Send failed - the recording is saved on this phone and will be retried."}
                </div>
              )}
              {l?.interrupted !== undefined && (
                <div className="rec-row-err">
                  Cut short: {l.interrupted}
                </div>
              )}
              {transcribeFailed && (
                <div className="rec-row-err">
                  Transcription failed on the Gateway. The audio is safely uploaded and can be retried there.
                </div>
              )}

              {/* Two independent statuses, never conflated (Android principle #3). */}
              <div className="rec-row-status">
                <span className="rec-status-item">
                  <Check on={uploadedOn} tone="ok" />
                  Uploaded
                </span>
                <span className="rec-status-item">
                  {transcribeFailed ? <Cross /> : <Check on={transcribedOn} tone="ok" />}
                  {transcribing ? "Transcribing..." : "Transcribed"}
                </span>
              </div>

              <div className="rec-row-actions">
                {(l !== undefined || (s !== undefined && s.segments > 0)) && (
                  <button
                    type="button"
                    className="rec-row-btn"
                    onClick={() => (isPlaying ? stopPlayback() : void playRecording(row))}
                  >
                    {isPlaying ? "Stop playback" : "Play"}
                  </button>
                )}
                {l !== undefined && l.state === "retry" && (
                  <button type="button" className="rec-row-btn rec-row-btn-primary" onClick={() => void retryUpload(l.recordingId)}>
                    Retry
                  </button>
                )}
                {l !== undefined && l.state === "retry" && (
                  confirmDiscard === l.recordingId ? (
                    <>
                      <button type="button" className="rec-row-btn rec-row-btn-danger" onClick={() => void discardRecording(l.recordingId)}>
                        Really discard
                      </button>
                      <button type="button" className="rec-row-btn" onClick={() => setConfirmDiscard(null)}>
                        Keep
                      </button>
                    </>
                  ) : (
                    <button type="button" className="rec-row-btn" onClick={() => setConfirmDiscard(l.recordingId)}>
                      Discard
                    </button>
                  )
                )}
                {transcribedOn && (
                  <button type="button" className="rec-row-btn" onClick={() => void toggleTranscript(row.key)}>
                    {transcriptFor?.id === row.key ? "Hide transcript" : "View transcript"}
                  </button>
                )}
              </div>

              {transcriptFor?.id === row.key && <div className="rec-transcript">{transcriptFor.text}</div>}
            </div>
          );
        })}
      </section>
    </div>
  );
}
