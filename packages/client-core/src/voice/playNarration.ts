// Playing the narration a session already has - THE one implementation of that verb outside the Voice screen.
//
// The Voice screen owns an <audio> element and a whole player, because listening is what that screen is for. Every
// other surface - the roster's play triangle, the Wingman tab's Now view - wants one thing: play what this session
// has to say, now. That is three steps in a fixed order and they are here rather than at each button, so no surface
// can get the order wrong or skip the download:
//
//   1. Ask the Gateway what the current narration is (GET /sessions/{sid}/wingman/voice).
//   2. Make sure THIS computer holds its audio - a cache hit costs nothing, a miss downloads it once.
//   3. Play it, through the shared single-clip player, so playback never overlaps and is always stoppable.
//
// WHETHER THE AUDIO IS HERE IS A LOCAL FACT, NOT A VERDICT. The Gateway says a narration is ready; whether this
// browser holds its bytes is something only this browser knows, and it is the one thing this file answers for
// itself. It never rules on what the session is doing or what the owner should see.
import { getWingmanVoice } from "../api/client";
import { ensureClip, playClip } from "./clips";

/**
 * Play this session's current narration on this computer.
 *
 * Resolves true when audio actually started, and false when there is nothing to play here - the Gateway has no
 * current narration, or its audio could not be brought down to this computer. A false is never dressed up as a
 * play, so the caller can say so instead of leaving a silent button.
 *
 * Throws the underlying GatewayError when the READ was refused, so a refusal reaches the caller as the Gateway's
 * own sentence rather than as a quiet false.
 */
export async function playSessionNarration(sessionId: string, signal?: AbortSignal): Promise<boolean> {
  const voice = await getWingmanVoice(sessionId, signal);
  if (!voice.ready || !voice.generatedAt) return false;
  await ensureClip(sessionId, voice.generatedAt);
  return playClip(sessionId);
}
