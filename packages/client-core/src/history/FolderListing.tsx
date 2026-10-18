import { useEffect, useState } from "react";
import { fetchSessionFolder } from "../api/client";
import type { SessionFolderListing } from "../api/client";
import { describeAndReport } from "../errors/reportClientError";

// Issue #4078 / #4084: a folder this session may see, through the session folder browser
// (GET /sessions/{sid}/folder). ONE component for both shells - the Cockpit's file viewer and the phone's
// file page - so the two cannot drift: the same entries, the same order, the same words. The Director already
// filtered every entry to what opening it would admit, so nothing here decides what may be shown. Each shell
// supplies only what opening an entry means for it (the Cockpit moves its modal, the phone navigates to the
// entry's own page) and styles the shared folder-listing-* classes in its own stylesheet.

export interface FolderListingProps {
  /** The session whose machine holds the folder; the routing key for the Gateway proxy. */
  sessionId: string;
  /** The folder's absolute path on that machine. */
  path: string;
  /** Runs with an entry's path when it is chosen: a file to render, a folder to list. */
  onOpen: (entryPath: string) => void;
  /** The surface name a failure is reported under (e.g. "cockpit-file-viewer", "mobile-file-view"). */
  surface: string;
}

export function FolderListing({ sessionId, path, onOpen, surface }: FolderListingProps) {
  const [listing, setListing] = useState<SessionFolderListing | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setListing(null);
    setError(null);
    fetchSessionFolder(sessionId, path, controller.signal)
      .then((result) => setListing(result))
      .catch((err) => {
        if (controller.signal.aborted) return;
        setError(describeAndReport(surface, "open the folder", err, { sessionId }));
      });
    return () => controller.abort();
  }, [sessionId, path, surface]);

  if (error !== null) return <div className="folder-listing-error">{error}</div>;
  if (listing === null) return <div className="folder-listing-loading">Loading...</div>;
  return (
    <div className="folder-listing-scroll">
      <p className="folder-listing-note">This is a folder. Choose a file to open it.</p>
      <ul className="folder-listing" aria-label="Folder contents">
        {listing.parentPath !== null && (
          <li>
            <button type="button" className="folder-listing-entry folder" onClick={() => onOpen(listing.parentPath!)}>
              .. (up one folder)
            </button>
          </li>
        )}
        {listing.entries.map((entry) => (
          <li key={entry.path}>
            <button
              type="button"
              className={`folder-listing-entry ${entry.isFile ? "file" : "folder"}`}
              title={entry.path}
              onClick={() => onOpen(entry.path)}
            >
              {entry.isFile ? entry.name : `${entry.name}/`}
            </button>
          </li>
        ))}
      </ul>
      {listing.entries.length === 0 && <p className="folder-listing-note">This folder has no files to show.</p>}
    </div>
  );
}
