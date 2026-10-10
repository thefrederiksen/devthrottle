import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  deleteRecording,
  getAgentInfo,
  getRecordings,
  getTranscript,
  promoteRecording,
  recordingAudioUrl,
  updateRecordingMeta,
  type RecordingListItem,
} from "@devthrottle/client-core/recordings/recordingsClient";
import { ConfirmDialog, EmptyState, ErrorBanner, LoadingState } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { ClipboardRefusedError } from "../components/clipboardFailure";

const SURFACE = "cockpit-transcripts";

// The Voice Recorder page (issue #977, epic #967) - the React port of the Blazor Cockpit
// Transcripts.razor(.css) (#183). Recordings are uploaded from the phone and transcribed on the
// Gateway; they are kept locally and are temporary. Each card expands to read the transcript, plays
// its audio segment-by-segment, and supports Copy path, Save to vault, Delete, inline Title/Subtitle/
// Summary editing, and Copy agent info. It reads and writes same-origin through the Gateway front door
// (client-core) - never a Director address.
//
// Audio playback (the one thing a server-rendered Blazor page needed a JS shim for) is native here: a
// single HTMLAudioElement plays segment 0, then 1, ... in order. It authenticates via the
// cc-gateway-token cookie the shell mirrors at startup, exactly like the live terminal WebSocket.
//
// This is one of the first three pages to adopt the shared user-interface kit (issue #1244): Delete
// asks through the shared ConfirmDialog instead of a browser window.confirm/alert, and the load and
// empty states use the shared LoadingState / EmptyState / ErrorBanner.

// Per-recording view state (open/transcript/edit fields/transient messages), keyed by id.
interface CardState {
  open: boolean;
  transcriptLoaded: boolean;
  transcriptText: string;
  copied: string;
  vaultMsg: string;
  saveMsg: string;
  saving: boolean;
  promoting: boolean;
  editTitle: string;
  editSubtitle: string;
  editSummary: string;
}

function emptyCard(): CardState {
  return {
    open: false,
    transcriptLoaded: false,
    transcriptText: "Loading transcript...",
    copied: "",
    vaultMsg: "",
    saveMsg: "",
    saving: false,
    promoting: false,
    editTitle: "",
    editSubtitle: "",
    editSummary: "",
  };
}

function isDone(item: RecordingListItem): boolean {
  return item.state === "transcribed" || item.state === "filed";
}

