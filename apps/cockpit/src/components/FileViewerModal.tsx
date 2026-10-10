import { useEffect, useState } from "react";
import { classifyFile, formatFileSize } from "@devthrottle/client-core/history/fileTypes";
import type { FileViewerType } from "@devthrottle/client-core/history/fileTypes";
import {
  ensureGatewayCookie,
  fetchSessionFileSize,
  fetchSessionFileText,
  sessionFileUrl,
} from "@devthrottle/client-core/api/client";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { Button } from "./Button";
import { useDismissOnBackdrop } from "./useDismissOnBackdrop";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-file-viewer";
// An <img> that fails to load says nothing about why; the Gateway's answer is not readable from it.
const IMAGE_DID_NOT_LOAD = "the image did not load. The file may be missing (404) or the session's machine offline.";

// Local Files mission (Phase 2): the Cockpit file viewer. A clicked file path - in the Chat tab or in
// the terminal - opens this modal, which renders the file IN PLACE by type, streamed from the owning
// session's machine through the Gateway (sessionFileUrl / fetchSessionFileText). It is modeled on the
// shared ConfirmDialog shell (the ui-modal-backdrop convention) and follows the same fail-loud contract:
// the modal shell shows immediately, the text-fetching modes show "Loading..." until the bytes arrive,
// and any load failure is surfaced as a specific, visible message (never a silent blank).
//
// Render modes come straight from the mission brief (decision 4), keyed by classifyFile():
//   image    -> <img>            (fit within the modal, natural size scrolls)
//   pdf      -> <iframe>         (the browser's native PDF viewer)
//   html     -> SANDBOXED <iframe sandbox="allow-scripts"> - NO allow-same-origin (brief decision 3),
//               so a self-contained report's own chart JS still runs but in a null origin with none of
//               the Gateway's cookie authority.
//   markdown -> fetch the text, render with the shared sanitized markdownToHtml()
//   text     -> fetch the text, show in a <pre> (covers code files too)
//   download -> unknown/binary: no guessed render; show the file name and a plain Download link.

export interface FileViewerModalProps {
  /** The session whose machine holds the file; the routing key for the Gateway proxy. */
  sessionId: string;
  /** The absolute file path on that machine, exactly as detected in chat or the terminal. */
  path: string;
  /** Runs when the viewer is dismissed (backdrop click, Escape, or the Close button). */
  onClose: () => void;
}

/** The final path segment shown as the file's name (handles both \ and / separators). */
function baseName(path: string): string {
  const segments = path.replace(/\\/g, "/").split("/");
  return segments[segments.length - 1] || path;
}

export function FileViewerModal({ sessionId, path, onClose }: FileViewerModalProps) {
  // The image / PDF / HTML modes render a bare <img>/<iframe> whose src hits a gated Gateway route and
  // so cannot carry a Bearer header; like the terminal stream (terminal/stream.ts), they authenticate
  // through the cc-gateway-token cookie. Re-mirror it on mount so it is present the moment those
  // elements load even if the startup cookie was evicted. Synchronous: an effect would run too late.
  ensureGatewayCookie();

  const type = classifyFile(path);
  const url = sessionFileUrl(sessionId, path);
  const name = baseName(path);

  // Escape dismisses the viewer, matching ConfirmDialog.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  // Backdrop dismissal that survives a mouse drag - selecting text in a viewed file and releasing
  // outside the panel must not close the viewer (see useDismissOnBackdrop).
  const dismiss = useDismissOnBackdrop(onClose);

  return (
    <div className="ui-modal-backdrop" {...dismiss}>
      <div
        className="file-viewer"
        role="dialog"
        aria-modal="true"
        aria-label={name}
      >
        <div className="file-viewer-head">
          <span className="file-viewer-name" title={path}>{name}</span>
          <div className="file-viewer-head-actions">
            <a className="file-viewer-download" href={url} download={name}>Download</a>
            <Button variant="secondary" onClick={onClose}>Close</Button>
          </div>
        </div>
        <div className="file-viewer-body">
          <FileViewerContent type={type} url={url} name={name} sessionId={sessionId} path={path} />
        </div>
      </div>
    </div>
  );
}

