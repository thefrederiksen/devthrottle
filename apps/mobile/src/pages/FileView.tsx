import { useEffect, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { classifyFile, formatFileSize } from "@devthrottle/client-core/history/fileTypes";
import type { FileViewerType } from "@devthrottle/client-core/history/fileTypes";
import {
  GatewayError,
  authHeaders,
  ensureGatewayCookie,
  fetchSessionFileSize,
  fetchSessionFileText,
  sessionFileUrl,
} from "@devthrottle/client-core/api/client";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { reportShownError } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "mobile-file-view";
const IMAGE_FAILED = "Could not load image. The file may be missing (404) or the session's machine offline.";

// Local Files mission (Phase 3): the mobile /m file viewer. A clicked file path - in the Chat page or
// in the Terminal mirror - navigates to this FULL-SCREEN ROUTE (mobile has no modal shell; every view
// is a route), which renders the file IN PLACE by type, streamed from the owning session's machine
// through the Gateway (sessionFileUrl / fetchSessionFileText). Only the shell differs from the Cockpit
// FileViewerModal - a page, not a modal - the render logic mirrors it exactly (brief decision 4):
//   image    -> <img>            (fit within the body, natural size scrolls)
//   pdf      -> <iframe>         (the browser's native PDF viewer; iOS Safari may not render it inline)
//   html     -> SANDBOXED <iframe sandbox="allow-scripts"> - NO allow-same-origin (brief decision 3),
//               so a self-contained report's own chart JS still runs but in a null origin with none of
//               the Gateway's cookie authority.
//   markdown -> fetch the text, render with the shared sanitized markdownToHtml()
//   text     -> fetch the text, show in a <pre> (covers code files too; horizontal scroll on a phone)
//   download -> unknown/binary: no guessed render; show the file name and a plain Download link.
//
// The absolute file path rides as a ?path= query param so a hard reload / deep link resolves it too
// (route state would be lost on refresh). The shell shows immediately; the text-fetching modes show
// "Loading..." until the bytes arrive; any load failure is a specific, visible message (never a silent
// blank). Back returns to the session the file was opened from (browser back).

/** The final path segment shown as the file's name (handles both \ and / separators). */
function baseName(path: string): string {
  const segments = path.replace(/\\/g, "/").split("/");
  return segments[segments.length - 1] || path;
}

// Turn a file-load failure into a specific, human message. A 404 is a missing file; a 503 is the
// owning session's machine being offline (the Gateway could not reach the Director). Anything else
// shows its status so the failure is never mistaken for an empty file. Shown AND reported in one act (issue #3675);
// the report carries the session, never the file's path.
function fileLoadMessage(err: unknown, action: string, sessionId: string): string {
  return reportShownError(SURFACE, action, fileLoadSentence(err), { sessionId }, err);
}

function fileLoadSentence(err: unknown): string {
  if (err instanceof GatewayError) {
    if (err.status === 404) return "Could not load file: not found (404). It may have been moved or deleted.";
    if (err.status === 503) return "Could not load file: the session's machine is offline (503).";
    return `Could not load file: ${err.status}`;
  }
  return `Could not load file: ${err instanceof Error ? err.message : String(err)}`;
}

export function FileView() {
  // The image / PDF / HTML modes below render a bare <img>/<iframe> whose src hits a gated Gateway
  // route. Those elements cannot carry a Bearer header, so - exactly like the terminal stream before it
  // opens its WebSocket (terminal/stream.ts) - they authenticate through the cc-gateway-token cookie.
  // Re-mirror it here, on mount, so the cookie is present the moment those elements load even if an
  // installed PWA evicted the app-startup cookie between launches (the "Could not load image" failure).
  // Synchronous, not an effect: an effect runs after the first paint, too late for the initial request.
  ensureGatewayCookie();

  const { sessionId } = useParams<{ sessionId: string }>();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const path = params.get("path") ?? "";

  // Back returns to the tab the file was opened from. Chat/Terminal pass that tab as ?from= when they
  // navigate here, so Back is an EXPLICIT route, not history.back(): navigate(-1) does nothing when the
  // viewer was reached with no in-app history to pop - a hard reload, a deep link, or a link that opened
  // a fresh context - which left the Back button dead and forced an app restart. With no ?from we still
  // have a working destination: the session itself (its Chat), so Back is never a no-op.
  const from = params.get("from");
  const goBack = () => {
    if (from) navigate(from);
    else if (sessionId) navigate(`/session/${sessionId}`);
    else navigate("/");
  };

  if (!sessionId || !path) {
    return (
      <div className="terminal-screen">
        <header className="app-bar">
          <button type="button" className="file-view-back" onClick={goBack}>Back</button>
          <h1 className="term-title">File</h1>
        </header>
        <div className="file-view-body">
          <div className="file-view-error">No file path was provided.</div>
        </div>
      </div>
    );
  }

  const type = classifyFile(path);
  const url = sessionFileUrl(sessionId, path);
  const name = baseName(path);

  return (
    <div className="terminal-screen">
      <header className="app-bar">
        <button type="button" className="file-view-back" onClick={goBack}>Back</button>
        <h1 className="term-title" title={path}>{name}</h1>
        <DownloadButton url={url} name={name} sessionId={sessionId} />
      </header>
      <div className="file-view-body">
        <FileViewContent type={type} url={url} name={name} sessionId={sessionId} path={path} />
      </div>
    </div>
  );
}

// The Download control. The old bare <a download> gave the phone NO sign a tap registered - so a file
// that saved silently (or failed silently) looked like nothing happened, and the button got tapped over
// and over. This fetches the bytes itself (carrying the Bearer, so it works regardless of the cookie),
// shows "Saving..." while it runs, then a visible "Saved ... to your device" (or the specific failure),
// and only then triggers the browser's save. One in-flight download at a time.
function DownloadButton({ url, name, sessionId }: { url: string; name: string; sessionId: string }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const onDownload = async () => {
    if (busy) return;
    setBusy(true);
    setNote(`Saving ${name}...`);
    try {
      const res = await fetch(url, { headers: authHeaders() });
      if (!res.ok) throw new GatewayError(res.status, `download failed: ${res.status}`);
      const blob = await res.blob();
      const objectUrl = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = objectUrl;
      a.download = name;
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.setTimeout(() => URL.revokeObjectURL(objectUrl), 15000);
      setNote(`Saved ${name} to your device`);
    } catch (err) {
      setNote(fileLoadMessage(err, "save the file", sessionId));
    } finally {
      setBusy(false);
      window.setTimeout(() => setNote(null), 4000);
    }
  };

  return (
    <>
      <button type="button" className="file-view-download" onClick={onDownload} disabled={busy}>
        {busy ? "Saving..." : "Download"}
      </button>
      {note !== null && <div className="file-view-download-toast" role="status">{note}</div>}
    </>
  );
}

function FileViewContent(props: {
  type: FileViewerType;
  url: string;
  name: string;
  sessionId: string;
  path: string;
}) {
  const { type, url, name, sessionId, path } = props;
  switch (type) {
    case "image":
      return <ImageFile url={url} name={name} sessionId={sessionId} />;
    case "pdf":
      return <iframe className="file-view-frame" src={url} title={name} />;
    case "html":
      // Sandboxed WITHOUT allow-same-origin (brief decision 3): a self-contained report's own script
      // runs, but in a null origin, so it cannot use the Gateway's cookie authority to call its APIs.
      return <iframe className="file-view-frame" src={url} title={name} sandbox="allow-scripts" />;
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
// be determined simply shows the name with no size (never a fake one). Mirrors the Cockpit DownloadPanel.
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
        setError(fileLoadMessage(err, "read the file's size", sessionId));
      });
    return () => controller.abort();
  }, [sessionId, path]);

  if (error !== null) return <div className="file-view-error">{error}</div>;

  const sizeLabel = size === null ? "" : formatFileSize(size);
  return (
    <div className="file-view-download-panel">
      <p className="file-view-download-note">This file type has no in-app preview.</p>
      <p className="file-view-download-name">{name}</p>
      {sizeLabel ? <p className="file-view-download-size">{sizeLabel}</p> : null}
      <a className="file-view-download-btn" href={url} download={name}>Download</a>
    </div>
  );
}

// An image renders directly from the Gateway URL. A failed load (missing file / offline machine) shows
// a specific message rather than a broken-image icon, keeping the fail-loud contract for this mode too.
function ImageFile({ url, name, sessionId }: { url: string; name: string; sessionId: string }) {
  const [failed, setFailed] = useState<string | null>(null);
  if (failed !== null) return <div className="file-view-error">{failed}</div>;
  return (
    <div className="file-view-scroll">
      <img
        className="file-view-image"
        src={url}
        alt={name}
        onError={() => setFailed(reportShownError(SURFACE, "show the image", IMAGE_FAILED, { sessionId }))}
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
        setError(fileLoadMessage(err, "open the file", sessionId));
      });
    return () => controller.abort();
  }, [sessionId, path]);

  if (error !== null) return <div className="file-view-error">{error}</div>;
  if (text === null) return <div className="file-view-loading">Loading...</div>;
  if (render === "markdown") {
    // markdownToHtml is the shared sanitized renderer both chat views already trust (XSS-safe).
    return <div className="file-view-md" dangerouslySetInnerHTML={{ __html: markdownToHtml(text) }} />;
  }
  return <pre className="file-view-pre">{text}</pre>;
}
