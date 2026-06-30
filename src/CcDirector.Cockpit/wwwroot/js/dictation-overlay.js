// Shared Dictate overlay - the ONE web dictation dialog used by BOTH the Blazor
// Cockpit and the Director's plain HTML session view. It is the browser twin of the
// desktop SpeakDialog (src/CcDirector.Avalonia/Voice/SpeakDialog.axaml) and the phone
// SpeakIntoTextboxDialog, and obeys the same canonical contract,
// docs/architecture/dictation/DICTATION_UX_SPEC.md.
//
// One source of truth: this file physically lives in the Cockpit wwwroot and is ALSO
// embedded into the Director's Control API (a linked EmbeddedResource served at
// /dictation-overlay.js), so the two surfaces can never drift apart again. The older
// cockpit-dictate.js and dictate-client.js were merged into this file.
//
// Whole-clip BATCH: audio is captured as 24 kHz mono PCM16 via the vendored
// pcm16-writer AudioWorklet and sent over a WebSocket to the owning Director's /dictate
// endpoint. NO text appears while talking - there is no realtime/streaming and no live
// partials by design (transcribing the whole clip at once is more accurate). Pause stops,
// transcribes what was said so far, shows it (editable), and Resume appends a fresh
// segment. The server is single-shot per connection, so a Pause finalizes the current
// segment and a Resume opens a brand new one; cleaned text is accumulated client-side,
// mirroring the desktop dialog.
//
// Capture-first: frames recorded before the server says 'started' are buffered in order
// and flushed the instant it is, so the opening words are never clipped by connect latency.
//
// Public API (one entry, two host adapters):
//
//   window.dictationOverlay.start({
//     wsUrl,                 // optional; defaults to same-origin ws(s)://<host>/dictate
//     workletUrl,            // optional; defaults to /dictate-worklet.js
//     profile,               // optional; defaults to 'default'
//     dotNetRef,             // optional Blazor DotNetObjectReference<...>; when present the
//                            //   overlay calls OnDictateInsert(text)/OnDictateSend(text)/
//                            //   OnDictateCancel() on it
//     onInsert, onSend, onCancel,  // plain-JS callbacks (used when dotNetRef is absent);
//                            //   onSend defaults to onInsert when omitted
//   });
//
// Backward-compatible shim: window.cockpitDictate.start(ref, wsUrl, workletUrl, profile)
// is preserved so existing Blazor call sites need no change.
window.dictationOverlay = (function () {
  // Capture-first frame router (pure/synchronous): buffer frames until the server is ready,
  // then flush in capture order and stream live thereafter.
  //
  // Issue #226: every captured frame is ALSO appended to a retained log ('all') so a
  // mid-stream drop can be retried by replaying the whole segment's audio onto a fresh
  // socket (capture-first), instead of discarding the words spoken before the drop. The
  // retained log is independent of 'pending' (which empties on markReady), so it survives
  // the live-sending phase. 'capturedBytes' lets the dialog apply the same recoverable-
  // audio floor the server uses (MinRecoverableAudioBytes = 24000) to decide whether a
  // drop is worth a Retry or is a sub-floor clip that should just cancel.
  function createCaptureBuffer() {
    let ready = false;
    const pending = [];
    const all = [];
    let capturedBytes = 0;
    return {
      get ready() { return ready; },
      get capturedBytes() { return capturedBytes; },
      get frames() { return all; },
      push: function (frame, send) {
        all.push(frame);
        capturedBytes += (frame && frame.byteLength) || 0;
        if (ready) send(frame); else pending.push(frame);
      },
      markReady: function (send) { for (const f of pending) send(f); pending.length = 0; ready = true; },
    };
  }

  function injectStyles() {
    if (document.getElementById('dictation-overlay-styles')) return;
    const css = `
      .cd-overlay { position: fixed; inset: 0; z-index: 99999; background: rgba(0,0,0,0.65);
        display: flex; align-items: center; justify-content: center;
        font-family: "Segoe UI", system-ui, sans-serif; }
      .cd-card { width: min(640px, 94vw); background: #1E1E1E; border: 1px solid #3C3C3C;
        border-radius: 10px; color: #DDD; padding: 16px; box-shadow: 0 12px 40px rgba(0,0,0,0.45); }
      .cd-mic-row { display: flex; align-items: center; gap: 8px; margin-bottom: 12px; }
      .cd-mic-label { color: #888; font-family: "Cascadia Mono", Consolas, monospace; font-size: 12px; }
      .cd-mic { flex: 1; background: #252526; color: #DDD; border: 1px solid #3C3C3C;
        border-radius: 6px; padding: 5px 8px; font-size: 12px; }
      .cd-head { display: flex; align-items: center; gap: 14px; margin-bottom: 12px; }
      .cd-status { font-family: "Cascadia Mono", Consolas, monospace; font-size: 20px;
        font-weight: 700; letter-spacing: 0.06em; color: #F44747; }
      .cd-status.amber { color: #DCDCAA; }
      .cd-timer { margin-left: auto; font-family: "Cascadia Mono", Consolas, monospace;
        font-size: 22px; font-weight: 600; color: #F44747; }
      .cd-timer.amber { color: #DCDCAA; }
      .cd-eq { display: flex; align-items: center; justify-content: center; gap: 6px;
        height: 100px; margin: 4px 0; }
      .cd-well { width: 12px; height: 92px; background: #33333A; border-radius: 3px;
        display: flex; align-items: flex-end; }
      .cd-bar { width: 12px; height: 8px; background: #F44747; border-radius: 3px;
        transition: height 60ms linear; }
      .cd-bar.amber { background: #DCDCAA; }
      .cd-bar.paused { background: #6A6A6A; }
      .cd-hint { text-align: center; color: #DCDCAA; font-family: "Cascadia Mono", Consolas, monospace;
        font-size: 12px; min-height: 18px; margin: 2px 0 8px; }
      .cd-transcript { width: 100%; min-height: 90px; max-height: 220px; box-sizing: border-box;
        background: #252526; border: 1px solid #3C3C3C; border-radius: 6px; padding: 8px 12px;
        color: #DDD; font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px;
        resize: vertical; white-space: pre-wrap; }
      .cd-transcript[readonly] { color: #DDD; }
      .cd-foot { display: flex; align-items: center; gap: 10px; margin-top: 14px; }
      .cd-spacer { flex: 1; }
      .cd-btn { border: 0; border-radius: 6px; padding: 8px 14px; font-size: 14px; font-weight: 600;
        cursor: pointer; min-width: 84px; }
      .cd-btn:disabled { opacity: 0.55; cursor: not-allowed; }
      .cd-cancel { background: #2D2D30; color: #CCC; }
      .cd-retry { background: #007ACC; color: #fff; display: none; }
      .cd-pause { background: #2D2D30; color: #CCC; min-width: 100px; }
      .cd-insert { background: #16A34A; color: #fff; }
      .cd-send { background: #007ACC; color: #fff; min-width: 100px; }
      .cd-pause-glyph { display: inline-flex; gap: 4px; align-items: center; justify-content: center; }
      .cd-pause-glyph i { width: 4px; height: 14px; background: #CCC; display: block; border-radius: 1px; }
    `;
    const style = document.createElement('style');
    style.id = 'dictation-overlay-styles';
    style.textContent = css;
    document.head.appendChild(style);
  }

  function buildOverlay() {
    const overlay = document.createElement('div');
    overlay.className = 'cd-overlay';
    let wells = '';
    for (let i = 0; i < 9; i++) wells += '<div class="cd-well"><div class="cd-bar"></div></div>';
    overlay.innerHTML = `
      <div class="cd-card" role="dialog" aria-modal="true" aria-label="Dictate">
        <div class="cd-mic-row">
          <span class="cd-mic-label">Mic</span>
          <select class="cd-mic"></select>
        </div>
        <div class="cd-head">
          <div class="cd-status">STARTING</div>
          <div class="cd-timer">0:00.0</div>
        </div>
        <div class="cd-eq">${wells}</div>
        <div class="cd-hint"></div>
        <textarea class="cd-transcript" readonly placeholder="Speak naturally - your words are turned into text when you pause or finish, not while you talk (it is more accurate that way). Press Pause any time to see what you have said so far."></textarea>
        <div class="cd-foot">
          <button type="button" class="cd-btn cd-cancel">Cancel</button>
          <button type="button" class="cd-btn cd-retry">Retry</button>
          <button type="button" class="cd-btn cd-pause"><span class="cd-pause-glyph"><i></i><i></i></span></button>
          <div class="cd-spacer"></div>
          <button type="button" class="cd-btn cd-insert">Insert</button>
          <button type="button" class="cd-btn cd-send">Send</button>
        </div>
      </div>`;
    return overlay;
  }

  // Resolve the three outcome callbacks from the options object. A Blazor host passes a
  // DotNetObjectReference (dotNetRef) and we invoke its OnDictate* methods; a plain-JS host
  // passes onInsert/onSend/onCancel directly. onSend falls back to onInsert when omitted.
  function resolveCallbacks(opts) {
    if (opts.dotNetRef) {
      const ref = opts.dotNetRef;
      return {
        onInsert: (text) => notifyDotNet(ref, 'OnDictateInsert', text),
        onSend: (text) => notifyDotNet(ref, 'OnDictateSend', text),
        onCancel: () => notifyDotNet(ref, 'OnDictateCancel'),
      };
    }
    const onInsert = opts.onInsert || function () {};
    return {
      onInsert,
      onSend: opts.onSend || onInsert,
      onCancel: opts.onCancel || function () {},
    };
  }

  function defaultWsUrl() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return proto + '//' + location.host + '/dictate';
  }

  async function start(opts) {
    opts = opts || {};
    const profile = opts.profile || 'default';
    const workletUrl = opts.workletUrl || '/dictate-worklet.js';
    const wsUrl = opts.wsUrl || defaultWsUrl();
    const { onInsert, onSend, onCancel } = resolveCallbacks(opts);

    if (!window.AudioWorkletNode || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      onCancel();
      alert('Dictation needs microphone + AudioWorklet support over HTTPS (or localhost).');
      return;
    }

    injectStyles();
    const overlay = buildOverlay();
    document.body.appendChild(overlay);

    const micSel       = overlay.querySelector('.cd-mic');
    const statusEl     = overlay.querySelector('.cd-status');
    const timerEl      = overlay.querySelector('.cd-timer');
    const bars         = overlay.querySelectorAll('.cd-bar');
    const hintEl       = overlay.querySelector('.cd-hint');
    const transcriptEl = overlay.querySelector('.cd-transcript');
    const cancelBtn    = overlay.querySelector('.cd-cancel');
    const retryBtn     = overlay.querySelector('.cd-retry');
    const pauseBtn     = overlay.querySelector('.cd-pause');
    const insertBtn    = overlay.querySelector('.cd-insert');
    const sendBtn      = overlay.querySelector('.cd-send');

    // ---- per-segment state (reset on each Resume / device change) ----
    let mediaStream = null, ws = null, audioCtx = null, sourceNode = null, workletNode = null;
    let levelCtx = null, analyser = null, levelData = null, rafId = null, capture = null;

    // Capture-health (issue #863): the worklet emits raw 24 kHz mono PCM16 = 48000 bytes/sec, so
    // captured bytes versus elapsed capture time is the SAME audio-loss check the desktop NAudio
    // path uses. lastFrameAtMs/maxFrameGapMs add the arrival-cadence signal that tells a local
    // stall (large gaps) apart from clean capture. Diagnostic only - it changes nothing.
    const EXPECTED_BYTES_PER_SEC = 48000;
    let lastFrameAtMs = 0, maxFrameGapMs = 0;

    // ---- persistent across segments ----
    let stage = 'starting';            // starting | recording | transcribing | paused | error
    let finalIntent = 'pause';         // what to do when 'final' arrives: pause | insert | send
    let finalizing = false;            // guards the tail-drain window so finalize can't re-enter
    let accumulatedText = '';          // cleaned text from finalized segments
    let selectedDeviceId = '';         // '' = system default
    let done = false;

    // Issue #226: failure-UX + retry-with-preserved-audio.
    // 24 kHz mono PCM16 = 48000 bytes/sec, so 24000 bytes is half a second. This MUST match
    // DictationEndpoint's recoverable-audio floor: below it a drop carried nothing worth
    // saving, so we treat it as a clean cancel (no Retry, no red error); at/above it the audio
    // is worth recovering, so we offer Retry that replays the buffered frames.
    const MIN_RECOVERABLE_AUDIO_BYTES = 24000;
    // The last typed {type:error} cause the Director sent (e.g. "no API key" / provider failure).
    // Surfaced on the close instead of a bare code/reason so the user sees the real cause.
    let lastServerError = '';
    // Frames retained from the dropped segment, snapshotted so a Retry can replay the audio
    // captured before the drop onto a fresh socket. Null when there is nothing to replay.
    let replayFrames = null;

    // Trailing-audio drain window (web parity with the desktop no-loss stop). The worklet posts
    // a PCM frame every ~5ms via postMessage; those become queued main-thread tasks that ws.send
    // each frame. Sending 'stop' synchronously on the click would (a) race ahead of
    // already-queued-but-unsent frames and (b) miss the input-latency tail that has not reached
    // the worklet yet - the server finalizes on 'stop', so both are lost. Keeping capture live
    // for DRAIN_MS and sending 'stop' from a setTimeout guarantees every queued frame has flushed.
    const DRAIN_MS = 250;
    let t0 = performance.now();
    let elapsedBeforeMs = 0;
    let timerHandle = null;
    let micEnumerated = false;

    function sendFrame(buf) { if (ws && ws.readyState === WebSocket.OPEN) { try { ws.send(buf); } catch (e) {} } }

    function joinText(a, b) {
      if (!a) return b || ''; if (!b) return a;
      return (/\s$/.test(a) || /^\s/.test(b)) ? a + b : a + ' ' + b;
    }

    function renderTranscript() {
      transcriptEl.value = accumulatedText;
    }

    // ---------- stage transitions ----------
    function setStage(s) {
      stage = s;
      statusEl.classList.toggle('amber', s !== 'recording' && s !== 'error');
      timerEl.classList.toggle('amber', s !== 'recording');
      bars.forEach(b => { b.classList.remove('amber', 'paused'); });
      if (s === 'starting') {
        statusEl.textContent = 'STARTING';
      } else if (s === 'recording') {
        statusEl.textContent = 'RECORDING'; statusEl.style.color = '';
        transcriptEl.setAttribute('readonly', '');
        pauseBtn.disabled = false; insertBtn.disabled = false; sendBtn.disabled = false;
        micSel.disabled = false;
        // Restore the action row after a Retry (issue #226): the error stage hid these and the
        // close handler may have shown Retry / relabeled Cancel. A live recording uses the normal
        // controls again.
        pauseBtn.style.display = ''; insertBtn.style.display = ''; sendBtn.style.display = '';
        retryBtn.style.display = 'none';
        cancelBtn.textContent = 'Cancel';
        setPauseGlyph();
      } else if (s === 'transcribing') {
        statusEl.textContent = 'TRANSCRIBING';
        transcriptEl.setAttribute('readonly', '');
        pauseBtn.disabled = true; insertBtn.disabled = true; sendBtn.disabled = true;
        micSel.disabled = true;
        bars.forEach(b => { b.classList.add('amber'); b.style.height = '34px'; });
        hintEl.textContent = 'Transcribing the whole recording...';
      } else if (s === 'paused') {
        statusEl.textContent = 'PAUSED - reviewing';
        pauseBtn.textContent = 'Resume'; pauseBtn.disabled = false;
        insertBtn.disabled = false; sendBtn.disabled = false; micSel.disabled = false;
        bars.forEach(b => { b.classList.add('paused'); b.style.height = '8px'; });
        hintEl.textContent = '';
        transcriptEl.removeAttribute('readonly');   // editable for review
        renderTranscript();
        transcriptEl.focus();
        transcriptEl.selectionStart = transcriptEl.selectionEnd = transcriptEl.value.length;
      } else if (s === 'error') {
        statusEl.textContent = 'ERROR'; statusEl.style.color = '#F44747';
        pauseBtn.style.display = 'none'; insertBtn.style.display = 'none'; sendBtn.style.display = 'none';
        cancelBtn.textContent = 'Close';
      }
    }

    function setPauseGlyph() { pauseBtn.innerHTML = '<span class="cd-pause-glyph"><i></i><i></i></span>'; }

    function showError(msg) { hintEl.textContent = ''; transcriptEl.value = msg; setStage('error'); }

    // ---------- mid-stream drop (issue #226) ----------
    // The dictation socket connected and then dropped while recording/transcribing. Surface the
    // REAL cause (the Director's typed {type:error} if one arrived, else the WS close code/reason)
    // and, if enough audio was captured to be worth recovering, offer Retry that replays it.
    function describeDrop(ev) {
      if (lastServerError) return 'Dictation dropped: ' + lastServerError;
      const code = ev && typeof ev.code === 'number' ? ev.code : 0;
      const reason = ev && ev.reason ? String(ev.reason) : '';
      if (code === 1005 && !reason) return 'Dictation dropped (connection closed without a status).';
      return reason
        ? 'Dictation dropped (code ' + code + ': ' + reason + ').'
        : 'Dictation dropped (code ' + code + ').';
    }

    function handleMidStreamDrop(ev) {
      const captured = capture ? capture.capturedBytes : 0;
      if (captured < MIN_RECOVERABLE_AUDIO_BYTES) {
        // Sub-floor clip: nothing worth recovering. Treat it as a clean cancel.
        teardownAll();
        onCancel();
        return;
      }
      // Snapshot the frames captured this segment so Retry can replay them onto a fresh socket
      // even after teardownSegment nulls 'capture'.
      replayFrames = capture.frames.slice();
      showError(describeDrop(ev));
      teardownSegment();
      retryBtn.style.display = 'inline-block';
    }

    // Retry: reopen the socket and replay the buffered audio (capture-first), so the words
    // spoken before the drop survive.
    async function retryWithPreservedAudio() {
      if (!replayFrames || !replayFrames.length) return;
      const frames = replayFrames;
      replayFrames = null;
      retryBtn.style.display = 'none';
      cancelBtn.textContent = 'Cancel';
      lastServerError = '';
      capture = createCaptureBuffer();
      for (const f of frames) capture.push(f, sendFrame);
      finalizing = false;
      lastFrameAtMs = 0; maxFrameGapMs = 0;
      setStage('starting');
      const ok = await bootCapture();
      if (!ok) return;
      openSocket();
    }

    // ---------- timer ----------
    function startTimer() {
      if (timerHandle) return;
      timerHandle = setInterval(() => {
        if (stage !== 'recording') return;
        const ms = (performance.now() - t0) + elapsedBeforeMs;
        const s = Math.floor(ms / 1000);
        timerEl.textContent = Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0') + '.' + Math.floor((ms % 1000) / 100);
      }, 100);
    }

    // ---------- equalizer ----------
    function setupLevelMeter(stream) {
      try {
        levelCtx = new (window.AudioContext || window.webkitAudioContext)();
        const src = levelCtx.createMediaStreamSource(stream);
        analyser = levelCtx.createAnalyser();
        analyser.fftSize = 64; analyser.smoothingTimeConstant = 0.5;
        src.connect(analyser);
        levelData = new Uint8Array(analyser.frequencyBinCount);
        renderLevelLoop();
      } catch (e) { /* meter is non-essential */ }
    }
    function renderLevelLoop() {
      rafId = requestAnimationFrame(renderLevelLoop);
      if (!analyser || stage !== 'recording') return;
      analyser.getByteFrequencyData(levelData);
      const n = bars.length, center = Math.floor(n / 2), maxH = 92, minH = 8;
      for (let i = 0; i < n; i++) {
        const t = Math.abs(i - center) / Math.max(1, center);
        const idx = Math.min(levelData.length - 1, Math.floor(2 + t * (levelData.length - 4)));
        const v = levelData[idx] / 255;
        bars[i].style.height = (minH + v * (maxH - minH)).toFixed(0) + 'px';
      }
    }

    // ---------- mic selector ----------
    async function populateMics() {
      if (micEnumerated) return;
      try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        const ins = devices.filter(d => d.kind === 'audioinput');
        micSel.innerHTML = '';
        const def = document.createElement('option');
        def.value = ''; def.textContent = 'Default microphone';
        micSel.appendChild(def);
        for (const d of ins) {
          if (!d.deviceId || d.deviceId === 'default') continue;
          const o = document.createElement('option');
          o.value = d.deviceId; o.textContent = d.label || ('Microphone ' + (micSel.length));
          micSel.appendChild(o);
        }
        micSel.value = selectedDeviceId;
        micEnumerated = true;
      } catch (e) { /* enumeration optional */ }
    }
    micSel.addEventListener('change', async () => {
      const next = micSel.value;
      if (next === selectedDeviceId) return;
      selectedDeviceId = next;
      // Switch device: keep accumulated text (re-seed from the edit box) and start a fresh
      // segment on the new device.
      accumulatedText = transcriptEl.value || '';
      teardownSegment();
      elapsedBeforeMs = 0;
      await startSegment();
    });

    // ---------- capture + socket (one per segment) ----------
    async function bootCapture() {
      const constraints = { audio: { sampleRate: 24000, channelCount: 1, echoCancellation: true, noiseSuppression: true } };
      if (selectedDeviceId) constraints.audio.deviceId = { exact: selectedDeviceId };
      try {
        mediaStream = await navigator.mediaDevices.getUserMedia(constraints);
      } catch (e) {
        showError('Microphone access denied: ' + e.message);
        return false;
      }
      setupLevelMeter(mediaStream);
      await populateMics();
      try {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 24000 });
        await audioCtx.audioWorklet.addModule(workletUrl);
        sourceNode = audioCtx.createMediaStreamSource(mediaStream);
        workletNode = new AudioWorkletNode(audioCtx, 'pcm16-writer');
        let first = false;
        workletNode.port.onmessage = (e) => {
          const nowMs = performance.now();
          if (!first) { first = true; t0 = nowMs; setStage('recording'); startTimer(); }
          else { const gap = nowMs - lastFrameAtMs; if (gap > maxFrameGapMs) maxFrameGapMs = gap; }
          lastFrameAtMs = nowMs;
          if (capture) capture.push(e.data, sendFrame);
        };
        sourceNode.connect(workletNode);
      } catch (e) {
        showError('Audio capture setup failed: ' + e.message);
        return false;
      }
      return true;
    }

    function openSocket() {
      try { ws = new WebSocket(wsUrl); } catch (e) { showError('Could not open dictation stream: ' + e.message); return; }
      ws.binaryType = 'arraybuffer';
      ws.onmessage = (ev) => {
        let m; try { m = JSON.parse(ev.data); } catch { return; }
        switch (m.type) {
          case 'ready': ws.send(JSON.stringify({ type: 'start', profile })); break;
          case 'started': if (capture) capture.markReady(sendFrame); break;
          // Whole-clip batch: the server emits NO 'partial' frames - text appears only in the
          // single 'final' after 'transcribing'. There is no live preview by design.
          case 'transcribing': setStage('transcribing'); break;
          case 'final': {
            const cleaned = (m.cleaned || m.raw || '');
            accumulatedText = joinText(accumulatedText, cleaned);
            if (finalIntent === 'pause') { teardownSegment(); setStage('paused'); }
            else if (finalIntent === 'insert') finishWith(onInsert);
            else if (finalIntent === 'send') finishWith(onSend);
            break;
          }
          case 'error': {
            // Issue #226: remember the Director's typed cause. If it arrives just before the
            // socket closes mid-recording, the onclose handler surfaces THIS specific cause
            // (e.g. "no API key on the owning machine") instead of a bare close code/reason.
            const cause = m.message || 'unknown';
            lastServerError = cause;
            showError('Server error: ' + cause);
            break;
          }
        }
      };
      // Issue #268: the dictate socket is SAME-ORIGIN to the Gateway, which reverse-proxies to
      // the owning Director. So a failure to open it almost always means the Gateway could not
      // reach the owning Director (offline / unreachable) - not a bare "WebSocket failed".
      // 'wasReady' tells a never-opened upgrade apart from a mid-stream drop.
      let wasReady = false;
      ws.onerror = () => {
        if (stage === 'transcribing') return;
        showError(wasReady
          ? 'Dictation connection failed mid-stream.'
          : 'Could not reach the owning Director through the Gateway (it may be offline or unreachable).');
      };
      ws.onclose = (ev) => {
        if (done || stage === 'paused') return;
        if (stage === 'starting') {
          showError(wasReady
            ? 'Dictation stream closed before it was ready.'
            : 'Could not reach the owning Director through the Gateway (it may be offline or unreachable).');
        } else if (stage === 'recording' || (stage === 'transcribing' && !finalizing)) {
          handleMidStreamDrop(ev);
        }
      };
      ws.addEventListener('open', () => { wasReady = true; });
    }

    async function startSegment() {
      capture = createCaptureBuffer();
      finalizing = false;
      lastFrameAtMs = 0; maxFrameGapMs = 0;
      setStage('starting');
      const ok = await bootCapture();   // mic up immediately (capture-first)...
      if (!ok) return;
      openSocket();                     // ...socket opens in parallel; early frames buffer
    }

    // ---------- capture-health (issue #863) ----------
    // Raw PCM16 at 24 kHz mono is 48000 bytes/sec, so captured bytes versus the elapsed capture
    // window is the same expected-vs-actual audio-loss check the desktop path logs. A deficit with
    // large maxFrameGapMs points at a local capture/scheduling stall; a deficit with steady frames
    // points upstream (e.g. the getUserMedia source under-delivering). Computed once at stop, when
    // the drained tail has arrived and before any teardown nulls 'capture'.
    function captureHealth() {
      if (!capture) return null;
      const recordingMs = lastFrameAtMs > t0 ? (lastFrameAtMs - t0) : 0;
      const capturedBytes = capture.capturedBytes;
      const expectedBytes = Math.round(EXPECTED_BYTES_PER_SEC * recordingMs / 1000);
      const deficit = expectedBytes > 0 ? Math.max(0, 1 - capturedBytes / expectedBytes) : 0;
      return {
        recordingMs: Math.round(recordingMs), capturedBytes: capturedBytes, expectedBytes: expectedBytes,
        deficit: deficit, frames: capture.frames.length, maxFrameGapMs: Math.round(maxFrameGapMs),
      };
    }

    function logCaptureHealth(h) {
      if (!h) return;
      const line = '[capture-health] surface=overlay capturedBytes=' + h.capturedBytes +
        ' expectedBytes=' + h.expectedBytes + ' deficit=' + (h.deficit * 100).toFixed(1) + '%' +
        ' recordingMs=' + h.recordingMs + ' frames=' + h.frames + ' maxFrameGapMs=' + h.maxFrameGapMs;
      if (h.deficit > 0.1) console.warn(line + ' (audio appears to have been dropped before transcription)');
      else console.log(line);
    }

    // ---------- finalize / send-stop ----------
    // Send the client-measured capture-health WITH the stop frame so the server persists it next to
    // the bytes it actually received (issue #863): server-received bytes versus client recording
    // wall-clock is the end-to-end audio deficit (capture loss PLUS any network loss).
    function sendStop() {
      const h = captureHealth();
      logCaptureHealth(h);
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(h ? { type: 'stop', health: h } : { type: 'stop' }));
      }
      setStage('transcribing');
    }

    // Recording -> drain the trailing audio -> stop -> wait for 'final', then dispatch by
    // finalIntent. We keep the mic graph live for DRAIN_MS so the last words actually reach the
    // server before it finalizes (see DRAIN_MS note above). Controls are frozen during the window
    // (and 'finalizing' guards re-entry) so a second click cannot stack another stop.
    function finalizeFromRecording(intent) {
      if (finalizing) return;
      finalizing = true;
      finalIntent = intent;
      pauseBtn.disabled = true; insertBtn.disabled = true; sendBtn.disabled = true; micSel.disabled = true;
      hintEl.textContent = 'finishing...';
      setTimeout(sendStop, DRAIN_MS);
    }

    function finishWith(callback) {
      const text = (transcriptEl.value || accumulatedText || '').trim();
      teardownAll();
      callback(text);
    }

    // ---------- teardown ----------
    function teardownSegment() {
      try { if (rafId) cancelAnimationFrame(rafId); } catch (e) {} rafId = null;
      try { if (ws) { ws.onmessage = ws.onerror = ws.onclose = null; } } catch (e) {}
      try { if (sourceNode) sourceNode.disconnect(); } catch (e) {}
      try { if (workletNode) workletNode.disconnect(); } catch (e) {}
      try { if (audioCtx) audioCtx.close(); } catch (e) {}
      try { if (levelCtx) levelCtx.close(); } catch (e) {}
      try { if (mediaStream) mediaStream.getTracks().forEach(t => t.stop()); } catch (e) {}
      try { if (ws && ws.readyState === WebSocket.OPEN) ws.close(); } catch (e) {}
      ws = audioCtx = sourceNode = workletNode = levelCtx = analyser = levelData = mediaStream = capture = null;
    }
    function teardownAll() {
      if (done) return; done = true;
      try { if (timerHandle) clearInterval(timerHandle); } catch (e) {}
      teardownSegment();
      document.removeEventListener('keydown', onKeyDown);
      if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
    }

    // ---------- buttons ----------
    cancelBtn.addEventListener('click', () => {
      try { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'abort' })); } catch (e) {}
      teardownAll();
      onCancel();
    });
    retryBtn.addEventListener('click', () => { retryWithPreservedAudio(); });
    pauseBtn.addEventListener('click', () => {
      if (stage === 'recording') { elapsedBeforeMs += performance.now() - t0; pauseBtn.disabled = true; finalizeFromRecording('pause'); }
      else if (stage === 'paused') { accumulatedText = transcriptEl.value || ''; pauseBtn.disabled = true; elapsedBeforeMs = 0; startSegment(); }
    });
    insertBtn.addEventListener('click', () => {
      if (stage === 'recording') finalizeFromRecording('insert');
      else if (stage === 'paused') finishWith(onInsert);
    });
    sendBtn.addEventListener('click', () => {
      if (stage === 'recording') finalizeFromRecording('send');
      else if (stage === 'paused') finishWith(onSend);
    });

    function onKeyDown(e) {
      if (e.key === 'Escape') { e.preventDefault(); cancelBtn.click(); }
      else if (e.key === 'Enter' && !e.shiftKey && document.activeElement !== transcriptEl) {
        // Enter sends, except while editing the transcript box (there it inserts a newline).
        e.preventDefault();
        if (!sendBtn.disabled) sendBtn.click();
      }
    }
    document.addEventListener('keydown', onKeyDown);

    // Boot the mic/socket without blocking the promise this function returns. The dialog is
    // fully event-driven from here (button clicks + WS messages + host callbacks), so a Blazor
    // JS-interop call that invoked start() completes the moment the UI is wired - NOT staying
    // open until getUserMedia resolves (a hung/denied mic would otherwise trip Blazor's 60s
    // interop timeout). bootCapture handles its own failures (showError); swallow any stray
    // rejection here so it is not logged as unhandled.
    startSegment().catch(() => {});
  }

  function notifyDotNet(ref, method, arg) {
    if (!ref) return;
    try { arg === undefined ? ref.invokeMethodAsync(method) : ref.invokeMethodAsync(method, arg); }
    catch (e) { /* circuit gone */ }
  }

  return { start: start, _createCaptureBuffer: createCaptureBuffer };
})();

// Backward-compatible shim for existing Blazor call sites:
//   cockpitDictate.start(dotNetRef, wsUrl, workletUrl, profile)
// maps onto the unified options API so BriefPane.razor / Cockpit.razor need no change.
window.cockpitDictate = {
  start: function (ref, wsUrl, workletUrl, profile) {
    return window.dictationOverlay.start({ dotNetRef: ref, wsUrl: wsUrl, workletUrl: workletUrl, profile: profile });
  },
};
