import { useVoiceMode, formatClock } from "@devthrottle/client-core/voice/useVoiceMode";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";

// The Cockpit Voice tab (issue #1213): a thin view over the SAME shared client-core hook the mobile
// Voice page uses (useVoiceMode), so the two apps render the hands-free Wingman narration from one
// source. The states are the mobile states unchanged - off, working, speaking, "voice unavailable" -
// using the ported voice-* classes; only the layout is adapted for a wide screen (a centered column).
// The Respond flow is the shared DictationDialog with no Insert, Send goes straight into the session.

export function VoiceTab({ sessionId }: { sessionId: string | undefined }) {
  const v = useVoiceMode(sessionId);

  return (
    <div className="voice-tab">
      {/* The clip element is always mounted (hidden) so auto-play works in any state; the visible play
          controls live in the speaking state below. */}
      <audio
        ref={v.setAudioEl}
        src={v.clipUrl ?? undefined}
        preload="auto"
        onLoadedMetadata={v.onLoadedMeta}
        onTimeUpdate={v.onTimeUpdate}
        onPlay={() => v.setPlaying(true)}
        onPause={() => v.setPlaying(false)}
        onEnded={v.onEndedAudio}
        style={{ display: "none" }}
      />

      {/* The live status of a background dictation reply for THIS session - the same shared strip the
          mobile Voice page mounts. Renders nothing when no dictation is in play. */}
      <DictationStatusStrip sessionId={sessionId} />

      <div className="voice-body">
        {v.error !== null && (
          <div className="composer-error" role="alert">{v.error}</div>
        )}

        {/* A menu owns this session's screen, so the last spoken reply was NOT typed (issue #2193).
            Nothing was sent and no Enter was pressed - the reply would have landed in a chooser, where
            the trailing Enter would have confirmed whichever option happened to be highlighted. The
            person answers it in the session itself; voice cannot pick an option yet. */}
        {v.menuBlocked !== null && (
          <div className="voice-menu-blocked" role="alert">
            <span className="voice-state voice-state-yellow">Waiting on a menu</span>
            <p className="voice-narr-body">{v.menuBlocked}</p>
            <button type="button" className="voice-off-toggle" onClick={v.clearMenuBlocked}>
              Dismiss
            </button>
          </div>
        )}

        {/* TTS fallback: the Gateway folded a generic "switched to a backup voice" notice onto this turn's
            ready clip (the primary provider was temporarily overloaded). Rendered VERBATIM - never names a
            provider. Shows above whichever voice card is up while the backup clip is the current one. */}
        {v.voiceDisplay?.voiceFallbackNotice != null && v.voiceDisplay.voiceFallbackNotice !== "" && (
          <div className="voice-fallback-note" role="status">{v.voiceDisplay.voiceFallbackNotice}</div>
        )}

        {/* A0. HELD - a live session owns this one, so it is read by that owner and never narrated to you.
            The Gateway's verdict, rendered verbatim, offering NOTHING - see VoiceDisplayFold.HeldDisplay for
            why the "Switch to voice mode" button below must not appear here: the enrolment sweep refuses a
            held session, so pressing it either fails or is undone. */}
        {v.voiceDisplay?.kind === "held" && (
          <div className="voice-off">
            <p className="voice-off-title">{v.voiceDisplay.label}</p>
            <p className="voice-hint">{v.voiceDisplay.message}</p>
          </div>
        )}

        {/* A. OFF - one clear "Switch to voice mode" button; only ever shown once a poll has confirmed
            the session is NOT in voice mode. */}
        {v.voiceDisplay?.kind !== "held" && !v.voiceOn && v.pollDone && (
          <div className="voice-off">
            <p className="voice-off-title">Voice mode is off for this session.</p>
            <p className="voice-hint">Turn it on and the Wingman will start narrating every turn.</p>
            <button type="button" className="voice-switch" onClick={() => void v.onSwitchOn()} disabled={v.enabling}>
              {v.enabling ? "Switching..." : "Switch to voice mode"}
            </button>
          </div>
        )}

        {/* THE GATEWAY VERDICT, rendered verbatim (the client is dumb). This view used to derive its own
            "audio unavailable" answer and branch on retrying / service-down / reason to choose the badge,
            the message, and whether a Generate button appeared - the same guessing the mobile screen did,
            and the same dead-end Generate button it produced. All of that ruling is now folded once on the
            Gateway (VoiceDisplayFold) and arrives as v.voiceDisplay; this renders its label, tone, message,
            and a Generate button ONLY when the Gateway says one can help. */}
        {(() => {
          const vd = v.voiceDisplay;
          // Held wins over every card below: the session is not the user's to be read aloud, so none of
          // "preparing", "downloading" or a Generate button describes anything he is waiting for.
          const held = vd?.kind === "held";
          const busy = !held && v.voiceOn && !v.speaking && (vd?.kind === "preparing" || vd?.kind === "working");
          const downloading = !held && v.voiceOn && !v.speaking && vd?.kind === "ready";
          const status =
            !held && v.voiceOn && !v.speaking && vd != null &&
            vd.kind !== "ready" && vd.kind !== "preparing" && vd.kind !== "working" && vd.kind !== "off";
          if (busy && vd) {
            return (
              <>
                <div className="voice-statusbar">
                  <span className={"voice-state voice-state-" + vd.tone}>{vd.label}</span>
                </div>
                <div className="voice-narr"><div className="voice-narr-body">{vd.message}</div></div>
                <div className="voice-working">
                  <span className="voice-spinner" aria-hidden="true" />
                  <span className="voice-ref">{vd.kind === "working" ? "working" : "rendering audio"}</span>
                </div>
              </>
            );
          }
          if (downloading) {
            return (
              <>
                <div className="voice-statusbar">
                  <span className="voice-state voice-state-yellow">Voice on its way</span>
                </div>
                <div className="voice-narr">
                  <div className="voice-narr-body">Downloading the spoken audio. It will play automatically.</div>
                </div>
                <div className="voice-working">
                  <span className="voice-spinner" aria-hidden="true" />
                  <span className="voice-ref">downloading</span>
                </div>
              </>
            );
          }
          if (status && vd) {
            return (
              <>
                <div className="voice-statusbar">
                  <span className={"voice-state voice-state-" + vd.tone}>{vd.label}</span>
                </div>
                <div className="voice-narr"><div className="voice-narr-body">{vd.message}</div></div>
                {vd.canGenerate && (
                  <button
                    type="button"
                    className="voice-switch"
                    onClick={() => void v.onGenerateNow()}
                    disabled={v.regenerating}
                  >
                    {v.regenerating ? "Generating narration..." : "Generate narration now"}
                  </button>
                )}
              </>
            );
          }
          return null;
        })()}

        {/* C. SPEAKING - the audio bar and Respond are pinned at the top; the response text scrolls
            below. */}
        {v.voiceDisplay?.kind !== "held" && v.speaking && (
          <>
            <div className="voice-top">
              <div className="voice-statusbar">
                <span className="voice-state voice-state-green">Speaking</span>
                {v.resumed && <span className="voice-ref">picked up where you left off</span>}
              </div>
              <div className="voice-player">
                <button
                  type="button"
                  className="voice-tri-btn"
                  onClick={v.onTogglePlay}
                  aria-label={v.playing ? "Pause" : "Play"}
                >
                  <span className={v.playing ? "voice-pause" : "voice-tri"} aria-hidden="true" />
                </button>
                <button
                  type="button"
                  className="voice-restart-btn"
                  onClick={v.onRestart}
                  aria-label="Restart from the beginning"
                >
                  <span className="voice-restart" aria-hidden="true" />
                </button>
                <input
                  type="range"
                  className="voice-seek"
                  min={0}
                  max={v.dur > 0 ? v.dur : 0}
                  step="any"
                  value={v.dur > 0 ? Math.min(v.pos, v.dur) : 0}
                  onChange={v.onSeek}
                  aria-label="Seek through the narration"
                />
                <span className="voice-ref voice-clock">{formatClock(v.pos)} / {formatClock(v.dur)}</span>
              </div>
              <button type="button" className="voice-respond" onClick={() => v.setResponding(true)}>
                Respond
              </button>
            </div>
            <div className="voice-narr voice-narr-scroll">
              <div className="voice-narr-title">{v.narrative}</div>
              <div className="voice-narr-body">
                {v.playing ? "Speaking. Click pause to stop, or Respond to answer." : "Click play to listen, or Respond to answer."}
              </div>
            </div>
          </>
        )}

        {/* Turn voice off: a low-emphasis control present in every on-state. */}
        {v.voiceOn && (
          <button type="button" className="voice-off-toggle" onClick={() => void v.onSwitchOff()}>
            Turn voice off
          </button>
        )}
      </div>

      {/* Reply: the shared dictation interface with NO Insert - Send goes straight into the session.
          Send while still recording is the hook's fire-and-forget path (onRespondSendAudio ->
          backgroundTranscribeAndSend), the SAME wiring the mobile Voice page has: the dialog closes the
          instant Send is pressed and the transcription + submit run in the background, with the shared
          DictationStatusStrip above showing the live phase and any held/dropped outcome. This tab was
          deliberately NOT wired to that path while the /dictation/* pipeline resolved sessions in the
          Local partition on hosted (blocker #1884, see the SessionComposer history note); the pipeline is
          tenant-partitioned end to end now (PR #1945) and the strip is mounted here, so the reasons are
          gone. A capture-loss warning rides the strip as the delivered-with-warning state, so a reply
          that lost audio is still never silent. The PAUSED-stage Send submits the already-transcribed
          text synchronously, unchanged. */}
      {v.responding && (
        <DictationDialog
          surface="cockpit"
          showInsert={false}
          onSend={(text, spokenDeliveryId) => void v.onRespondSend(text, spokenDeliveryId)}
          onSendAudio={v.onRespondSendAudio}
          onClose={() => v.setResponding(false)}
        />
      )}
    </div>
  );
}
