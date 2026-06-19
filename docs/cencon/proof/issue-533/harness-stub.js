// Proof harness stub for issue #533 (NOT shipped - lives only under docs/cencon/proof/).
// Injected BEFORE the real /m/m.js so the unmodified page runs against a simulated gateway.
// It stubs ONLY the network (window.fetch) and gives a real, playable audio file so that the
// in-session Play button's genuine "speaking" state can be observed. All of m.js's real logic
// (openSession / openVoice / heroReady / preparePlaybackUrl / play) runs unchanged.
(function () {
  "use strict";
  // A tiny real WAV (silent, ~0.3s) so the <audio> element actually buffers and plays. This lets
  // the real waitPlayable() resolve via a real 'canplaythrough'/'loadeddata' event and the real
  // play() set the genuine "speaking" class - no faking of audio state.
  var WAV = makeSilentWavDataUri(0.3);

  var SESSIONS = {
    sessions: [
      { sessionId: "sess-ready-1", name: "Voice Ready Session", repoPath: "C:/repos/alpha",
        assessedState: "WaitingForInput", activityState: "WaitingForInput", statusColor: "red",
        machineName: "test-machine", lastActivityAt: "2026-06-19T10:00:00Z" }
    ]
  };

  var realFetch = window.fetch.bind(window);
  window.fetch = function (input, opts) {
    var url = (typeof input === "string") ? input : (input && input.url) || "";
    function json(obj) { return Promise.resolve(new Response(JSON.stringify(obj), { status: 200, headers: { "Content-Type": "application/json" } })); }

    if (url.indexOf("/sessions?envelope=true") >= 0) return json(SESSIONS);
    if (url.indexOf("/wingman/voice/ready") >= 0) return json({ sids: ["sess-ready-1"] });
    if (/\/wingman\/menu/.test(url)) return json({ isMenu: false });
    if (/\/wingman\/voice$/.test(url)) return json({ ready: true, spoken: "Here is your ready summary.", reply: "Full reply text." });
    // The audio element fetches the voice audio URL directly via its src (not window.fetch), so the
    // server serves it. Any other call falls through to the real fetch (none expected).
    return realFetch(input, opts);
  };

  // Point the audio URL builder at our real playable WAV by overriding the element src resolution:
  // m.js sets audioEl.src = voiceAudioUrl(sid). We intercept by serving that path from the server,
  // but to keep this stub self-contained we also rewrite the <audio> src setter to the data URI.
  document.addEventListener("DOMContentLoaded", function () {
    var audioEl = document.getElementById("tts-audio");
    if (!audioEl) return;
    var proto = Object.getPrototypeOf(audioEl);
    var desc = Object.getOwnPropertyDescriptor(proto, "src") || Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "src");
    Object.defineProperty(audioEl, "src", {
      get: function () { return desc.get.call(this); },
      set: function (v) {
        // Any voice audio URL the real code sets -> serve the real playable WAV instead.
        if (v && v.indexOf("/wingman/voice/audio") >= 0) { desc.set.call(this, WAV); }
        else { desc.set.call(this, v); }
      },
      configurable: true
    });
  });

  function makeSilentWavDataUri(seconds) {
    var sampleRate = 8000, n = Math.floor(sampleRate * seconds);
    var bytes = 44 + n * 2, buf = new ArrayBuffer(bytes), dv = new DataView(buf);
    function s(o, str) { for (var i = 0; i < str.length; i++) dv.setUint8(o + i, str.charCodeAt(i)); }
    s(0, "RIFF"); dv.setUint32(4, bytes - 8, true); s(8, "WAVE"); s(12, "fmt ");
    dv.setUint32(16, 16, true); dv.setUint16(20, 1, true); dv.setUint16(22, 1, true);
    dv.setUint32(24, sampleRate, true); dv.setUint32(28, sampleRate * 2, true);
    dv.setUint16(32, 2, true); dv.setUint16(34, 16, true); s(36, "data"); dv.setUint32(40, n * 2, true);
    var bin = "";
    var u8 = new Uint8Array(buf);
    for (var i = 0; i < u8.length; i++) bin += String.fromCharCode(u8[i]);
    return "data:audio/wav;base64," + btoa(bin);
  }

  // Test driver hooks the page can call after load (used by the harness to read state for screenshots).
  window.__proof = {
    inSessionView: function () { return !document.getElementById("session-view").classList.contains("hidden") && document.getElementById("list-view").classList.contains("hidden"); },
    playState: function () {
      var b = document.getElementById("play-btn");
      return { speaking: b.classList.contains("speaking"), ready: b.classList.contains("ready"), loading: b.classList.contains("loading"), disabled: b.disabled };
    },
    heroStatus: function () { return document.getElementById("hero-status").textContent; },
    audioPaused: function () { return document.getElementById("tts-audio").paused; }
  };
})();
