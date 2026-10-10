import { useCallback, useEffect, useRef, useState } from "react";
import {
  deleteScreenshot,
  getScreenshots,
  screenshotFileUrl,
  type ScreenshotInfo,
} from "@devthrottle/client-core/api/client";
import {
  NO_BROKEN_IMAGES,
  isImageBroken,
  markImageBroken,
  type BrokenImageSet,
} from "@devthrottle/client-core/sessions/screenshotGallery";
import { ConfirmDialog } from "../components";

// The screenshots gallery panel (issue #972) - the React port of the Blazor Cockpit screenshots tab.
// The folder lives on the owning Director's machine; the Cockpit asks in the context of the selected
// session, so the session id is the routing key (the Gateway forwards to the Director). The image
// BYTES load same-origin through the Gateway's per-session proxy (screenshotFileUrl) - never a
// Director address - so a loopback-only Director still renders for a remote browser.
//
// The folder can hold thousands of files, so the gallery is deliberately bounded: fetch the newest
// FETCH_COUNT only, render INITIAL_SHOWN thumbnails, and grow on demand. Clicking a thumbnail VIEWS
// it full-size (new tab); Insert drops the Director-side path into the composer; Delete removes the
// file from the Director's disk.
const FETCH_COUNT = 60;
import { describeAndReport, reportShownError } from "@devthrottle/client-core/errors/reportClientError";

// The surface label on every client-error report from this view, so the stored error
// report names where the user was standing (issue #2189).
const SURFACE = "cockpit-screenshots";
// What the card shows in place of a thumbnail the browser could not load (the img element gives no reason).
const IMAGE_UNAVAILABLE = "Image unavailable";
const INITIAL_SHOWN = 12;
const SHOW_MORE_STEP = 24;

export interface ScreenshotsPanelProps {
  sessionId: string | undefined;
  /** Insert a screenshot's on-disk path into the composer. */
  onInsert: (path: string) => void;
}

export function ScreenshotsPanel({ sessionId, onInsert }: ScreenshotsPanelProps) {
  const [shots, setShots] = useState<ScreenshotInfo[]>([]);
  const [total, setTotal] = useState(0);
  const [shown, setShown] = useState(INITIAL_SHOWN);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loadedOnce, setLoadedOnce] = useState(false);
  // Deleting a screenshot removes the file from the Director's disk, so it asks through the shared
  // ConfirmDialog (issue #1244); this holds the file name awaiting confirmation.
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  // Thumbnails whose bytes failed to load (the file vanished on disk). These render the "image
  // unavailable" placeholder instead of the browser's broken-image glyph (issue #1254). A fresh
  // list load clears this back to NO_BROKEN_IMAGES so a re-appeared file gets a fresh chance.
  const [brokenImages, setBrokenImages] = useState<BrokenImageSet>(NO_BROKEN_IMAGES);
  // Whether this list load has already reported a thumbnail that did not load: a folder whose files have gone breaks
  // every thumbnail at once, and that is one failure, not one per image. A fresh list load resets it.
  const brokenReported = useRef(false);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      if (!sessionId) return;
      setBusy(true);
      setError(null);
      try {
        const result = await getScreenshots(sessionId, FETCH_COUNT, signal);
        setShots(result.items);
        setTotal(result.total > 0 ? result.total : result.items.length);
        setShown(INITIAL_SHOWN);
        setBrokenImages(NO_BROKEN_IMAGES);
        brokenReported.current = false;
        setLoadedOnce(true);
      } catch (err) {
        if (signal?.aborted) return;
        setError(describeAndReport(SURFACE, "load the screenshots", err));
      } finally {
        setBusy(false);
      }
    },
    [sessionId],
  );

  // Load on mount and whenever the selected session changes.
  useEffect(() => {
    const controller = new AbortController();
    setShots([]);
    setTotal(0);
    setBrokenImages(NO_BROKEN_IMAGES);
    setLoadedOnce(false);
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // The actual delete, run once the ConfirmDialog is confirmed. A failure is left to throw so the
  // dialog surfaces it (fail loudly) and the list is never touched. On success the gallery is
  // re-synchronized from the Director's disk with a re-fetch rather than an optimistic local edit
  // (issue #1254), so the count and thumbnails stay honest even if other files changed meanwhile.
  const performDelete = useCallback(
    async (fileName: string) => {
      if (!sessionId) return;
      await deleteScreenshot(sessionId, fileName);
      await load();
    },
    [sessionId, load],
  );

  return (
    <div className="shots-panel">
      <div className="shots-head">
        <span className="shots-title">Screenshots</span>
        <button type="button" className="linkbtn" disabled={busy} onClick={() => void load()} title="Reload from the Director">
          Refresh
        </button>
      </div>

      {error !== null && (
        <div className="shots-error">
          {error}{" "}
          <button type="button" className="linkbtn" disabled={busy} onClick={() => void load()}>
            Retry
          </button>
        </div>
      )}

      {error === null && busy && shots.length === 0 && <div className="shots-empty">Loading screenshots...</div>}

      {error === null && loadedOnce && shots.length === 0 && !busy && (
        <div className="shots-empty">No screenshots in this Director's folder yet.</div>
      )}

      {shots.length > 0 && (
        <>
          <div className="gallery">
            {shots.slice(0, shown).map((s) => (
              <div className="shot-card" key={s.fileName}>
                {sessionId &&
                  (isImageBroken(brokenImages, s.fileName) ? (
                    <div className="shot-thumb-missing" role="img" aria-label={`${s.fileName} (image unavailable)`}>
                      {IMAGE_UNAVAILABLE}
                    </div>
                  ) : (
                    <a href={screenshotFileUrl(sessionId, s.fileName)} target="_blank" rel="noreferrer" title="View full size">
                      <img
                        className="shot-thumb"
                        src={screenshotFileUrl(sessionId, s.fileName)}
                        alt={s.fileName}
                        loading="lazy"
                        onError={() => {
                          if (!brokenReported.current) {
                            brokenReported.current = true;
                            reportShownError(SURFACE, "show the screenshot", IMAGE_UNAVAILABLE, { sessionId });
                          }
                          setBrokenImages((prev) => markImageBroken(prev, s.fileName));
                        }}
                      />
                    </a>
                  ))}
                <div className="shot-row">
                  <span className="shot-time">{s.timeLabel}</span>
                  <button type="button" className="linkbtn" title="Insert path into the composer" onClick={() => onInsert(s.path)}>
                    Insert
                  </button>
                  <button type="button" className="linkbtn del" disabled={busy} onClick={() => setPendingDelete(s.fileName)}>
                    Del
                  </button>
                </div>
              </div>
            ))}
          </div>
          {shots.length > shown ? (
            <button type="button" className="gallery-more" onClick={() => setShown((n) => n + SHOW_MORE_STEP)}>
              Show {Math.min(SHOW_MORE_STEP, shots.length - shown)} more
            </button>
          ) : (
            total > shots.length && (
              <div className="gallery-foot">
                Newest {shots.length} of {total} - older files stay on the Director's disk.
              </div>
            )
          )}
        </>
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete this screenshot?"
        message="This removes the image file from the Director's disk. This cannot be undone."
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
