// Small browser-side helpers for the Blazor tool pages (issue #183: exes / transcripts /
// dictionary, converted from static HTML). These cover the handful of things a Blazor Server
// component cannot do server-side: native confirm/alert dialogs, clipboard writes, and the
// segment-by-segment audio playback the Voice Recorder page needs. Exposed as one global
// object so the components can call window.ccTools.* via IJSRuntime.

window.ccTools = (function () {
    // Currently-playing Audio element (one at a time, mirroring the old transcripts.html).
    let currentAudio = null;

    return {
        // Native blocking confirm dialog. Returns true if the user accepted.
        confirm: function (message) {
            return window.confirm(message);
        },

        // Native blocking alert dialog.
        alert: function (message) {
            window.alert(message);
        },

        // Copy text to the system clipboard. Returns true on success, false if the clipboard
        // API is unavailable/blocked (non-secure context) so the caller can fall back to
        // showing the text for manual copy - same behavior as the old static pages.
        copyText: async function (text) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch (e) {
                return false;
            }
        },

        // Play a recording's audio segments in order: segment 0, then 1, ... up to count-1,
        // fetched same-origin from the Gateway proxy (GET /ingest/recording/{id}/audio/{i}).
        // dotNetRef.OnAudioState(state) is invoked with: "playing", "ended", "error", "blocked".
        playRecording: function (recordingId, segmentCount, dotNetRef) {
            this.stopAudio();
            let i = 0;
            const a = new Audio();
            currentAudio = a;
            const notify = (state) => { try { dotNetRef.invokeMethodAsync("OnAudioState", state); } catch (e) { } };
            a.onended = () => {
                i++;
                if (i < segmentCount && currentAudio === a) {
                    a.src = `/ingest/recording/${recordingId}/audio/${i}`;
                    a.play();
                } else if (currentAudio === a) {
                    currentAudio = null;
                    notify("ended");
                }
            };
            a.onerror = () => { notify("error"); };
            a.src = `/ingest/recording/${recordingId}/audio/0`;
            a.play().then(() => notify("playing")).catch(() => notify("blocked"));
        },

        // Stop any in-progress playback.
        stopAudio: function () {
            if (currentAudio) {
                currentAudio.pause();
                currentAudio.onended = null;
                currentAudio.onerror = null;
                currentAudio = null;
            }
        }
    };
})();
