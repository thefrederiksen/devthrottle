import type { SessionDto } from "@devthrottle/client-core/api/client";
import { dotHex } from "@devthrottle/client-core/sessions/ordering";
import {
  crewAge,
  crewSummary,
  crewSummaryLine,
  descendantsOf,
  type SessionTree,
} from "@devthrottle/client-core/sessions/tree";
import { useNow as useSharedNow } from "@devthrottle/client-core/polling/useNow";

// The crew line on a collapsed parent: one dot per session under it, in their order and colours (so
// collapsing hides no colour), the counts, and how long the crew has been going. Ticks on the shared
// one-second clock for the age. Shared by the Sessions list and the Fleet Map, so a collapsed crew reads
// the same on both.
export function CrewLine({ root, tree }: { root: SessionDto; tree: SessionTree }) {
  const now = useSharedNow();
  const kids = descendantsOf(tree, root).map((d) => d.session);
  const sum = crewSummary(root, kids);
  const age = crewAge(sum, now);
  return (
    <span className="roster-crew" title={crewSummaryLine(sum)}>
      <span className="roster-crew-strip" aria-hidden="true">
        {kids.map((k) => (
          <i key={k.sessionId} style={{ backgroundColor: dotHex(k) }} />
        ))}
      </span>
      <span className={sum.needsYou > 0 ? "roster-crew-text alarm" : "roster-crew-text"}>{crewSummaryLine(sum)}</span>
      {age.length > 0 && <span className="roster-crew-age">{age}</span>}
    </span>
  );
}
