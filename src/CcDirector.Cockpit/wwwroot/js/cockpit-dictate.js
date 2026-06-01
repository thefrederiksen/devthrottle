// Cockpit Dictate dialog: a desktop-parity modal for the cockpit's Speak button.
//
// This is the browser twin of the desktop SpeakDialog (src/CcDirector.Avalonia/Voice/
// SpeakDialog.axaml). It overlays a modal with a mic selector, a RECORDING/TRANSCRIBING/
// PAUSED status + timer, a live equalizer, an editable transcript, and the same four
// actions: Cancel, Pause/Resume, Insert (drop text into the composer, do not send) and
// Send (drop into the composer AND submit).
//
// Audio is captured as 24 kHz mono PCM16 via the vendored pcm16-writer AudioWorklet and
// streamed over a WebSocket to the OWNING DIRECTOR's /dictate endpoint (the same server
// pipeline the desktop dialog uses: OpenAI realtime + shared dictionary + verbatim
// cleanup). The worklet is loaded SAME-ORIGIN from the cockpit (a cross-origin worklet
// module would be blocked); the dictate socket is cross-origin to the Director, exactly
// like the cockpit terminal stream.
//
// Capture-first: frames recorded before the server says 'started' are buffered in order
// and flushed the instant it is, so the opening words are never clipped by connect latency.
//
// Blazor callbacks (DotNetObjectReference<Cockpit>):
//   OnDictateInsert(text)  - Insert pressed: put cleaned text in the composer, no submit.
//   OnDictateSend(text)    - Send pressed: put cleaned text in the composer and submit.
//   OnDictateCancel()      - Cancel/closed with no text.
window.cockpitDictate = (function () {
  // Capture-first frame router (pure/synchronous): buffer frames until the server is ready,
  // then flush in capture order and stream live thereafter.
  function createCaptureBuffer() {
    let ready = false;
    const pending = [];
    return {
      get ready() { return ready; },
      push: function (frame, send) { if (ready) send(frame); else pending.push(frame); },
      markReady: function (send) { for (const f of pending) send(f); pending.length = 0; ready = true; },
    };
  }

  function injectStyles() {
    if (document.getElementById('cockpit-dictate-styles')) return;
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
      .cd-pause { background: #2D2D30; color: #CCC; min-width: 100px; }
      .cd-insert { background: #16A34A; color: #fff; }
      .cd-send { background: #007ACC; color: #fff; min-width: 100px; }
      .cd-pause-glyph { display: inline-flex; gap: 4px; align-items: center; justify-content: center; }
      .cd-pause-glyph i { width: 4px; height: 14px; background: #CCC; display: block; border-radius: 1px; }
    `;
    const style = document.createElement('style');
    style.id = 'cockpit-dictate-styles';
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
        <textarea class="cd-transcript" readonly placeholder="(your words will appear here)"></textarea>
        <div class="cd-foot">
          <button type="button" class="cd-btn cd-cancel">Cancel</button>
          <button type="button" class="cd-btn cd-pause"><span class="cd-pause-glyph"><i></i><i></i></span></button>
          <div class="cd-spacer"></div>
          <button type="button" class="cd-btn cd-insert">Insert</button>
          <button type="button" class="cd-btn cd-send">Send</button>
        </div>
      </div>`;
    return overlay;
  }

  async function start(ref, wsUrl, workletUrl, profile) {
    workletUrl = workletUrl || '/js/dictate-worklet.js';
    profile = profile || 'default';

    if (!window.AudioWorkletNode || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      notify(ref, 'OnDictateCancel');
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
    const pauseBtn     = overlay.querySelector('.cd-pause');
    const insertBtn    = overlay.querySelector('.cd-insert');
    const sendBtn      = overlay.querySelector('.cd-send');

    // ---- per-segment state (reset on each Resume / device change) ----
    let mediaStream = null, ws = null, audioCtx = null, sourceNode = null, workletNode = null;
    let levelCtx = null, analyser = null, levelData = null, rafId = null, capture = null;

    // ---- persistent across segments ----
    let stage = 'starting';            // starting | recording | transcribing | paused | error
    let finalIntent = 'pause';         // what to do when 'final' arrives: pause | insert | send
    let accumulatedText = '';          // cleaned text from finalized segments
    let currentPartial = '';
    let selectedDeviceId = '';         // '' = system default
    let done = false;
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
      transcriptEl.value = joinText(accumulatedText, currentPartial);
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
        setPauseGlyph();
      } else if (s === 'transcribing') {
        statusEl.textContent = 'TRANSCRIBING';
        transcriptEl.setAttribute('readonly', '');
        pauseBtn.disabled = true; insertBtn.disabled = true; sendBtn.disabled = true;
        micSel.disabled = true;
        bars.forEach(b => { b.classList.add('amber'); b.style.height = '34px'; });
        hintEl.textContent = '';
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
      // Switch device: keep accumulated text (re-seed from the edit box), drop the
      // in-flight partial, and start a fresh segment on the new device.
      accumulatedText = transcriptEl.value || '';
      currentPartial = '';
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
          if (!first) { first = true; t0 = performance.now(); setStage('recording'); startTimer(); }
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
          case 'partial': currentPartial = m.text || ''; renderTranscript(); break;
          case 'transcribing': setStage('transcribing'); break;
          case 'final': {
            const cleaned = (m.cleaned || m.raw || '');
            accumulatedText = joinText(accumulatedText, cleaned);
            currentPartial = '';
            if (finalIntent === 'pause') { teardownSegment(); setStage('paused'); }
            else if (finalIntent === 'insert') finishWith('OnDictateInsert');
            else if (finalIntent === 'send') finishWith('OnDictateSend');
            break;
          }
          case 'error': showError('Server error: ' + (m.message || 'unknown')); break;
        }
      };
      ws.onerror = () => { if (stage !== 'transcribing') showError('WebSocket connection failed.'); };
      // Include 'starting': a socket that opens then closes before 'ready'/'started' (e.g. the
      // Director rejects/closes the /dictate upgrade) would otherwise leave the dialog stuck on
      // STARTING with no error and no callback - keeping the C# Speak button disabled forever.
      ws.onclose = () => {
        if (!done && (stage === 'starting' || stage === 'recording' || stage === 'transcribing'))
          showError(stage === 'starting' ? 'Dictation stream closed before it was ready.' : 'Connection closed.');
      };
    }

    async function startSegment() {
      capture = createCaptureBuffer();
      setStage('starting');
      const ok = await bootCapture();   // mic up immediately (capture-first)...
      if (!ok) return;
      openSocket();                     // ...socket opens in parallel; early frames buffer
    }

    // ---------- finalize / send-stop ----------
    function sendStop() { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'stop' })); setStage('transcribing'); }

    // Recording -> stop -> wait for 'final', then dispatch by finalIntent.
    function finalizeFromRecording(intent) { finalIntent = intent; sendStop(); }

    function finishWith(method) {
      const text = (transcriptEl.value || accumulatedText || '').trim();
      teardownAll();
      notify(ref, method, text);
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
      notify(ref, 'OnDictateCancel');
    });
    pauseBtn.addEventListener('click', () => {
      if (stage === 'recording') { elapsedBeforeMs += performance.now() - t0; currentPartial = ''; pauseBtn.disabled = true; finalizeFromRecording('pause'); }
      else if (stage === 'paused') { accumulatedText = transcriptEl.value || ''; currentPartial = ''; pauseBtn.disabled = true; elapsedBeforeMs = 0; startSegment(); }
    });
    insertBtn.addEventListener('click', () => {
      if (stage === 'recording') finalizeFromRecording('insert');
      else if (stage === 'paused') finishWith('OnDictateInsert');
    });
    sendBtn.addEventListener('click', () => {
      if (stage === 'recording') finalizeFromRecording('send');
      else if (stage === 'paused') finishWith('OnDictateSend');
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
    // fully event-driven from here (button clicks + WS messages + DotNet callbacks), so the
    // Blazor JS-interop call that invoked start() should complete the moment the UI is wired -
    // NOT stay open until getUserMedia resolves. Holding it open meant a hung/denied mic (no
    // device, or a user ignoring the permission prompt) tripped Blazor's 60s interop timeout,
    // which surfaced a bogus "dictation unavailable: A task was canceled" error on a dialog
    // that was actually fine. bootCapture handles its own failures (showError), so a rejection
    // here is already accounted for; swallow any stray rejection so it isn't logged as unhandled.
    startSegment().catch(() => {});
  }

  function notify(ref, method, arg) {
    if (!ref) return;
    try { arg === undefined ? ref.invokeMethodAsync(method) : ref.invokeMethodAsync(method, arg); }
    catch (e) { /* circuit gone */ }
  }

  return { start };
})();
