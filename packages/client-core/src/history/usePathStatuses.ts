import { useEffect, useMemo, useState } from "react";
import { fetchPathStatuses, PATH_STATUS_NOT_ON_DISK, type PathStatus } from "../api/client";
import { backgroundRecovered, describeAndReport } from "../errors/reportClientError";
import type { RenderedBubble } from "./chatView";

const ACTION = "check which paths are on disk";

// Issue #4078: what each path the Chat shows is for this session, asked of the session's own Director in ONE
// call whenever the set of paths on screen changes (not on every poll - the set is keyed by its contents). The
// Chat greys out a path that is not on disk before anyone clicks it. The Director decides; this only carries
// its answer to the chip.
//
// A failure leaves every chip as it was - a plain, clickable path - because the hint is extra information, and
// a click still asks the Director and shows its reason. The failure is reported once as a background error, so
// a Director too old to answer is visible in the error log without a banner over the conversation.
export function usePathStatuses(
  sessionId: string | undefined,
  bubbles: RenderedBubble[],
  surface: string,
): Map<string, PathStatus> {
  const paths = useMemo(() => {
    const distinct = new Set<string>();
    for (const bubble of bubbles) {
      for (const link of bubble.links) {
        if (!link.isUrl) distinct.add(link.text);
      }
    }
    return [...distinct].sort();
  }, [bubbles]);
  const key = paths.join("\n");

  const [statuses, setStatuses] = useState<Map<string, PathStatus>>(new Map());

  useEffect(() => {
    if (!sessionId || paths.length === 0) {
      setStatuses(new Map());
      return;
    }
    const controller = new AbortController();
    fetchPathStatuses(sessionId, paths, controller.signal)
      .then((rows) => {
        backgroundRecovered(surface, ACTION);
        setStatuses(new Map(rows.map((row) => [row.path, row])));
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        describeAndReport(surface, ACTION, err, { sessionId, background: true });
      });
    return () => controller.abort();
    // `key` stands for `paths`: the effect re-runs when the SET of paths changes, not when a poll rebuilds an
    // identical array.
  }, [sessionId, key, surface]);

  return statuses;
}

/** True when the Director said this path is not on disk - the chip is greyed and labelled "not on disk". */
export function isNotOnDisk(statuses: Map<string, PathStatus>, path: string): boolean {
  return statuses.get(path)?.code === PATH_STATUS_NOT_ON_DISK;
}
