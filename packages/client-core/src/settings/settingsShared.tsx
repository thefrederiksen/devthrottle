import { useCallback, useEffect, useRef, useState } from "react";
import { getGatewaySettings, type GatewaySettings } from "./settingsClient";
import { gatewayErrorMessage } from "../api/client";
import type { AiModel } from "../api/ai";
import { reportShownError } from "../errors/reportClientError";

/** Where a Settings failure is filed. Settings is one page on both shells, so one surface. */
const SETTINGS_SURFACE = "settings";

// The pieces every Settings card is built from, shared by the Cockpit and the phone: the card heading
// with its scope pill, the Gateway settings document loader, the read-only row, and the small helpers
// that keep a <select> honest. One implementation, so a card cannot look or behave differently
// depending on which screen you opened it on.

// The scope of a setting: how far a change to it reaches. Rendered as the pill in every card heading.
export type Scope = "this device" | "your account" | "included";

// The scope pill for a per-account setting (issue #2022): always "your account" now that self-host is the
// hosted Gateway with one tenant. Held as a constant so every per-account card reads the same word.
export const ACCOUNT_SCOPE: Scope = "your account";

export function CardHead({ title, scope }: { title: string; scope: Scope }) {
  return (
    <h2 className="settings-h2">
      {title} <span className="settings-pill">{scope}</span>
    </h2>
  );
}

// ---- The Gateway settings document -----------------------------------------------------------------
//
// The Notifications tab's snooze and time-zone cards each read this one document, so the load/error/busy/
// message plumbing lives here once. Each consumer mounts its own copy, which means one fetch per card - and
// tabs unmount when you switch, so nothing lingers.
//
// runSave wraps a mutation in the immediate-feedback contract every card owes the user (CodingStyle.md):
// the controls disable and the message reads "Saving..." before the call goes out, the caller's returned
// sentence replaces it on success, and a failure reports the Gateway's own error rather than a fabricated
// value. The try/catch is here because this IS the event-handler entry point.
//
// It also refuses to start a second save while one is in flight. Every caller applies its result over the
// settings snapshot its render captured, so two overlapping saves would let the slower one write back a
// value the faster one had already superseded. Disabling the controls is not enough on its own - that
// stops a click, not a keyboard repeat or a queued change event - so the invariant is enforced here, once,
// for every caller. The guard reads a ref because this callback is created once and would otherwise close
// over the first render's `busy`.
export function useGatewaySettings() {
  const [settings, setSettings] = useState<GatewaySettings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const busyRef = useRef(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setError(null);
      setSettings(await getGatewaySettings(signal));
    } catch (e) {
      if (signal?.aborted) return;
      setError(errText(e, "load the settings"));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const runSave = useCallback(async (apply: () => Promise<string>) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMsg("Saving...");
    try {
      setMsg(await apply());
    } catch (e) {
      setMsg(errText(e, "save the settings"));
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, []);

  return { settings, setSettings, error, busy, msg, setMsg, runSave };
}

/** A read-only label/value row, used by the Transcription tab's model line. */
export function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="settings-row">
      <div className="settings-row-label">{label}</div>
      <div className="settings-row-value">{value}</div>
    </div>
  );
}

// Build the option id list, guaranteeing the currently-saved id is present + first even when the catalog
// failed to load or does not list it (so the <select> value always matches an option).
export function ensureIds(current: string, models: AiModel[]): string[] {
  const ids = models.map((m) => m.id);
  if (current && ids.indexOf(current) < 0) ids.unshift(current);
  return ids;
}

export function ensureStrings(current: string, values: string[]): string[] {
  const out = values.slice();
  if (current && out.indexOf(current) < 0) out.unshift(current);
  return out;
}

/** A settings failure, shown AND reported in one act (the Error Logging mission, issue #3675). The sentence names
 *  no action, as it always has - the card around it already says what was being done; `action` labels the stored
 *  report. */
export function errText(e: unknown, action: string): string {
  return reportShownError(SETTINGS_SURFACE, action, gatewayErrorMessage(e), undefined, e);
}
