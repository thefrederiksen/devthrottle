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
    // Speak text with the natural OpenAI "nova" voice (the same voice the phone uses), NOT the
    // robotic browser voice. The audio is synthesized on the Gateway (which holds the OpenAI key)
    // and fetched here DIRECTLY over plain HTTP from the Cockpit's /api/tts proxy - deliberately not
    // marshalled through the Blazor circuit, which chokes on a multi-megabyte payload. Returns a
    // status string the caller shows: 'ok' (playing), 'nokey' (no OpenAI key on the Gateway), or
    // 'fail'. Any prior speech/audio is stopped first.
    speakViaServer: async function (text) {
        try {
            this.stop();
            var resp = await fetch('/api/tts', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ text: text })
            });
            if (resp.status === 503) return 'nokey';
            if (!resp.ok) return 'fail';
            var blob = await resp.blob();
            var url = URL.createObjectURL(blob);
            var audio = new Audio(url);
            window.__ccVoiceAudio = audio;
            audio.onended = function () { try { URL.revokeObjectURL(url); } catch (e) { } };
            await audio.play();
            return 'ok';
        } catch (e) {
            return 'fail';
        }
    },
    stop: function () {
        try { if ('speechSynthesis' in window) window.speechSynthesis.cancel(); } catch (e) { }
        try {
            if (window.__ccVoiceAudio) {
                window.__ccVoiceAudio.pause();
                window.__ccVoiceAudio = null;
            }
        } catch (e) { }
    }
};