export function TranscriptsView() {
  const [items, setItems] = useState<RecordingListItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [agentMsg, setAgentMsg] = useState("");
  const [state, setState] = useState<Record<string, CardState>>({});
  // Single audio-playback label (the page plays one clip at a time, mirroring the Blazor single-audio
  // model that set nowPlaying on every open card).
  const [nowPlaying, setNowPlaying] = useState("");
  // The recording awaiting delete confirmation. Deleting a recording removes its local transcript and
  // audio, so it asks through the shared ConfirmDialog (issue #1244) instead of a browser window.confirm.
  const [pendingDelete, setPendingDelete] = useState<RecordingListItem | null>(null);

  const audioRef = useRef<HTMLAudioElement | null>(null);
  const msgTimers = useRef<number[]>([]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const list = await getRecordings();
        if (!cancelled) setItems(list);
      } catch (err) {
        if (!cancelled) setError(describeAndReport(SURFACE, "load the transcripts", err));
      }
    })();
    const timers = msgTimers.current;
    return () => {
      cancelled = true;
      stopAudio();
      for (const t of timers) window.clearTimeout(t);
    };
  }, []);

  const cardOf = (id: string): CardState => state[id] ?? emptyCard();
  const patchCard = (id: string, patch: Partial<CardState>) =>
    setState((s) => ({ ...s, [id]: { ...(s[id] ?? emptyCard()), ...patch } }));

  const clearLater = (fn: () => void, ms: number) => {
    const t = window.setTimeout(fn, ms);
    msgTimers.current.push(t);
  };

  // ---- expand / transcript ----
  const toggleBody = async (item: RecordingListItem) => {
    const s = cardOf(item.recordingId);
    if (s.open) {
      patchCard(item.recordingId, { open: false });
      return;
    }
    // Seed the edit fields from the current record (mirrors the Blazor ToggleBody).
    patchCard(item.recordingId, {
      open: true,
      editTitle: item.title ?? "",
      editSubtitle: item.subtitle ?? "",
      editSummary: item.summary ?? "",
    });
    if (!s.transcriptLoaded) {
      if (item.hasTranscript) {
        const text = await getTranscript(item.recordingId);
        patchCard(item.recordingId, { transcriptText: text ?? "(transcript unavailable)", transcriptLoaded: true });
      } else {
        patchCard(item.recordingId, {
          transcriptText: isDone(item) ? "(no transcript text stored)" : `Not transcribed yet (state: ${item.state}).`,
          transcriptLoaded: true,
        });
      }
    }
  };

  // ---- audio (segment-by-segment, native) ----
  function stopAudio() {
    const a = audioRef.current;
    if (a !== null) {
      a.pause();
      a.onended = null;
      a.onerror = null;
      audioRef.current = null;
    }
  }

  const playAudio = (item: RecordingListItem) => {
    stopAudio();
    const segmentCount = Math.max(1, item.segments);
    let i = 0;
    const a = new Audio();
    audioRef.current = a;
    a.onended = () => {
      i++;
      if (i < segmentCount && audioRef.current === a) {
        a.src = recordingAudioUrl(item.recordingId, i);
        void a.play();
      } else if (audioRef.current === a) {
        audioRef.current = null;
        setNowPlaying("");
      }
    };
    a.onerror = () => setNowPlaying(" (playback error)");
    a.src = recordingAudioUrl(item.recordingId, 0);
    a.play().then(() => setNowPlaying(" playing...")).catch(() => setNowPlaying(" (tap again to play)"));
  };

  // ---- copy path ----
  const copyPath = async (item: RecordingListItem) => {
    const path = item.transcriptPath ?? "";
    // A blocked clipboard is not shown as an error: the path itself is shown instead, to copy by hand.
    const failure = await copyText(path);
    patchCard(item.recordingId, { copied: failure === null ? ` copied: ${path}` : ` ${path}` });
    clearLater(() => patchCard(item.recordingId, { copied: "" }), 6000);
  };

  // ---- delete ----
  // The actual delete, run once the ConfirmDialog is confirmed. A failure is left to throw so the
  // dialog surfaces it (fail loudly) rather than a browser window.alert.
  const performDelete = async (item: RecordingListItem) => {
    stopAudio();
    await deleteRecording(item.recordingId);
    setItems((prev) => (prev === null ? prev : prev.filter((x) => x.recordingId !== item.recordingId)));
    setState((s) => {
      const copy = { ...s };
      delete copy[item.recordingId];
      return copy;
    });
  };

  // ---- promote to vault ----
  const saveToVault = async (item: RecordingListItem) => {
    patchCard(item.recordingId, { promoting: true, vaultMsg: " saving to vault..." });
    try {
      await promoteRecording(item.recordingId);
      setItems((prev) =>
        prev === null ? prev : prev.map((x) => (x.recordingId === item.recordingId ? { ...x, inVault: true } : x)),
      );
      patchCard(item.recordingId, { vaultMsg: " saved to vault" });
      clearLater(() => patchCard(item.recordingId, { vaultMsg: "" }), 5000);
    } catch (err) {
      patchCard(item.recordingId, { vaultMsg: ` save failed: ${describeAndReport(SURFACE, "save the transcript to the vault", err)}` });
    } finally {
      patchCard(item.recordingId, { promoting: false });
    }
  };

  // ---- inline details edit ----
  const saveDetails = async (item: RecordingListItem) => {
    const s = cardOf(item.recordingId);
    patchCard(item.recordingId, { saving: true, saveMsg: " saving..." });
    try {
      const updated = await updateRecordingMeta(item.recordingId, {
        title: s.editTitle,
        subtitle: s.editSubtitle,
        summary: s.editSummary,
      });
      setItems((prev) =>
        prev === null ? prev : prev.map((x) => (x.recordingId === item.recordingId ? updated : x)),
      );
      patchCard(item.recordingId, { saveMsg: " saved" });
    } catch (err) {
      patchCard(item.recordingId, { saveMsg: ` save failed: ${describeAndReport(SURFACE, "save the transcript details", err)}` });
    } finally {
      patchCard(item.recordingId, { saving: false });
      clearLater(() => patchCard(item.recordingId, { saveMsg: "" }), 5000);
    }
  };

  // ---- agent info ----
  const copyAgentInfo = async () => {
    setAgentMsg(" building...");
    try {
      const text = await getAgentInfo();
      const failure = await copyText(text);
      setAgentMsg(failure === null ? " copied agent info to clipboard" : ` ${describeAndReport(SURFACE, "copy the agent information", new ClipboardRefusedError(failure))}`);
    } catch (err) {
      setAgentMsg(` failed: ${describeAndReport(SURFACE, "build the agent information", err)}`);
    }
    clearLater(() => setAgentMsg(""), 6000);
  };

  return (
    <div className="ts-root">
      <div className="ts-wrap">
        <div className="ts-top">
          <h1>Voice Recorder</h1>
          <div className="ts-topactions">
            <span className="ts-agentmsg">{agentMsg}</span>
            <button
              className="ts-ghost"
              title="Copy API endpoint + directory + usage so an external agent can connect and process these transcripts"
              onClick={() => void copyAgentInfo()}
            >
              Copy agent info
            </button>
            <Link className="ts-back" to="/">
              &larr; Sessions
            </Link>
          </div>
        </div>
        <p className="ts-sub">
          Recordings uploaded from the phone and transcribed. These are kept locally and are temporary.
          Use "Save to vault" to keep one permanently, or "Delete" to remove a transient transcript.
          Click one to read it or play the audio.
        </p>

        {items === null ? (
          error !== null ? (
            <ErrorBanner message={error} />
          ) : (
            <LoadingState />
          )
        ) : items.length === 0 ? (
          <EmptyState message="No recordings yet. Record something on the phone and it will appear here." />
        ) : (
          items.map((item) => {
            const s = cardOf(item.recordingId);
            const done = isDone(item);
            return (
              <div className="ts-card" key={item.recordingId}>
                <div className="ts-row" onClick={() => void toggleBody(item)}>
                  <div>
                    <div className="ts-title">{item.title}</div>
                    {item.subtitle && item.subtitle.trim().length > 0 && (
                      <div className="ts-subtitle">{item.subtitle}</div>
                    )}
                    <div className="ts-meta">
                      {fmtDate(item.startedAt)} &middot; {fmtDur(item.durationMs)} &middot; {item.segments} segment(s)
                    </div>
                  </div>
                  <div className="ts-badges">
                    <span className={`ts-badge ${done ? "ts-b-uploaded" : "ts-b-other"}`}>
                      {done ? "transcribed" : item.state}
                    </span>
                    {item.inVault && <span className="ts-badge ts-b-vault">in vault</span>}
                  </div>
                </div>

                {s.open && (
                  <div className="ts-body">
                    <div className="ts-controls">
                      <button
                        className="ts-play"
                        onClick={(e) => {
                          e.stopPropagation();
                          playAudio(item);
                        }}
                      >
                        Play audio
                      </button>
                      <button
                        className="ts-copy"
                        onClick={(e) => {
                          e.stopPropagation();
                          void copyPath(item);
                        }}
                        disabled={!item.transcriptPath || item.transcriptPath.trim().length === 0}
                        title={
                          !item.transcriptPath || item.transcriptPath.trim().length === 0
                            ? "Path available once the recording is transcribed"
                            : undefined
                        }
                      >
                        Copy path
                      </button>
                      <button
                        className="ts-vault"
                        onClick={(e) => {
                          e.stopPropagation();
                          void saveToVault(item);
                        }}
                        disabled={item.inVault || !done || s.promoting}
                        title={vaultTitle(item, done)}
                      >
                        {item.inVault ? "In vault" : "Save to vault"}
                      </button>
                      <button
                        className="ts-danger"
                        onClick={(e) => {
                          e.stopPropagation();
                          setPendingDelete(item);
                        }}
                      >
                        Delete
                      </button>
                      <span className="ts-nowplaying">{nowPlaying}</span>
                      <span className="ts-copied">{s.copied}</span>
                      <span className="ts-vaultmsg">{s.vaultMsg}</span>
                    </div>
                    <div className="ts-details">
                      <label className="ts-fld">
                        <span className="ts-lbl">Title</span>
                        <input
                          type="text"
                          value={s.editTitle}
                          onClick={(e) => e.stopPropagation()}
                          onChange={(e) => patchCard(item.recordingId, { editTitle: e.target.value })}
                        />
                      </label>
                      <label className="ts-fld">
                        <span className="ts-lbl">Subtitle</span>
                        <input
                          type="text"
                          value={s.editSubtitle}
                          onClick={(e) => e.stopPropagation()}
                          onChange={(e) => patchCard(item.recordingId, { editSubtitle: e.target.value })}
                        />
                      </label>
                      <label className="ts-fld">
                        <span className="ts-lbl">Summary</span>
                        <textarea
                          value={s.editSummary}
                          onClick={(e) => e.stopPropagation()}
                          onChange={(e) => patchCard(item.recordingId, { editSummary: e.target.value })}
                        />
                      </label>
                      <button
                        className="ts-save"
                        onClick={(e) => {
                          e.stopPropagation();
                          void saveDetails(item);
                        }}
                        disabled={s.saving}
                      >
                        Save details
                      </button>
                      <span className="ts-savemsg">{s.saveMsg}</span>
                    </div>
                    <div className="ts-transcript">{s.transcriptText}</div>
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>

      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete this recording?"
        message={
          pendingDelete === null
            ? ""
            : `Delete "${pendingDelete.title}"? This removes the local transcript and its audio.` +
              (pendingDelete.inVault ? " This recording is saved in the vault; that copy is kept." : "") +
              " This cannot be undone."
        }
        confirmLabel="Delete"
        busyLabel="Deleting..."
        onConfirm={async () => {
          if (pendingDelete !== null) await performDelete(pendingDelete);
        }}
        onClose={() => setPendingDelete(null)}
      />
    </div>
  );
}

// Copy text to the system clipboard. Answers null when it was copied, otherwise the browser's refusal (the
// clipboard API is unavailable or blocked in a non-secure context), so the caller can say why.
async function copyText(text: string): Promise<unknown> {
  try {
    await navigator.clipboard.writeText(text);
    return null;
  } catch (err) {
    return err;
  }
}

function vaultTitle(item: RecordingListItem, done: boolean): string | undefined {
  if (item.inVault) return "Saved in the vault. The vault copy is kept even if you delete this transcript.";
  if (!done) return "Available once the recording is transcribed";
  return undefined;
}

// ---- display helpers (mirroring transcripts.html / Transcripts.razor) ----
function fmtDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, {
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });
}

function fmtDur(ms: number): string {
  const total = Math.round(ms / 1000);
  const m = Math.floor(total / 60);
  const r = total % 60;
  return `${m < 10 ? "0" : ""}${m}:${r < 10 ? "0" : ""}${r}`;
}
