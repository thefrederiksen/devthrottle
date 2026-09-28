import { useVoiceModeAll } from "./useVoiceModeAll";

// THE QUIET LINE (voice mode auto-off, owner ruling 2026-09-28). When the owner has answered five sessions without
// listening, voice mode switches itself off, and he finds out from this one line beside the voice switch - no
// notification. The Gateway writes the whole sentence, time and reason included, and this renders it verbatim: the
// client is dumb (CLAUDE.md rule 7), so nothing here formats, words or decides it.
//
// ONE component for both surfaces, reading the one shared voice-mode state (the same single poll the switch and the
// banner read), so the phone and the Cockpit cannot say it differently. Each surface passes its own note class, which
// is layout, not content. Renders nothing when the Gateway has nothing to say.
export function VoiceAutoOffNote({ className }: { className: string }) {
  const { note } = useVoiceModeAll();
  if (note === null) return null;
  return (
    <div className={className} role="status">
      {note}
    </div>
  );
}
