/*
 * cc-director in-page dictation client.
 *
 * One-shot dictation overlay any page in the Director can drop in.
 * Captures PCM16 via Web Audio + AudioWorklet, streams to the /dictate
 * WebSocket, calls back with the cleaned transcript.
 *
 * Usage:
 *   window.ccDictate.start({
 *     onResult: (cleanedText) => { ... },   // required - auto-insert into your input
 *     onCancel: () => { ... },              // optional
 *     profile: 'default',                   // optional, defaults to 'default'
 *   });
 *
 * No "Use it" confirmation step: the moment cleanup completes, onResult
 * fires and the overlay disposes itself. Cancel button before stop or
 * close-by-Escape calls onCancel.
 *
 * Reuses /dictate-worklet.js, /dictate, server-side AudioBuffer + provider +
 * cleanup. No duplication of dictation logic; this file is purely the UI
 * overlay + WebSocket plumbing.
 */
(function () {
  if (window.ccDictate) return;

  /*
   * Capture-first frame router - the browser twin of the desktop
   * DictationPipeline's no-loss guarantee. Captured PCM frames are routed so
   * that anything recorded BEFORE the server's transcription session is ready
   * is buffered and flushed IN ORDER the moment it becomes ready; after that,
   * frames stream straight through. This means the user can start talking the
   * instant the mic is live, even while the WebSocket/OpenAI link is still
   * coming up, and nothing they say is dropped.
   *
   * Pure and synchronous (no mic, no socket, no timers) so it is unit-testable
   * on its own - see dictate-capture-first.test.html.
   */
  function createCaptureBuffer() {
    let ready = false;
    const pending = [];
    return {
      get ready() { return ready; },
      get pendingCount() { return pending.length; },
      // Route one captured frame. send(frame) is called now if the server is
      // ready, otherwise the frame is buffered and sent on markReady - either
      // way in capture order.
      push: function (frame, send) {
        if (ready) send(frame);
        else pending.push(frame);
      },
      // Server signalled 'started': flush everything captured during the
      // connect, in order, then go live so future frames stream directly.
      markReady: function (send) {
        for (let i = 0; i < pending.length; i++) send(pending[i]);
        pending.length = 0;
        ready = true;
      },
    };
  }

  function injectStyles() {
    if (document.getElementById('cc-dictate-styles')) return;
    const css = `
      .ccd-overlay {
        position: fixed; inset: 0; z-index: 99999;
        background: rgba(0,0,0,0.65);
        display: flex; align-items: center; justify-content: center;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      }
      .ccd-card {
        width: min(560px, 92vw);
        background: #1e1e1e;
        border: 1px solid #3c3c3c;
        border-radius: 10px;
        color: #ddd;
        padding: 18px 20px;
        box-shadow: 0 12px 40px rgba(0,0,0,0.4);
      }
      .ccd-head {
        display: flex; align-items: center; gap: 14px;
        margin-bottom: 14px;
      }
      .ccd-label {
        font-family: "SFMono-Regular", Consolas, Menlo, monospace;
        font-size: 18px; font-weight: 700; letter-spacing: 0.08em;
        text-transform: uppercase; color: #f44747;
      }
      .ccd-label.initializing { color: #dcdcaa; }
      .ccd-label.transcribing { color: #dcdcaa; }
      .ccd-label.paused { color: #dcdcaa; }
      .ccd-label.error { color: #f44747; }
      .ccd-timer {
        margin-left: auto;
        font-family: "SFMono-Regular", Consolas, Menlo, monospace;
        font-size: 22px; font-weight: 600; color: #f44747;
      }
      .ccd-timer.transcribing { color: #dcdcaa; }
      .ccd-timer.paused { color: #dcdcaa; }
      .ccd-eq {
        display: flex; align-items: flex-end; justify-content: center;
        gap: 5px; height: 52px; margin: 8px 0 14px;
      }
      .ccd-eq .bar {
        width: 8px; min-height: 6px; height: 6px;
        background: #f44747; border-radius: 3px;
        transition: height 60ms linear, background 120ms;
      }
      .ccd-eq.transcribing .bar {
        background: #dcdcaa;
        animation: ccd-bar-idle 1.0s ease-in-out infinite;
      }
      .ccd-eq.transcribing .bar:nth-child(1) { animation-delay: 0.00s; }
      .ccd-eq.transcribing .bar:nth-child(2) { animation-delay: 0.08s; }
      .ccd-eq.transcribing .bar:nth-child(3) { animation-delay: 0.16s; }
      .ccd-eq.transcribing .bar:nth-child(4) { animation-delay: 0.24s; }
      .ccd-eq.transcribing .bar:nth-child(5) { animation-delay: 0.32s; }
      .ccd-eq.transcribing .bar:nth-child(6) { animation-delay: 0.40s; }
      .ccd-eq.transcribing .bar:nth-child(7) { animation-delay: 0.48s; }
      .ccd-eq.transcribing .bar:nth-child(8) { animation-delay: 0.40s; }
      .ccd-eq.transcribing .bar:nth-child(9) { animation-delay: 0.32s; }
      .ccd-eq.paused .bar { background: #6a6a6a; }
      @keyframes ccd-bar-idle {
        0%, 100% { height: 6px; }
        50%      { height: 38px; }
      }
      .ccd-transcript {
        min-height: 56px;
        background: #252526; border: 1px solid #3c3c3c;
        border-radius: 6px; padding: 10px 12px;
        font-family: "SFMono-Regular", Consolas, Menlo, monospace;
        font-size: 13px; color: #aaa;
        white-space: pre-wrap; word-break: break-word;
      }
      .ccd-foot {
        display: flex; align-items: center; gap: 10px;
        margin-top: 14px;
      }
      .ccd-hint { flex: 1; font-size: 12px; color: #888; }
      .ccd-btn {
        border: 0; border-radius: 6px;
        padding: 8px 16px; font-size: 14px; font-weight: 600;
        cursor: pointer; min-width: 88px;
      }
      .ccd-btn-cancel { background: #2d2d30; color: #ccc; }
      .ccd-btn-pause  { background: #2d2d30; color: #ccc; }
      .ccd-btn-pause:disabled { opacity: 0.6; cursor: not-allowed; }
      .ccd-btn-stop   { background: #f44747; color: #fff; }
      .ccd-btn-stop:disabled { opacity: 0.6; cursor: not-allowed; }
      .ccd-pause-glyph {
        display: inline-flex; gap: 4px; align-items: center; justify-content: center;
      }
      .ccd-pause-glyph i { width: 4px; height: 14px; background: #ccc; display: block; border-radius: 1px; }
    `;
    const style = document.createElement('style');
    style.id = 'cc-dictate-styles';
    style.textContent = css;
    document.head.appendChild(style);
  }

  function buildOverlay() {
    const overlay = document.createElement('div');
    overlay.className = 'ccd-overlay';
    overlay.innerHTML = `
      <div class="ccd-card" role="dialog" aria-modal="true" aria-label="Dictation">
        <div class="ccd-head">
          <div class="ccd-label initializing">initializing</div>
          <div class="ccd-timer">0:00.0</div>
        </div>
        <div class="ccd-eq">
          <div class="bar"></div><div class="bar"></div><div class="bar"></div>
          <div class="bar"></div><div class="bar"></div><div class="bar"></div>
          <div class="bar"></div><div class="bar"></div><div class="bar"></div>
        </div>
        <div class="ccd-transcript" data-empty="(your words will appear here)">(your words will appear here)</div>
        <div class="ccd-foot">
          <div class="ccd-hint">Starting microphone...</div>
          <button type="button" class="ccd-btn ccd-btn-cancel">Cancel</button>
          <button type="button" class="ccd-btn ccd-btn-pause" disabled><span class="ccd-pause-glyph"><i></i><i></i></span></button>
          <button type="button" class="ccd-btn ccd-btn-stop" disabled>Stop</button>
        </div>
      </div>
    `;
    return overlay;
  }

  async function start(opts) {
    opts = opts || {};
    const onResult = opts.onResult || function () {};
    const onCancel = opts.onCancel || function () {};
    const profile = opts.profile || 'default';

    if (!window.AudioWorkletNode) {
      alert('Dictation needs AudioWorklet support; please use a current browser.');
      onCancel();
      return;
    }

    injectStyles();
    const overlay = buildOverlay();
    document.body.appendChild(overlay);

    const labelEl     = overlay.querySelector('.ccd-label');
    const timerEl     = overlay.querySelector('.ccd-timer');
    const eqEl        = overlay.querySelector('.ccd-eq');
    const eqBars      = overlay.querySelectorAll('.ccd-eq .bar');
    const transcriptEl= overlay.querySelector('.ccd-transcript');
    const hintEl      = overlay.querySelector('.ccd-hint');
    const cancelBtn   = overlay.querySelector('.ccd-btn-cancel');
    const pauseBtn    = overlay.querySelector('.ccd-btn-pause');
    const stopBtn     = overlay.querySelector('.ccd-btn-stop');

    // Segment-scoped (reset on each Resume): one WebSocket + capture graph per
    // recording segment. The server's /dictate is single-shot (one start ->
    // stop -> final -> close), so a Pause finalizes the current segment and a
    // Resume opens a brand new segment. Cleaned text is accumulated across
    // segments client-side, mirroring the desktop SpeakDialog.
    let mediaStream = null;
    let ws = null;
    let audioContext = null;
    let sourceNode = null;
    let workletNode = null;
    let levelAudioContext = null;
    let levelAnalyser = null;
    let levelData = null;
    let levelRaf = null;
    let capture = null;                   // capture-first frame router for this segment

    // Send one PCM frame over the live socket (no-op if it is not open). Passed
    // to the capture buffer so buffered and live frames take the exact same path.
    function sendFrame(frame) {
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(frame);
    }

    // Persistent across segments.
    let timerHandle = null;
    let t0 = performance.now();
    let elapsedBeforeSegmentMs = 0;       // total recorded time across prior segments
    let accumulatedText = '';             // cleaned text from finalized segments
    let currentPartial = '';              // live partial for the active segment
    let finalIntent = 'complete';         // what to do when 'final' arrives: 'complete' | 'pause'
    let stage = 'initializing';           // initializing | recording | paused | transcribing | error
    let done = false;

    // Tear down just the current segment's WebSocket + audio graph. Detaches
    // the socket handlers first so the controlled close does not trip the
    // onclose/onerror error paths. Does NOT remove the overlay or fire any
    // callback, so the dialog can keep living through a pause.
    function teardownSegment() {
      try { if (levelRaf) cancelAnimationFrame(levelRaf); } catch (_) {}
      levelRaf = null;
      try { if (ws) { ws.onmessage = null; ws.onerror = null; ws.onclose = null; } } catch (_) {}
      try { if (sourceNode) sourceNode.disconnect(); } catch (_) {}
      try { if (workletNode) workletNode.disconnect(); } catch (_) {}
      try { if (audioContext) audioContext.close(); } catch (_) {}
      try { if (levelAudioContext) levelAudioContext.close(); } catch (_) {}
      try { if (mediaStream) mediaStream.getTracks().forEach(t => t.stop()); } catch (_) {}
      try { if (ws && ws.readyState === WebSocket.OPEN) ws.close(); } catch (_) {}
      ws = null; audioContext = null; sourceNode = null; workletNode = null;
      levelAudioContext = null; levelAnalyser = null; levelData = null; mediaStream = null;
      capture = null;
    }

    function teardown() {
      if (done) return;
      done = true;
      try { if (timerHandle) clearInterval(timerHandle); } catch (_) {}
      teardownSegment();
      try { document.removeEventListener('keydown', onKeyDown); } catch (_) {}
      if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
    }

    function joinText(left, right) {
      if (!left) return right || '';
      if (!right) return left;
      if (/\s$/.test(left) || /^\s/.test(right)) return left + right;
      return left + ' ' + right;
    }

    function renderTranscript() {
      const combined = joinText(accumulatedText, currentPartial);
      transcriptEl.textContent = combined || transcriptEl.getAttribute('data-empty');
      transcriptEl.style.color = combined ? '#ddd' : '#888';
    }

    function setPauseGlyph() {
      pauseBtn.innerHTML = '<span class="ccd-pause-glyph"><i></i><i></i></span>';
    }

    function complete(text) {
      teardown();
      onResult(text || '');
    }

    function cancel() {
      // Tell the server this is a deliberate cancel (not a dropped recording)
      // BEFORE teardown closes the socket. The browser flushes queued frames
      // ahead of the close handshake, so the abort arrives first. This keeps
      // the server's audio-recovery path (close-with-audio = finalize) from
      // firing on an intentional cancel.
      try {
        if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'abort' }));
      } catch (_) {}
      teardown();
      onCancel();
    }

    function onKeyDown(e) {
      if (e.key === 'Escape') {
        e.preventDefault();
        cancel();
      }
    }
    document.addEventListener('keydown', onKeyDown);
    cancelBtn.addEventListener('click', cancel);
    stopBtn.addEventListener('click', () => {
      if (stage === 'recording') {
        finalIntent = 'complete';
        sendStop();
      } else if (stage === 'paused') {
        // Nothing live to finalize; just hand back what we have accumulated.
        complete(accumulatedText);
      }
    });
    pauseBtn.addEventListener('click', () => {
      if (stage === 'recording') pause();
      else if (stage === 'paused') resume();
    });

    function pause() {
      // Freeze the timer at the click instant by banking the current segment's
      // elapsed time, then finalize the segment so the server cleans up what
      // was said. The 'final' handler (finalIntent === 'pause') accumulates the
      // text and parks the UI in the paused state.
      elapsedBeforeSegmentMs += performance.now() - t0;
      currentPartial = '';
      finalIntent = 'pause';
      pauseBtn.disabled = true;
      sendStop();
    }

    function resume() {
      currentPartial = '';
      pauseBtn.disabled = true;
      startSegment();
    }

    function setStage(s) {
      stage = s;
      labelEl.classList.remove('initializing', 'transcribing', 'paused', 'error');
      eqEl.classList.remove('transcribing', 'paused');
      timerEl.classList.remove('transcribing', 'paused');
      if (s === 'initializing') {
        labelEl.textContent = 'initializing';
        labelEl.classList.add('initializing');
        stopBtn.disabled = true;
        stopBtn.textContent = 'Stop';
        pauseBtn.disabled = true;
        setPauseGlyph();
        hintEl.textContent = 'Setting up the microphone. Do not speak yet...';
      } else if (s === 'recording') {
        labelEl.textContent = 'recording';
        stopBtn.disabled = false;
        stopBtn.textContent = 'Stop';
        pauseBtn.disabled = false;
        setPauseGlyph();
        setRecordingHint();
      } else if (s === 'paused') {
        labelEl.textContent = 'paused';
        labelEl.classList.add('paused');
        timerEl.classList.add('paused');
        eqEl.classList.add('paused');
        stopBtn.disabled = false;
        stopBtn.textContent = 'Stop';
        pauseBtn.disabled = false;
        pauseBtn.textContent = 'Resume';
        eqBars.forEach(bar => { bar.style.height = '6px'; });
        hintEl.textContent = 'Paused. Resume to keep talking, Stop to finish. Esc to cancel.';
      } else if (s === 'transcribing') {
        labelEl.textContent = 'transcribing';
        labelEl.classList.add('transcribing');
        timerEl.classList.add('transcribing');
        eqEl.classList.add('transcribing');
        stopBtn.disabled = true;
        stopBtn.textContent = 'Wait...';
        pauseBtn.disabled = true;
        hintEl.textContent = 'Running cleanup pass through gpt-4.1-nano...';
      } else if (s === 'error') {
        labelEl.textContent = 'error';
        labelEl.classList.add('error');
        stopBtn.style.display = 'none';
        pauseBtn.style.display = 'none';
        cancelBtn.textContent = 'Close';
      }
    }

    // While recording: if the transcriber link is already up, show the normal
    // controls hint; if it is still connecting, say so - but the user can talk
    // anyway (frames are buffered), so this is a non-blocking note, NOT a
    // "do not speak" gate.
    function setRecordingHint() {
      hintEl.textContent = (capture && capture.ready)
        ? 'Pause to take a break, Stop when done. Esc to cancel.'
        : 'Recording. Connecting transcriber...';
    }

    function startTimer() {
      // One interval for the whole dialog; segments after a Resume reuse it.
      // Elapsed = banked time from prior segments + the current segment.
      if (timerHandle) return;
      timerHandle = setInterval(() => {
        if (stage !== 'recording') return;
        const ms = (performance.now() - t0) + elapsedBeforeSegmentMs;
        const s = Math.floor(ms / 1000);
        const tenths = Math.floor((ms % 1000) / 100);
        timerEl.textContent = (Math.floor(s / 60)) + ':' + String(s % 60).padStart(2, '0') + '.' + tenths;
      }, 100);
    }

    function setupLevelMeter(stream) {
      try {
        levelAudioContext = new (window.AudioContext || window.webkitAudioContext)();
        const source = levelAudioContext.createMediaStreamSource(stream);
        levelAnalyser = levelAudioContext.createAnalyser();
        levelAnalyser.fftSize = 64;
        levelAnalyser.smoothingTimeConstant = 0.5;
        source.connect(levelAnalyser);
        levelData = new Uint8Array(levelAnalyser.frequencyBinCount);
        renderLevelLoop();
      } catch (e) {
        // visualization is non-essential
      }
    }

    function renderLevelLoop() {
      if (!levelAnalyser || stage !== 'recording') {
        levelRaf = requestAnimationFrame(renderLevelLoop);
        return;
      }
      levelAnalyser.getByteFrequencyData(levelData);
      const n = eqBars.length;
      const center = Math.floor(n / 2);
      const maxH = 46, minH = 6;
      for (let i = 0; i < n; i++) {
        const distFromCenter = Math.abs(i - center);
        const t = distFromCenter / Math.max(1, center);
        const bandIdx = Math.min(levelData.length - 1, Math.floor(2 + t * (levelData.length - 4)));
        const v = levelData[bandIdx] / 255;
        const h = minH + v * (maxH - minH);
        eqBars[i].style.height = h.toFixed(0) + 'px';
      }
      levelRaf = requestAnimationFrame(renderLevelLoop);
    }

    function sendStop() {
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'stop' }));
      setStage('transcribing');
    }

    async function bootCapture() {
      try {
        mediaStream = await navigator.mediaDevices.getUserMedia({
          audio: { sampleRate: 24000, channelCount: 1, echoCancellation: true, noiseSuppression: true },
        });
      } catch (e) {
        hintEl.textContent = 'Microphone access denied: ' + e.message;
        setStage('error');
        return;
      }
      setupLevelMeter(mediaStream);

      try {
        audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 24000 });
        await audioContext.audioWorklet.addModule('/dictate-worklet.js');
        sourceNode = audioContext.createMediaStreamSource(mediaStream);
        workletNode = new AudioWorkletNode(audioContext, 'pcm16-writer');
        let firstFrame = false;
        workletNode.port.onmessage = (e) => {
          // First PCM frame = capture is genuinely live. Flip to 'recording'
          // and start the timer NOW, even if the server link is not up yet -
          // the capture buffer holds these early frames so they are not lost.
          if (!firstFrame) {
            firstFrame = true;
            t0 = performance.now();
            setStage('recording');
            startTimer();
          }
          // Capture-first: stream live if the server is ready, otherwise buffer
          // in order until 'started' flushes it. Never dropped.
          if (capture) capture.push(e.data, sendFrame);
        };
        sourceNode.connect(workletNode);
      } catch (e) {
        hintEl.textContent = 'Audio capture setup failed: ' + e.message;
        setStage('error');
        return;
      }
    }

    function startSegment() {
      capture = createCaptureBuffer();
      setStage('initializing');
      // Capture-first: bring the mic up immediately AND open the socket in
      // parallel. Frames captured before the server is ready are buffered by
      // `capture` and flushed in order on 'started', so connection latency can
      // never clip the opening of the recording.
      bootCapture();
      openWebSocket();
    }

    function openWebSocket() {
      const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      ws = new WebSocket(proto + '//' + location.host + '/dictate');
      ws.binaryType = 'arraybuffer';
      ws.onmessage = (ev) => {
        let msg;
        try { msg = JSON.parse(ev.data); } catch { return; }
        switch (msg.type) {
          case 'ready':
            ws.send(JSON.stringify({ type: 'start', profile: profile }));
            break;
          case 'started':
            // Server transcription session is connected. Flush everything the
            // mic captured during the connect, in order, then stream live.
            // Capture was already started in startSegment, so the opening words
            // are sitting in the buffer waiting for exactly this moment.
            if (capture) capture.markReady(sendFrame);
            if (stage === 'recording') setRecordingHint();
            break;
          case 'partial':
            currentPartial = msg.text || '';
            renderTranscript();
            break;
          case 'state':
            // informational; not surfaced in the minimal overlay
            break;
          case 'transcribing':
            setStage('transcribing');
            break;
          case 'final': {
            const cleaned = msg.cleaned || msg.raw || '';
            accumulatedText = joinText(accumulatedText, cleaned);
            currentPartial = '';
            if (finalIntent === 'pause') {
              // End of a segment due to Pause: bank the text, drop the socket
              // for this segment, and park in the paused state. A later Resume
              // opens a fresh segment that appends to accumulatedText.
              finalIntent = 'complete';
              teardownSegment();
              setStage('paused');
              renderTranscript();
            } else {
              complete(accumulatedText);
            }
            break;
          }
          case 'error':
            hintEl.textContent = 'Server error: ' + (msg.message || 'unknown');
            setStage('error');
            break;
        }
      };
      ws.onerror = () => {
        if (stage !== 'transcribing') {
          hintEl.textContent = 'WebSocket connection failed.';
          setStage('error');
        }
      };
      ws.onclose = () => {
        // If we never received final, treat as error so the user can close.
        if (stage === 'recording' || stage === 'transcribing') {
          if (!done) {
            hintEl.textContent = 'Connection closed.';
            setStage('error');
          }
        }
      };
    }

    startSegment();
  }

  // _createCaptureBuffer is exposed for unit testing the no-loss routing in
  // isolation (see dictate-capture-first.test.html). Underscore = internal.
  window.ccDictate = { start: start, _createCaptureBuffer: createCaptureBuffer };
})();
