import { useEffect, useState } from "react";
import { getSuggestionCount } from "@devthrottle/client-core/dictation/dictionaryClient";

// The pending dictionary-suggestions count (devthrottle #2075) - the Gateway-owned verdict, rendered verbatim: a dot on
// your initials, a count on the Settings row of the menu behind your name, and a count on the Dictionary tab of
// Settings (owner, 8 Oct 2026). Polled so the attention signal shows without opening the page, and re-read whenever
// `refreshKey` changes - the shell passes the route, Settings passes its tab - so applying or dismissing a suggestion
// updates it promptly. The client renders the number; it never decides it.
export function useSuggestionCount(enabled: boolean, refreshKey: string): number {
  const [count, setCount] = useState(0);
  useEffect(() => {
    if (!enabled) return undefined;
    let cancelled = false;
    const poll = () =>
      void getSuggestionCount().then((n) => {
        if (!cancelled) setCount(n);
      });
    poll();
    const id = window.setInterval(poll, 45_000);
    return () => {
      cancelled = true;
      window.clearInterval(id);
    };
  }, [enabled, refreshKey]);
  return count;
}
