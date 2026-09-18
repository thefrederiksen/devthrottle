// Turning voice mode ON for one session - THE one implementation of that verb.
//
// It is two calls that must always happen together, in this order, and the pair is why this file exists rather than
// the calls being made wherever a button lives: `setVoiceMode` flips the owning Director's authoritative flag, so
// SessionDto.VoiceMode reads true for every client and survives navigation, and `markVoiceAndExplain` puts the
// session into the Gateway's turn-end re-narration set and reads the FIRST turn, which is what actually produces
// something to listen to. A screen that made only the first call would leave a voice session with no narration; a
// screen that made only the second would narrate one turn and then fall silent.
//
// The Voice screen's hook and the Wingman tab's Now view both switch voice on, and before this they would have been
// two copies of that pair, free to drift apart. The screens keep their own reporting - a screen decides what to show
// while it waits and what to say afterwards - and share the calls.
import { markVoiceAndExplain, setVoiceMode, type WingmanExplain } from "../api/client";

/**
 * Turn voice mode on for one session and read its current turn.
 *
 * Resolves with what the Gateway had to say about that first turn - `nothingYet` is true, with `spoken` explaining
 * it, when the session has nothing to read out yet (a fresh session, or one that has not finished a turn). Throws
 * the underlying GatewayError when either call was refused, so the caller shows the Gateway's own sentence.
 */
export async function switchVoiceModeOn(sessionId: string, signal?: AbortSignal): Promise<WingmanExplain> {
  await setVoiceMode(sessionId, true, signal);
  return await markVoiceAndExplain(sessionId, signal);
}
