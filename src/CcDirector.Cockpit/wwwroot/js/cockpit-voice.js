// Wingman Voice tab (issue #531): tap-to-listen only. The wingman's spoken summary appears
// as text the moment the turn finishes and is NEVER read automatically - the person taps
// Listen to hear it, so they never have to sit through audio they did not ask for. This uses
// the browser's built-in speech synthesis (no server round trip, no audio plumbing); the
// transcription of the person's voice is handled separately by the existing dictation pipeline.
window.cockpitVoice = {
    speak: function (text) {
        try {
            if (!('speechSynthesis' in window) || !text) return false;
            window.speechSynthesis.cancel();
            var u = new SpeechSynthesisUtterance(text);
            u.rate = 1.0;
            window.speechSynthesis.speak(u);
            return true;
        } catch (e) {
            return false;
        }
    },
    stop: function () {
        try { if ('speechSynthesis' in window) window.speechSynthesis.cancel(); } catch (e) { }
    }
};
