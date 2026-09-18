// What the Wingman tab's Now view can DO, wired to the calls that already exist.
//
// THIS IS WHERE A FAILURE BECOMES A SENTENCE. Now is the screen the owner is meant to live on, so an answer that did
// nothing and a reply that vanished are not acceptable there. Every write below runs the call, reads what came back,
// and hands the view one outcome: whether it was ACCEPTED, and the GATEWAY'S OWN SENTENCE about it, unedited. The
// answer route's refusal sentence in particular is written for the owner to read - the settled design says it is
// what he sees, unedited - and it used to be discarded here.
//
// NONE OF THESE REJECT. A rejected promise is exactly the swallowed failure this module exists to end: it would
// reach no screen, and on a reply it would take the owner's typed words with it. Every one of them catches and
// answers, which is why the view can safely keep his draft until `accepted` comes back true.
//
// The shell owns the tabs and the router, so the three plain navigations are passed in rather than decided here.
import { holdSession, sendPrompt } from "@devthrottle/client-core/api/client";
import { answerTurnVerdict } from "@devthrottle/client-core/sessions/verdictAnswer";
import { holdPillLabel, holdStateFromResponse } from "@devthrottle/client-core/sessions/snoozeAction";
import { switchVoiceModeOn } from "@devthrottle/client-core/voice/switchVoiceMode";
import { playSessionNarration } from "@devthrottle/client-core/voice/playNarration";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import type { WingmanNowActions, WingmanNowOutcome } from "@devthrottle/client-core/sessions/WingmanNow";

/** The surface label on every client-error report from Now, so the Gateway log names where the owner was standing. */
const SURFACE = "cockpit-wingman-now";

/** The Gateway's own sentence for a failed call, reported as a client error on the way past. */
function refusal(action: string, err: unknown): WingmanNowOutcome {
  return { accepted: false, message: describeAndReport(SURFACE, action, err) };
}

/** Where the shell can take the owner from Now. The tab set and the router are the shell's, not the view's. */
export interface WingmanNowNavigation {
  openTerminal: () => void;
  goToSession: (sessionId: string) => void;
  openSettings: () => void;
}

/** Everything Now can do for one session. An action left out here is not drawn, so Now never offers a dead control. */
export function wingmanNowActions(sessionId: string, go: WingmanNowNavigation): WingmanNowActions {
  return {
    onSendReply: async (text) => {
      try {
        await sendPrompt(sessionId, text, true);
        return { accepted: true, message: "Sent to the session." };
      } catch (err) {
        return refusal("send that to the session", err);
      }
    },

    // The verdict identifier rides with the option index. A null one cannot happen while the Gateway says the stop
    // can be answered by option - but it is sent as it is rather than quietly dropped, so if it ever does, the route
    // refuses it in its own words instead of the buttons silently doing nothing.
    onAnswerOption: async (option, verdictId) => {
      try {
        const result = await answerTurnVerdict(sessionId, verdictId ?? "", [option.index]);
        return { accepted: result.accepted, message: result.reason };
      } catch (err) {
        return refusal("answer that stop", err);
      }
    },

    onSnooze: async () => {
      try {
        const result = await holdSession(sessionId, true);
        // The settled snooze words, shared with the phone's pill: a snooze asked for while the agent is working is
        // ACCEPTED and arms when the work ends, and saying nothing about that is what made snooze read as broken.
        const said = holdPillLabel(holdStateFromResponse(result), result.onHold, null);
        if (said === null) return { accepted: false, message: "The session was not snoozed." };
        return { accepted: true, message: said };
      } catch (err) {
        return refusal("snooze the session", err);
      }
    },

    onTurnOnVoice: async () => {
      try {
        const explained = await switchVoiceModeOn(sessionId);
        // A session with nothing to read out yet says so in the Gateway's words. Otherwise the view shows the
        // Gateway's own after-turn-on sentence, so there is nothing to add here.
        return { accepted: true, message: explained.nothingYet ? explained.spoken : "" };
      } catch (err) {
        return refusal("turn voice on for this session", err);
      }
    },

    onPlayVoice: async () => {
      try {
        const played = await playSessionNarration(sessionId);
        // Audio starting is its own feedback; audio NOT starting is the case that needs saying out loud.
        return played
          ? { accepted: true, message: "" }
          : { accepted: false, message: "The narration could not be played on this computer." };
      } catch (err) {
        return refusal("play the narration", err);
      }
    },

    onOpenTerminal: go.openTerminal,
    onGoToSession: go.goToSession,
    onOpenSettings: go.openSettings,
  };
}
