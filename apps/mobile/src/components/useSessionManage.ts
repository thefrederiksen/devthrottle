import { useCallback, useEffect, useRef, useState } from "react";
import {
  holdSession,
  listSessions,
  stopSession,
  type SessionStopOutcome,
} from "@devthrottle/client-core/api/client";
import { classify, isDeferredHold, isWorking, snoozeCountdown } from "@devthrottle/client-core/sessions/ordering";
import { promptDeliveryNotice } from "@devthrottle/client-core/sessions/delivery";
import {
  isSnoozing,
  optimisticHoldFor,
  optimisticHoldToggle,
  reconcileHoldToggle,
  type HoldToggleOutcome,
  type HoldUiState,
} from "@devthrottle/client-core/sessions/snoozeAction";

// The session management verbs (Snooze/Unsnooze + Stop) for ONE session, hoisted out of the old
// SessionManageBar so two places can drive them from one copy of the state: the app bar's overflow
// menu (Stop, and Snooze on the screens with no room for it) and the Voice mode bottom bar (Snooze
// next to Respond). Owning this in a hook is what stops the two surfaces disagreeing about the live
// held state.
//
// The held state is polled from the SAME roster the Home page reads, so the session screen and the
// roster always agree, and a hold toggled elsewhere (the desktop) shows up here.
//
// SNOOZE MUST FEEL INSTANT (owner's #1 annoyance). Two things make it so, in every case:
//   - a hold has a TRI-STATE (held / deferred / none), not a boolean. Snoozing a WORKING session DEFERS
//     (its clock starts when the work ends - owner ruling), which the Gateway returns as
//     { onHold: false, pending: true }. The old client read only onHold, so a deferred snooze showed NO
//     change and read as "the button does nothing". `deferred` carries that state to the surfaces.
//   - the tap updates the UI OPTIMISTICALLY (before the server answers) and then triggers an IMMEDIATE
//     roster re-sync, so neither the button nor the pill wait up to a poll interval to reflect the snooze.

const POLL_INTERVAL_MS = 4000;

export interface SessionManage {
  onHold: boolean | null;
  held: boolean;
  // A DEFERRED snooze: asked for while the agent was working, so it arms when the work ends. Distinct
  // from `held` (armed now) - a caller that read only `held` would show nothing for a deferred snooze.
  deferred: boolean;
  // The FOLD's verdict for DISPLAY (classify === "onHold"), distinct from the raw `held` the toggle uses.
  // A Held session that has started working is blue "Working" and must NOT read "snoozed" - working wins in
  // the fold, so a display pill reads this, never the raw onHold flag.
  snoozed: boolean;
  // "wakes in 3h 48m" from the Gateway-owned snooze clock, or null when there is no running clock.
  holdCountdown: string | null;
  // "Your last prompt was not delivered ..." - the Gateway's sentence for a prompt that never reached the
  // agent (issue internal#811), or null when nothing was lost. Rendered VERBATIM by the app bar, so every
  // per-session screen says it, not just the one the user happened to be on when it happened.
  deliveryNotice: string | null;
  busy: boolean;
  error: string | null;
  setError: (message: string | null) => void;
  /** Resolves true when the hold change was accepted by the Gateway, false when it failed (the error
   *  is already surfaced) - so a caller that navigates away after a snooze can stay put on failure. */
  toggleHold: () => Promise<boolean>;
  /** Snooze for an EXPLICIT length in minutes (the voice screen's length picker), instead of letting the
   *  Gateway apply the user's default. Always a hold, never an unsnooze: picking a length while already
   *  snoozed re-arms the clock to that length. Resolves true when the Gateway accepted it. */
  holdFor: (minutes: number) => Promise<boolean>;
  /** Stop the session: end the agent process on its machine and take it off the roster.
   *
   *  The reason is REQUIRED - the Gateway will not carry a stop without one and records it with the stop
   *  (mission "Stop a session", Ruling 4) - and it is the caller's job to have collected one.
   *
   *  IT SENDS ONE STOP AT A TIME. A second call made while the first is still outstanding is REFUSED -
   *  it throws without reaching the Gateway - so a caller cannot get two stops in flight and then let
   *  their two independently completing handlers overwrite each other's answer.
   *
   *  IT RESOLVES WITH THE GATEWAY'S ANSWER AND NAVIGATES NOWHERE. The old removeSession went straight to
   *  the roster the instant the call returned, which threw the answer away before anyone could read it -
   *  the same silent success the Cockpit had. Leaving here is the caller's decision, taken once the user
   *  has read what happened. It throws on a failure, with the error already surfaced on `error`. */
  stopSession: (reason: string) => Promise<SessionStopOutcome>;
}

