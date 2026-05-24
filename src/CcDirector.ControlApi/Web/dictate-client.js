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
      .ccd-label.error { color: #f44747; }
      .ccd-timer {
        margin-left: auto;
        font-family: "SFMono-Regular", Consolas, Menlo, monospace;
        font-size: 22px; font-weight: 600; color: #f44747;
      }
      .ccd-timer.transcribing { color: #dcdcaa; }
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
      .ccd-btn-stop   { background: #f44747; color: #fff; }
      .ccd-btn-stop:disabled { opacity: 0.6; cursor: not-allowed; }
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
          <div class="ccd-hint">Setting up the microphone. Do not speak yet...</div>
          <button type="button" class="ccd-btn ccd-btn-cancel">Cancel</button>
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
    const stopBtn     = overlay.querySelector('.ccd-btn-stop');

    let mediaStream = null;
    let ws = null;
    let audioContext = null;
    let sourceNode = null;
    let workletNode = null;
    let levelAudioContext = null;
    let levelAnalyser = null;
    let levelData = null;
    let levelRaf = null;
    let timerHandle = null;
    let t0 = performance.now();
    let stage = 'initializing'; // initializing | recording | transcribing | error
    let done = false;

    function teardown() {
      if (done) return;
      done = true;
      try { if (timerHandle) clearInterval(timerHandle); } catch (_) {}
      try { if (levelRaf) cancelAnimationFrame(levelRaf); } catch (_) {}
      try { if (sourceNode) sourceNode.disconnect(); } catch (_) {}
      try { if (workletNode) workletNode.disconnect(); } catch (_) {}
      try { if (audioContext) audioContext.close(); } catch (_) {}
      try { if (levelAudioContext) levelAudioContext.close(); } catch (_) {}
      try { if (mediaStream) mediaStream.getTracks().forEach(t => t.stop()); } catch (_) {}
      try { if (ws && ws.readyState === WebSocket.OPEN) ws.close(); } catch (_) {}
      try { document.removeEventListener('keydown', onKeyDown); } catch (_) {}
      if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
    }

    function complete(text) {
      teardown();
      onResult(text || '');
    }

    function cancel() {
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
      if (stage !== 'recording') return;
      sendStop();
    });

    function setStage(s) {
      stage = s;
      labelEl.classList.remove('initializing', 'transcribing', 'error');
      eqEl.classList.remove('transcribing');
      timerEl.classList.remove('transcribing');
      if (s === 'initializing') {
        labelEl.textContent = 'initializing';
        labelEl.classList.add('initializing');
        stopBtn.disabled = true;
        stopBtn.textContent = 'Stop';
        hintEl.textContent = 'Setting up the microphone. Do not speak yet...';
      } else if (s === 'recording') {
        labelEl.textContent = 'recording';
        stopBtn.disabled = false;
        stopBtn.textContent = 'Stop';
        hintEl.textContent = 'Click Stop when you are done speaking. Esc to cancel.';
      } else if (s === 'transcribing') {
        labelEl.textContent = 'transcribing';
        labelEl.classList.add('transcribing');
        timerEl.classList.add('transcribing');
        eqEl.classList.add('transcribing');
        stopBtn.disabled = true;
        stopBtn.textContent = 'Wait...';
        hintEl.textContent = 'Running cleanup pass through gpt-4.1-nano...';
      } else if (s === 'error') {
        labelEl.textContent = 'error';
        labelEl.classList.add('error');
        stopBtn.style.display = 'none';
        cancelBtn.textContent = 'Close';
      }
    }

    function startTimer() {
      timerHandle = setInterval(() => {
        if (stage !== 'recording') return;
        const ms = performance.now() - t0;
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
          // First PCM frame = capture is genuinely live. Only now flip to
          // 'recording', invite the user to speak, and start the timer; this
          // closes the window where the UI said "recording" but no audio was
          // streaming yet (the source of the clipped first word).
          if (!firstFrame) {
            firstFrame = true;
            t0 = performance.now();
            setStage('recording');
            startTimer();
          }
          if (ws && ws.readyState === WebSocket.OPEN) ws.send(e.data);
        };
        sourceNode.connect(workletNode);
      } catch (e) {
        hintEl.textContent = 'Audio capture setup failed: ' + e.message;
        setStage('error');
        return;
      }
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
            // Start the worklet capture once the server is ready. The overlay
            // stays in 'initializing' and the timer does not run until the
            // worklet actually emits its first PCM frame (see bootCapture), so
            // we never invite the user to speak into a dead pipeline.
            bootCapture();
            break;
          case 'partial':
            transcriptEl.textContent = msg.text || '(your words will appear here)';
            transcriptEl.style.color = (msg.text ? '#ddd' : '#888');
            break;
          case 'state':
            // informational; not surfaced in the minimal overlay
            break;
          case 'transcribing':
            setStage('transcribing');
            break;
          case 'final':
            complete(msg.cleaned || msg.raw || '');
            break;
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

    openWebSocket();
  }

  window.ccDictate = { start: start };
})();
