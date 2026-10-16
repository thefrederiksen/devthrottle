import { useEffect, useState } from "react";
import {
  fetchPathStatuses,
  PATH_STATUS_FOLDER,
  PATH_STATUS_NOT_ALLOWED,
  PATH_STATUS_NOT_ON_DISK,
  type PathStatus,
} from "../api/client";
import { describeAndReport } from "../errors/reportClientError";

// Issue #4078: what ONE path is for this session, asked of the session's Director before a file viewer renders
// it - the Cockpit modal and the mobile page share this, so both say the same thing for the same path.
//
// "unknown" means the question could not be answered (a Director too old to know the path-status verb). The
// viewer then renders by file type as it always did, and each render mode still shows the Director's reason
// if the read itself is refused - the old behaviour, not a guess.
export type PathState =
  | { kind: "loading" }
  | { kind: "known"; status: PathStatus }
  | { kind: "unknown" };

export function usePathStatus(sessionId: string, path: string, surface: string): PathState {
  const [state, setState] = useState<PathState>({ kind: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: "loading" });
    fetchPathStatuses(sessionId, [path], controller.signal)
      .then((rows) => {
        const status = rows.find((row) => row.path === path);
        setState(status ? { kind: "known", status } : { kind: "unknown" });
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        describeAndReport(surface, "check what the path is", err, { sessionId, background: true });
        setState({ kind: "unknown" });
      });
    return () => controller.abort();
  }, [sessionId, path, surface]);

  return state;
}

/** The Director's sentence when it refused the path (not on disk, not allowed), or null when it did not. */
export function refusalSentence(state: PathState): string | null {
  if (state.kind !== "known") return null;
  const { code, sentence } = state.status;
  return code === PATH_STATUS_NOT_ON_DISK || code === PATH_STATUS_NOT_ALLOWED ? sentence : null;
}

/** True when the Director said the path is a folder. */
export function isFolder(state: PathState): boolean {
  return state.kind === "known" && state.status.code === PATH_STATUS_FOLDER;
}
