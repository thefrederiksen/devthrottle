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
    $("newsession-view").classList.toggle("hidden", view !== "newsession");
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
  var lastSessions = []; // the most recent /sessions snapshot (used to pick a Director id)

  // The ONE effective status color every client renders, mirroring the Gateway's
  // SessionOrdering.EffectiveColor: on-hold sinks to grey, a user "explain" deep-dive is
  // orange, an in-flight brief on a red turn is yellow, otherwise the Director's raw color.
  // (Previously this page used s.statusColor directly, so an on-hold session - whose raw
  // color is still red - showed a red dot identical to a needs-you session.)
  function effectiveColor(s) {
    if (s.onHold) return "grey";
    if (s.briefingState === "Explaining") return "orange";
    if (s.briefingState === "Briefing" && (s.statusColor || "").toLowerCase() === "red") return "yellow";
    return s.statusColor || "unknown";
  }

  // Mirror SessionRail.razor DotColor exactly so the dot matches the desktop/Cockpit.
  function dotHex(c) {
    switch (c) {
      case "green":  return "#22C55E";
      case "blue":   return "#3B82F6";
      case "yellow": return "#F59E0B";
      case "orange": return "#F97316";
      case "red":    return "#F14C4C";
      default:       return "#6B7280"; // grey / unknown / on-hold
    }
  }

  // Human-friendly status line, honoring on-hold and the effective color.
  function statusLabel(s) {
    if (s.onHold) return "On hold";
    switch (effectiveColor(s)) {
      case "red":    return "Needs you";
      case "yellow": return "Reading...";
      case "orange": return "Explaining...";
      case "blue":   return "Working";
      case "green":  return "Ready";
      default:       return s.activityState || s.status || "-";
    }
  }

  function sessionTitle(s) {
    if (s.name && s.name.trim()) return s.name.trim();
    var p = (s.repoPath || "").replace(/[\\/]+$/, "");
    var base = p.split(/[\\/]/).pop();
    return base || "session";
  }

  function sessionSubtitle(s) {
    var parts = [statusLabel(s)];
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

  function turnLabel(n) {
    return n + (n === 1 ? " turn" : " turns");
  }

  // The session's completed voice-turn count, read from the Gateway's durable archive
  // (GET /sessions/{id}/voice-turns -> { turns: [...] }). Lightweight: an archive read,
  // no JSONL parse. Returns the count, or null when the count cannot be read (so the
  // caller shows nothing rather than a misleading "0").
  async function fetchTurnCount(sid) {
    var r = await api("/sessions/" + sid + "/voice-turns");
    if (!r.ok || !r.data || !Array.isArray(r.data.turns)) return null;
    return r.data.turns.length;
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
    lastSessions = sessions;
    renderSessions(sessions);
    status.textContent = sessions.length ? (sessions.length + " session(s)") : "No sessions yet.";
  }

  function renderSessions(sessions) {
    var ul = $("session-list");
    ul.innerHTML = "";
    sessions.forEach(function (s) {
      var li = document.createElement("li");

      var dot = document.createElement("span");
      dot.className = "dot";
      dot.style.background = dotHex(effectiveColor(s));

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

      // Turn count fills in asynchronously so the row appears immediately (no blocking
      // on a per-row fetch); the count lands when the archive read returns.
      var turns = document.createElement("div");
      turns.className = "s-turns";
      fillTurnCount(turns, s.sessionId);

      li.appendChild(dot);
      li.appendChild(main);
      li.appendChild(turns);
      li.addEventListener("click", function () { openSession(s); });
      ul.appendChild(li);
    });
  }

  // Fill an element with "N turns" once the voice-turn count is read; leave it empty
  // (and unseen) when the count cannot be read, so a stale/failed fetch shows nothing
  // rather than a misleading "0 turns".
  async function fillTurnCount(el, sid) {
    var n = await fetchTurnCount(sid);
    if (n === null) { el.textContent = ""; return; }
    el.textContent = turnLabel(n);
  }

  // ===== session voice view =====
  var lastReplyUrl = null;

  function openSession(s) {
    current = s;
    $("session-name").textContent = sessionTitle(s);
    $("session-state").textContent = statusLabel(s);
    $("session-repo").textContent = s.repoPath || "";
    setHeaderTurns(s.sessionId);
    setStage("Tap Speak and talk.", "");
    $("reply-box").textContent = "";
    setPlayable(null);
    loadHistory(s.sessionId);
    show("session");
  }

  // Show the same voice-turn count in the open session header. Hidden until the count
  // is read (and stays hidden if it cannot be read), so the header never shows a
  // misleading "0 turns" from a failed fetch.
  async function setHeaderTurns(sid) {
    var el = $("session-turns");
    el.textContent = "";
    el.classList.add("hidden");
    var n = await fetchTurnCount(sid);
    if (n === null) return;
    el.textContent = turnLabel(n);
    el.classList.remove("hidden");
  }

  // ===== per-session turn history (issue #423) =====
  // historySid guards against a late /voice-turns response landing after the user
  // has navigated to a different session - we only render when the response is for
  // the session currently open.
  var historySid = null;

  // Load and render the open session's completed turns, newest first (the archive
  // already returns them newest-first). Empty state shows when there are none.
  async function loadHistory(sid) {
    historySid = sid;
    var list = $("history-list");
    var empty = $("history-empty");
    list.innerHTML = "";
    empty.textContent = "Loading history...";
    empty.classList.remove("hidden");

    var r = await api("/sessions/" + sid + "/voice-turns");
    if (sid !== historySid) return; // user switched sessions mid-flight

    if (!r.ok || !r.data || !Array.isArray(r.data.turns)) {
      empty.textContent = "Could not load history.";
      empty.classList.remove("hidden");
      return;
    }
    renderHistory(sid, r.data.turns);
  }

  function renderHistory(sid, turns) {
    var list = $("history-list");
    var empty = $("history-empty");
    list.innerHTML = "";
    if (!turns.length) {
      empty.textContent = "No turns yet. Tap Speak to start.";
      empty.classList.remove("hidden");
      return;
    }
    empty.classList.add("hidden");
    turns.forEach(function (t) { list.appendChild(historyItem(sid, t)); });
  }

  // Prepend a freshly-completed turn to the top of the history without a full reload,
  // so a new reply appears immediately. Skipped if the turn is already listed (e.g. a
  // later refresh raced ahead) so it never double-lists.
  function prependHistory(sid, turn) {
    if (sid !== historySid || !turn || !turn.turn_id) return;
    var list = $("history-list");
    if (list.querySelector('[data-turn-id="' + cssAttr(turn.turn_id) + '"]')) return;
    $("history-empty").classList.add("hidden");
    list.insertBefore(historyItem(sid, turn), list.firstChild);
  }

  function historyItem(sid, t) {
    var li = document.createElement("li");
    li.setAttribute("data-turn-id", t.turn_id || "");

    var main = document.createElement("div");
    main.className = "hist-main";

    var summary = document.createElement("div");
    summary.className = "hist-summary";
    summary.textContent = (t.summary && t.summary.trim()) || "(no spoken summary)";
    main.appendChild(summary);

    if (t.transcript && t.transcript.trim()) {
      var tr = document.createElement("div");
      tr.className = "hist-transcript";
      tr.textContent = t.transcript.trim();
      main.appendChild(tr);
    }

    var time = document.createElement("div");
    time.className = "hist-time";
    time.textContent = formatWhen(t.created_at);
    main.appendChild(time);

    li.appendChild(main);

    // Replay button only when the turn has durable reply audio to fetch.
    if (t.has_audio && t.turn_id) {
      var play = document.createElement("button");
      play.className = "secondary hist-play";
      play.textContent = "Play";
      play.addEventListener("click", function () { playTurnAudio(sid, t.turn_id, play); });
      li.appendChild(play);
    }
    return li;
  }

  // Fetch and play a past turn's reply audio. The /audio endpoint is token-gated, so we
  // fetch with auth headers and play the bytes as a blob URL (same approach as the live
  // reply's base64 path) rather than pointing an <audio src> at the URL.
  async function playTurnAudio(sid, turnId, btn) {
    var prev = btn.textContent;
    btn.disabled = true;
    btn.textContent = "...";
    var resp = await fetch("/sessions/" + sid + "/voice-turn/" + turnId + "/audio", {
      headers: authHeaders(),
    });
    if (!resp.ok) {
      btn.textContent = "No audio";
      setTimeout(function () { btn.textContent = prev; btn.disabled = false; }, 1500);
      return;
    }
    var buf = await resp.arrayBuffer();
    var url = URL.createObjectURL(new Blob([buf], { type: "audio/mpeg" }));
    var audio = new Audio(url);
    audio.addEventListener("ended", function () { URL.revokeObjectURL(url); });
    audio.play().catch(function () { /* a tap already gestured; ignore autoplay edge */ });
    btn.textContent = prev;
    btn.disabled = false;
  }

  function formatWhen(iso) {
    if (!iso) return "";
    var d = new Date(iso);
    if (isNaN(d.getTime())) return "";
    return d.toLocaleString();
  }

  // Escape a value for safe use inside an attribute-selector string.
  function cssAttr(v) { return String(v).replace(/["\\]/g, "\\$&"); }

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

    // Show the just-completed turn at the top of the history immediately - no manual
    // refresh. The same turn is now in the durable archive, so its Play button replays
    // from the /audio endpoint just like any older entry.
    prependHistory(sid, {
      turn_id: turnId,
      summary: summary,
      transcript: (data && data.transcript) || "",
      has_audio: !!(b64 && b64.length),
      created_at: new Date().toISOString(),
    });

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

  // ===== new session: pick a repo, create, open =====
  // The page is served behind the Gateway, so a session is created on a chosen Director
  // via POST /directors/{id}/sessions; the repo list comes from GET /directors/{id}/repos.
  var nsDirectorId = null; // the Director we create on
  var nsRepos = [];        // [{ name, path, lastUsed }]
  var nsSelectedPath = null;
  var nsCreating = false;

  function nsSetStatus(text) { $("ns-status").textContent = text; }

  function nsSetError(text) {
    var el = $("ns-error");
    if (!text) { el.classList.add("hidden"); el.textContent = ""; return; }
    el.textContent = text;
    el.classList.remove("hidden");
  }

  // Choose the Director to create on. Prefer one we already see owning a listed session
  // (the phone's own Director); otherwise ask the Gateway. With exactly one Director, use
  // it; with several, use the first (scope: this issue is single-Director "pick repo ->
  // create -> open"; multi-Director selection is out of scope per the issue).
  async function nsPickDirector() {
    for (var i = 0; i < lastSessions.length; i++) {
      var did = lastSessions[i] && lastSessions[i].directorId;
      if (did) return did;
    }
    var r = await api("/directors");
    if (!r.ok) return null;
    var dirs = Array.isArray(r.data) ? r.data : [];
    if (!dirs.length) return null;
    return dirs[0].directorId || null;
  }

  async function openNewSession() {
    show("newsession");
    nsSelectedPath = null;
    $("ns-search").value = "";
    nsSetError("");
    nsSetStatus("Loading repos...");
    $("ns-repo-list").innerHTML = "";
    $("ns-target").textContent = "";

    nsDirectorId = await nsPickDirector();
    if (!nsDirectorId) {
      nsSetStatus("");
      nsSetError("No Director is connected. Pair this phone with a running CC Director first.");
      return;
    }
    $("ns-target").textContent = "On Director " + nsDirectorId.slice(0, 8);
    await loadRepos();
  }

  async function loadRepos() {
    var r = await api("/directors/" + nsDirectorId + "/repos");
    if (!r.ok) {
      nsSetStatus("");
      nsSetError("Could not load repos (" + r.status + ").");
      return;
    }
    nsRepos = Array.isArray(r.data) ? r.data : [];
    if (!nsRepos.length) {
      nsSetStatus("No repos registered on this Director yet. Add one from the desktop app first.");
      return;
    }
    nsSetStatus(nsRepos.length + " repo(s) - tap one to create a session.");
    renderRepos("");
  }

  function renderRepos(filter) {
    var ul = $("ns-repo-list");
    ul.innerHTML = "";
    var f = (filter || "").trim().toLowerCase();
    var shown = nsRepos.filter(function (repo) {
      if (!f) return true;
      return ((repo.name || "") + " " + (repo.path || "")).toLowerCase().indexOf(f) >= 0;
    });
    shown.forEach(function (repo) {
      var li = document.createElement("li");
      var main = document.createElement("div");
      main.className = "s-main";
      var name = document.createElement("div");
      name.className = "s-name";
      name.textContent = repo.name || repoBase(repo.path);
      var sub = document.createElement("div");
      sub.className = "s-sub";
      sub.textContent = repo.path || "";
      main.appendChild(name);
      main.appendChild(sub);
      li.appendChild(main);
      li.addEventListener("click", function () { createSession(repo); });
      ul.appendChild(li);
    });
  }

  function repoBase(p) {
    var s = (p || "").replace(/[\\/]+$/, "");
    return s.split(/[\\/]/).pop() || "repo";
  }

  async function createSession(repo) {
    if (nsCreating) return;
    var path = repo && repo.path;
    if (!path) { nsSetError("That repo has no path."); return; }
    nsCreating = true;
    nsSelectedPath = path;
    nsSetError("");
    nsSetStatus("Creating session in " + (repo.name || repoBase(path)) + "...");

    var r = await api("/directors/" + nsDirectorId + "/sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ repoPath: path, agent: "ClaudeCode" }),
    });
    nsCreating = false;

    if (!r.ok || !r.data || !r.data.sessionId) {
      nsSetStatus("");
      nsSetError("Could not create session (" + r.status + ").");
      return;
    }
    // The created SessionDto is the same shape the list/open flow uses - open it directly.
    lastSessions = [];
    openSession(r.data);
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
  $("new-session-btn").addEventListener("click", function () { openNewSession(); });
  $("ns-back-btn").addEventListener("click", function () { show("list"); loadSessions(); });
  $("ns-search").addEventListener("input", function () { renderRepos($("ns-search").value); });
  $("back-btn").addEventListener("click", function () { show("list"); loadSessions(); });
  $("speak-btn").addEventListener("click", function () { toggleSpeak(); });
  $("play-btn").addEventListener("click", playReply);

  show("list");
  loadSessions();
})();