function FileViewerContent(props: {
  type: FileViewerType;
  url: string;
  name: string;
  sessionId: string;
  path: string;
}) {
  const { type, url, name, sessionId, path } = props;
  switch (type) {
    case "image":
      return <ImageFile sessionId={sessionId} url={url} name={name} />;
    case "pdf":
      return <iframe className="file-viewer-frame" src={url} title={name} />;
    case "html":
      // Sandboxed WITHOUT allow-same-origin (brief decision 3): a self-contained report's own script
      // runs, but in a null origin, so it cannot use the Gateway's cookie authority to call its APIs.
      return <iframe className="file-viewer-frame" src={url} title={name} sandbox="allow-scripts" />;
    case "markdown":
      return <TextFile sessionId={sessionId} path={path} render="markdown" />;
    case "text":
      return <TextFile sessionId={sessionId} path={path} render="text" />;
    case "download":
    default:
      return <DownloadPanel sessionId={sessionId} path={path} url={url} name={name} />;
  }
}

// The unknown/binary case: no guessed render (brief decision 4), just the file NAME, its SIZE, and a
// Download button. The size is probed through the Gateway (a one-byte ranged GET; fetchSessionFileSize).
// While it loads the button is already usable. A 404/503 during the probe means the file is genuinely
// missing or the machine is offline, so the panel shows that specific reason instead of offering a
// Download that would only fail - keeping the fail-loud contract for this mode too. A size that cannot
// be determined simply shows the name with no size (never a fake one).
function DownloadPanel({
  sessionId,
  path,
  url,
  name,
}: {
  sessionId: string;
  path: string;
  url: string;
  name: string;
}) {
  const [size, setSize] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setSize(null);
    setError(null);
    fetchSessionFileSize(sessionId, path, controller.signal)
      .then((bytes) => setSize(bytes))
      .catch((err) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport(SURFACE, "read the file's size", err, { sessionId }));
      });
    return () => controller.abort();
  }, [sessionId, path]);

  if (error !== null) return <div className="file-viewer-error">{error}</div>;

  const sizeLabel = size === null ? "" : formatFileSize(size);
  return (
    <div className="file-viewer-download-panel">
      <p className="file-viewer-download-note">This file type has no in-app preview.</p>
      <p className="file-viewer-download-name">{name}</p>
      {sizeLabel ? <p className="file-viewer-download-size">{sizeLabel}</p> : null}
      <a className="file-viewer-download-btn" href={url} download={name}>Download</a>
    </div>
  );
}

// An image renders directly from the Gateway URL. A failed load (missing file / offline machine) shows
// a specific message rather than a broken-image icon, keeping the fail-loud contract for this mode too.
function ImageFile({ sessionId, url, name }: { sessionId: string; url: string; name: string }) {
  const [failed, setFailed] = useState<string | null>(null);
  if (failed !== null) return <div className="file-viewer-error">{failed}</div>;
  return (
    <div className="file-viewer-scroll">
      <img
        className="file-viewer-image"
        src={url}
        alt={name}
        onError={() => setFailed(describeAndReport(SURFACE, "show the image", new Error(IMAGE_DID_NOT_LOAD), { sessionId }))}
      />
    </div>
  );
}

// The Markdown and text/code modes need the file's STRING, not an <img>/<iframe> src, so they fetch the
// text through the Gateway. Loading shows "Loading..."; a failure shows the specific reason.
function TextFile({
  sessionId,
  path,
  render,
}: {
  sessionId: string;
  path: string;
  render: "markdown" | "text";
}) {
  const [text, setText] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setText(null);
    setError(null);
    fetchSessionFileText(sessionId, path, controller.signal)
      .then((t) => setText(t))
      .catch((err) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport(SURFACE, "open the file", err, { sessionId }));
      });
    return () => controller.abort();
  }, [sessionId, path]);

  if (error !== null) return <div className="file-viewer-error">{error}</div>;
  if (text === null) return <div className="file-viewer-loading">Loading...</div>;
  if (render === "markdown") {
    // markdownToHtml is the shared sanitized renderer both chat views already trust (XSS-safe).
    return <div className="file-viewer-md" dangerouslySetInnerHTML={{ __html: markdownToHtml(text) }} />;
  }
  return <pre className="file-viewer-pre">{text}</pre>;
}
