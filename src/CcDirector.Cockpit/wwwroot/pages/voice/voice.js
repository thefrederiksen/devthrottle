/*
 * Voice Mode - Slice 1: the visible audio loop.
 *
 * Session list -> open a session -> Speak (record) -> resumable upload to the Gateway's
 * voice-turn endpoints -> poll -> play the spoken reply. All calls are same-origin against
 * the Gateway front door (the page is served behind it), so the cc-gateway-token cookie
 * authenticates automatically - the same pattern keys.html/settings.html use.
 *
 * Offline-first (IndexedDB + background sync) is Slice 2; here the upload runs right after
 * the recording stops. The chunked upload contract is already resume-safe on the server.
 */
(function () {
  "use strict";

  // ===== tiny DOM + fetch helpers =====
  var $ = function (id) { return document.getElementById(id); };

  // The Gateway token is injected into the page server-side. A value still starting with the
  // "__" placeholder means it was served without injection (e.g. the raw static path) - treat
  // that as absent. The voice-turn endpoints are Bearer-or-cookie gated, so we send Bearer.
  var GW_TOKEN = (function () {
    var t = (typeof window !== "undefined" && window.__GW_TOKEN__) || "";
    return t.indexOf("__") === 0 ? "" : t;
  })();

  function authHeaders(extra) {
    var h = Object.assign({}, extra || {});
    if (GW_TOKEN) h["Authorization"] = "Bearer " + GW_TOKEN;
    return h;
  }

  function show(view) {
    $("list-view").classList.toggle("hidden", view !== "list");
    $("session-view").classList.toggle("hidden", view !== "session");
  }

  async function api(path, opts) {
    opts = opts || {};
    opts.headers = authHeaders(opts.headers);
    var resp = await fetch(path, opts);
    var text = await resp.text();
    var data = null;
    if (text) { try { data = JSON.parse(text); } catch (e) { data = null; } }
    return { ok: resp.ok, status: resp.status, data: data, raw: text };
  }

  // ===== session list =====
  var current = null; // { sessionId, name, repoPath, ... }

  function colorClass(c) {
    if (c === "green" || c === "blue" || c === "amber" || c === "red") return c;
    return "";
  }

  function sessionTitle(s) {
    if (s.name && s.name.trim()) return s.name.trim();
    var p = (s.repoPath || "").replace(/[\\/]+$/, "");
    var base = p.split(/[\\/]/).pop();
    return base || "session";
  }

  function sessionSubtitle(s) {
    var parts = [];
    if (s.activityState) parts.push(s.activityState);
    if (s.machineName) parts.push(s.machineName);
    if (typeof s.idleSeconds === "number" && s.idleSeconds > 0) {
      parts.push("idle " + formatIdle(s.idleSeconds));
    }
    return parts.join("  -  ");
  }

  function formatIdle(sec) {
    sec = Math.floor(sec);
    if (sec < 60) return sec + "s";
    if (sec < 3600) return Math.floor(sec / 60) + "m";
    return Math.floor(sec / 3600) + "h";
  }

  async function loadSessions() {
    var status = $("list-status");
    status.textContent = "Loading sessions...";
    var r = await api("/sessions");
    if (!r.ok) {
      status.textContent = r.status === 401
        ? "Not connected. Pair this phone with the Gateway first."
        : "Could not load sessions (" + r.status + ").";
      return;
    }
    var sessions = Array.isArray(r.data) ? r.data : (r.data && r.data.sessions) || [];
    renderSessions(sessions);
    status.textContent = sessions.length ? (sessions.length + " session(s)") : "No sessions yet.";
  }

  function renderSessions(sessions) {
    var ul = $("session-list");
    ul.innerHTML = "";
    sessions.forEach(function (s) {
      var li = document.createElement("li");

      var dot = document.createElement("span");
      dot.className = "dot " + colorClass(s.statusColor);

      var main = document.createElement("div");
      main.className = "s-main";
      var name = document.createElement("div");
      name.className = "s-name";
      name.textContent = sessionTitle(s);
      var sub = document.createElement("div");
      sub.className = "s-sub";
      sub.textContent = sessionSubtitle(s);
      main.appendChild(name);
      main.appendChild(sub);

      li.appendChild(dot);
      li.appendChild(main);
      li.addEventListener("click", function () { openSession(s); });
      ul.appendChild(li);
    });
  }

  // ===== session voice view =====
  var lastReplyUrl = null;

  function openSession(s) {
    current = s;
    $("session-name").textContent = sessionTitle(s);
    $("session-state").textContent = s.activityState || s.status || "-";
    $("session-repo").textContent = s.repoPath || "";
    setStage("Tap Speak and talk.", "");
    $("reply-box").textContent = "";
    setPlayable(null);
    show("session");
  }

  function setStage(text, kind) {
    var el = $("stage-line");
    el.textContent = text;
    el.className = "stage" + (kind ? " " + kind : "");
  }

  function setPlayable(url) {
    if (lastReplyUrl) { URL.revokeObjectURL(lastReplyUrl); lastReplyUrl = null; }
    lastReplyUrl = url;
    $("play-btn").disabled = !url;
  }

  // ===== recording =====
  var mediaRecorder = null;
  var recChunks = [];
  var recMime = "audio/webm";
  var recording = false;

  function pickMime() {
    var prefs = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/aac", "audio/ogg"];
    if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported) {
      for (var i = 0; i < prefs.length; i++) {
        if (MediaRecorder.isTypeSupported(prefs[i])) return prefs[i];
      }
    }
    return "";
  }

  async function toggleSpeak() {
    if (recording) { stopRecording(); return; }
    await startRecording();
  }

  async function startRecording() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      setStage("This browser cannot record audio.", "error");
      return;
    }
    var stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (e) {
      setStage("Microphone permission denied.", "error");
      return;
    }
    recMime = pickMime();
    var opts = recMime ? { mimeType: recMime } : undefined;
    try {
      mediaRecorder = new MediaRecorder(stream, opts);
    } catch (e) {
      mediaRecorder = new MediaRecorder(stream);
    }
    if (mediaRecorder.mimeType) recMime = mediaRecorder.mimeType;
    recChunks = [];

    mediaRecorder.ondataavailable = function (ev) {
      if (ev.data && ev.data.size > 0) recChunks.push(ev.data);
    };
    mediaRecorder.onstop = function () {
      stream.getTracks().forEach(function (t) { t.stop(); });
      uploadAndRun(recChunks, recMime).catch(function (e) {
        setStage("Upload failed: " + e, "error");
        setSpeakUi(false);
      });
    };

    // One blob per second so a long utterance is many ordered fragments (resume-friendly).
    mediaRecorder.start(1000);
    recording = true;
    setSpeakUi(true);
    setStage("Listening... tap Stop when done.", "active");
  }

  function stopRecording() {
    if (mediaRecorder && mediaRecorder.state !== "inactive") mediaRecorder.stop();
    recording = false;
    setSpeakUi(false);
  }

  function setSpeakUi(isRecording) {
    var btn = $("speak-btn");
    btn.textContent = isRecording ? "Stop" : "Speak";
    btn.classList.toggle("recording", isRecording);
  }

  // ===== upload + turn (Slice 1: record-then-upload) =====
  function mimeExtSafe(mime) { return (mime || "audio/webm").split(";")[0]; }

  async function uploadAndRun(chunks, mime) {
    if (!current) return;
    if (!chunks.length) { setStage("Nothing recorded.", "error"); return; }
    var sid = current.sessionId;
    var turnGuid = (crypto.randomUUID ? crypto.randomUUID() : fallbackGuid());

    setStage("Saving recording...", "active");

    // 1) register the upload (Idempotency-Key = our turn id -> a retry never duplicates)
    var reg = await api("/sessions/" + sid + "/voice-turn/upload", {
      method: "POST",
      headers: { "Idempotency-Key": turnGuid },
    });
    if (!reg.ok || !reg.data || !reg.data.upload_id) {
      throw new Error("register " + reg.status);
    }
    var uploadId = reg.data.upload_id;

    // 2) PUT each ordered fragment
    setStage("Uploading...", "active");
    for (var i = 0; i < chunks.length; i++) {
      var buf = await chunks[i].arrayBuffer();
      var put = await fetch("/sessions/" + sid + "/voice-turn/upload/" + uploadId + "/chunk/" + i, {
        method: "PUT",
        headers: authHeaders({ "Content-Type": "application/octet-stream" }),
        body: buf,
      });
      if (!put.ok) throw new Error("chunk " + i + " -> " + put.status);
    }

    // 3) complete -> 202 { turn_id }  (409 would list missing chunks; we sent them all)
    var comp = await api("/sessions/" + sid + "/voice-turn/upload/" + uploadId + "/complete", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ totalChunks: chunks.length, mime: mimeExtSafe(mime) }),
    });
    if (comp.status === 409) {
      throw new Error("upload incomplete (server missing chunks)");
    }
    if (comp.status !== 202 || !comp.data || !comp.data.turn_id) {
      throw new Error("complete " + comp.status + " " + (comp.data && comp.data.error || ""));
    }

    await pollTurn(sid, comp.data.turn_id);
  }

  var STAGE_TEXT = {
    submitted: "Sent. Waiting...",
    transcribing: "Transcribing...",
    transcript: "Heard you. Thinking...",
    waiting: "Session busy, waiting...",
    thinking: "Claude is working...",
    summarizing: "Preparing the reply...",
  };

  async function pollTurn(sid, turnId) {
    var deadline = Date.now() + 5 * 60 * 1000;
    while (Date.now() < deadline) {
      var r = await api("/sessions/" + sid + "/voice-turn/" + turnId);
      if (!r.ok) { await sleep(800); continue; }
      var stage = r.data && r.data.stage;
      if (stage === "reply") { onReply(sid, turnId, r.data); return; }
      if (stage === "error") {
        setStage(replyError(r.data && r.data.message), "error");
        return;
      }
      setStage(STAGE_TEXT[stage] || "Working...", "active");
      await sleep(900);
    }
    setStage("Timed out waiting for a reply.", "error");
  }

  function replyError(msg) {
    if (msg && msg.indexOf("no_key") >= 0) return "No OpenAI key set on the Gateway (needed to transcribe).";
    return "Error: " + (msg || "unknown");
  }

  function onReply(sid, turnId, data) {
    var summary = (data && data.summary) || "";
    $("reply-box").textContent = summary || "(no spoken summary)";
    setStage(summary ? "Reply ready." : "Done.", "");

    var b64 = data && data.audioBase64;
    if (b64 && b64.length) {
      var url = base64ToBlobUrl(b64, "audio/mpeg");
      setPlayable(url);
      playUrl(url);
    } else {
      // No TTS audio (e.g. no key). The text reply still shows; fall back to the durable
      // audio endpoint in case it lands later.
      setPlayable(null);
    }
  }

  function playReply() { if (lastReplyUrl) playUrl(lastReplyUrl); }

  function playUrl(url) {
    var audio = new Audio(url);
    audio.play().catch(function () { /* autoplay may require a tap; Play button covers it */ });
  }

  // ===== util =====
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  function base64ToBlobUrl(b64, type) {
    var bin = atob(b64);
    var len = bin.length;
    var bytes = new Uint8Array(len);
    for (var i = 0; i < len; i++) bytes[i] = bin.charCodeAt(i);
    return URL.createObjectURL(new Blob([bytes], { type: type }));
  }

  function fallbackGuid() {
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, function (c) {
      var r = (Math.random() * 16) | 0, v = c === "x" ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    });
  }

  // ===== wire up =====
  $("refresh-btn").addEventListener("click", loadSessions);
  $("back-btn").addEventListener("click", function () { show("list"); loadSessions(); });
  $("speak-btn").addEventListener("click", function () { toggleSpeak(); });
  $("play-btn").addEventListener("click", playReply);

  show("list");
  loadSessions();
})();