export function useSessionManage(sessionId: string | undefined): SessionManage {
  const [onHold, setOnHold] = useState<boolean | null>(null);
  const [deferred, setDeferred] = useState(false);
  const [working, setWorking] = useState(false);
  const [snoozed, setSnoozed] = useState(false);
  const [holdCountdown, setHoldCountdown] = useState<string | null>(null);
  const [deliveryNotice, setDeliveryNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // While a toggle is in flight the optimistic state must not be clobbered by a slower poll.
  const pendingRef = useRef(false);
  // Whether a STOP request is outstanding. A ref and not the `busy` flag: two Enter presses land in the
  // same tick and both read the same stale busy, so a state check lets the second one straight through.
  const stopInFlightRef = useRef(false);

  // Hoisted out of the effect so toggleHold can call it for an IMMEDIATE re-sync after a snooze, instead
  // of leaving the button/pill stale until the next interval. Reads the SAME roster the Home page reads.
  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!sessionId) return;
    try {
      const all = await listSessions(signal);
      if (pendingRef.current) return;
      const match = all.find((s) => s.sessionId === sessionId);
      if (match) {
        // The toggle needs the raw hold (what it will flip); the DISPLAY reads the fold (working wins).
        setOnHold(Boolean(match.onHold));
        // The Gateway-owned tri-state: DeferredHold is a real snooze that has not armed yet.
        setDeferred(isDeferredHold(match));
        // Whether snoozing NOW would defer (working) or arm (settled) - drives the optimistic affordance.
        setWorking(isWorking(match));
        setHoldCountdown(snoozeCountdown(match));
        // A prompt that did not reach the agent (issue internal#811). Read on the SAME poll as the hold
        // state so the banner appears without the user doing anything - the failure happened while they
        // were looking at some other screen, which is exactly why it has to find them.
        setDeliveryNotice(promptDeliveryNotice(match));
        // classify fails loud against a Gateway that did not stamp triageBucket; in this polling loop a
        // mixed-version blip must not throw the refresh, so keep the last verdict on that rare miss.
        try {
          setSnoozed(classify(match) === "onHold");
        } catch {
          /* keep the last-known snoozed verdict */
        }
      }
    } catch {
      /* keep the last-known held state; the actions surface their own errors */
    }
  }, [sessionId]);

  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId, refresh]);

  // The one write path both snooze verbs take: show the optimistic state now, POST, then settle on the
  // TRUE outcome. The plain toggle and the length picker differ ONLY in what they ask for (`desired` +
  // an optional length) and in what to show while it is in flight - everything after that, including the
  // rollback that stops a false "Snoozed", must be identical for both, so it is written once here.
  const applyHold = useCallback(async (
    desired: boolean,
    optimistic: HoldUiState,
    snoozeMinutes?: number,
  ): Promise<boolean> => {
    if (!sessionId) return false;
    // The pre-tap state: what a FAILED /hold must roll back to.
    const preTap: HoldUiState = { held: onHold === true, deferred };
    setBusy(true);
    pendingRef.current = true;
    setError(null);
    setOnHold(optimistic.held);
    setDeferred(optimistic.deferred);
    let outcome: HoldToggleOutcome;
    try {
      outcome = { ok: true, response: await holdSession(sessionId, desired, snoozeMinutes) };
    } catch (err) {
      // /hold FAILED: the snooze did NOT happen. Surface the error; the rollback below returns the UI to
      // its pre-tap state so the button never falsely reads "Snoozed"/"Snoozing when it finishes".
      outcome = { ok: false };
      setError(err instanceof Error ? err.message : "Hold failed");
    } finally {
      pendingRef.current = false;
      setBusy(false);
    }
    // Settle on the TRUE server outcome: the authoritative tri-state on success, or the pre-tap state on
    // failure (never a false success). Only on success re-sync from the roster fold NOW, killing the
    // up-to-a-poll-interval lag so the countdown, the snoozed pill and the armed/deferred split land
    // immediately. On FAILURE we must NOT refresh: a roster blip could re-assert the optimistic snooze,
    // or a failed refresh would keep the last-known (optimistic) state - either way a false "Snoozed".
    const settled = reconcileHoldToggle(preTap, outcome);
    setOnHold(settled.held);
    setDeferred(settled.deferred);
    if (outcome.ok) void refresh();
    return outcome.ok;
  }, [sessionId, onHold, deferred, refresh]);

  const toggleHold = useCallback((): Promise<boolean> => {
    if (busy) return Promise.resolve(false);
    const preTap: HoldUiState = { held: onHold === true, deferred };
    // Optimistic: flip the UI the instant the tap lands, so a snooze is NEVER silent. A working session
    // shows the deferred affordance immediately; a settled one shows held.
    return applyHold(!isSnoozing(preTap), optimisticHoldToggle(preTap, working));
  }, [busy, onHold, deferred, working, applyHold]);

  // Snooze for a length the user PICKED, rather than the Gateway's default. Always a hold: choosing a
  // length while already snoozed re-arms the clock to that length (the reason the picker is offered
  // while snoozed at all), so this must never take the toggle's un-snooze branch.
  const holdFor = useCallback((minutes: number): Promise<boolean> => {
    if (busy) return Promise.resolve(false);
    return applyHold(true, optimisticHoldFor(working), minutes);
  }, [busy, working, applyHold]);

  // The stop. It hands the answer back and goes nowhere - see the note on the interface for why.
  //
  // An empty reason never reaches the Gateway: it would be refused, and the sheet already refuses to
  // submit one. This guard is the same rule stated where the call is made, so a future caller cannot
  // send a stop the Gateway can only turn down.
  //
  // THE BUSY GUARD IS ON THE ACTION, NOT ONLY ON THE BUTTON (inspection finding I7). The sheet disables
  // its Stop button while a request is outstanding, but the reason box stays active and Enter is not a
  // button - a disabled attribute does not block a key press. Two Enter presses used to send two stops,
  // and the second answer could land on top of the first or clear busy while a request was still
  // outstanding. The guard belongs HERE, at the one action every caller goes through, rather than on
  // each surface that can trigger it.
  //
  // A refused second call throws and surfaces NOTHING: it says nothing on `error`, because there is no
  // event to describe - the Gateway was never asked - and every word an operator reads about a stop is
  // the Gateway's (Ruling 5). The caller's own catch is what swallows it.
  const doStopSession = useCallback(async (reason: string): Promise<SessionStopOutcome> => {
    if (!sessionId) throw new Error("There is no session on this screen to stop.");
    if (reason.trim().length === 0) throw new Error("A stop needs a reason.");
    if (stopInFlightRef.current) throw new Error("A stop for this session is already on its way.");
    stopInFlightRef.current = true;
    setBusy(true);
    setError(null);
    try {
      return await stopSession(sessionId, reason.trim());
    } catch (err) {
      // The Gateway's own sentence - the refusal for a missing reason, or an ordinary failure - carried
      // to the banner rather than replaced with a word of ours.
      setError(err instanceof Error ? err.message : "Stop failed");
      throw err;
    } finally {
      stopInFlightRef.current = false;
      setBusy(false);
    }
  }, [sessionId]);

  return {
    onHold,
    held: onHold === true,
    deferred,
    snoozed,
    holdCountdown,
    deliveryNotice,
    busy,
    error,
    setError,
    toggleHold,
    holdFor,
    stopSession: doStopSession,
  };
}
