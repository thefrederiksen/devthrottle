/*
 * Voice Mode - Slice 1: the visible audio loop.
 *
 * Session list -> open a session -> Speak (record) -> resumable upload to the Gateway's
 * voice-turn endpoints -> poll -> play the spoken reply. All calls are same-origin against
 * the Gateway front door (the page is served behind it), so the cc-gateway-token cookie
 * authenticates automatically - the same pattern keys.html/settings.html use.
 *
 * Offline-first part 1 (issue #425): the moment a recording stops, the audio blob + turn
 * metadata are saved to IndexedDB (via VoiceDb in db.js) BEFORE any upload, and the page
 * shows "Recording saved locally". The upload then reads the blob back from IndexedDB and
 * each turn carries a status badge (Pending -> Uploading -> Uploaded, or Failed with a
 * manual Retry). A Pending/Failed turn survives a page reload because it lives in
 * IndexedDB.
 *
 * Offline-first part 2 (issue #426): a Service Worker scoped to /voice drains the outbox
 * AUTOMATICALLY when connectivity returns - no manual Retry tap. Background Sync (where
 * supported) wakes the page to drain even when the tab is backgrounded; on browsers without
 * Background Sync we fall back to an "online" event + a periodic connectivity probe and drain
 * in-page. On reconnect we also re-fetch any pending reply. A turn that has been waiting
 * longer than the 30-minute rule is badged "Stale" but keeps retrying (the threshold can be
 * shortened for proof via window.__VOICE_STALE_MS__). The chunked upload contract is already
 * resume-safe on the server.
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
    $("wingman-view").classList.toggle("hidden", view !== "wingman");
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

      // "Talk to Wingman" entry point on the row. stopPropagation so it does NOT also
      // open the voice session view (the row's own click).
      var wm = document.createElement("button");
      wm.className = "secondary s-wingman";
      wm.textContent = "Wingman";
      wm.title = "Talk to Wingman (read-only)";
      wm.addEventListener("click", function (ev) { ev.stopPropagation(); openWingman(s); });

      li.appendChild(dot);
      li.appendChild(main);
      li.appendChild(turns);
      li.appendChild(wm);
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
    loadOutbox(s.sessionId);
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
      saveLocallyThenUpload(recChunks, recMime).catch(function (e) {
        setStage("Could not save the recording: " + e.message, "error");
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

  // ===== offline-first outbox (issue #425) =====
  // The upload is no longer driven straight off the in-memory chunks. On stop we
  // assemble the chunks into one Blob, persist it to IndexedDB with status "pending"
  // (so the recording is safe the instant it stops), render an outbox row, THEN run the
  // upload reading the blob back from IndexedDB. A failed turn keeps its blob and offers
  // Retry; everything survives a page reload because it lives in IndexedDB.
  function mimeExtSafe(mime) { return (mime || "audio/webm").split(";")[0]; }

  // Chunk size for slicing the stored blob back into the ordered fragments the resumable
  // upload expects. 256 KB keeps each PUT small and resume-friendly.
  var UPLOAD_CHUNK_BYTES = 256 * 1024;

  // Save the just-stopped recording locally first, then start its upload. This is the
  // core promise: by the time we return from the write, the bytes are durable in
  // IndexedDB, so we tell the user "saved locally" before any network call.
  async function saveLocallyThenUpload(chunks, mime) {
    if (!current) return;
    if (!chunks.length) { setStage("Nothing recorded.", "error"); return; }
    var sid = current.sessionId;
    var localId = (crypto.randomUUID ? crypto.randomUUID() : fallbackGuid());
    var blob = new Blob(chunks, { type: mimeExtSafe(mime) });

    var record = {
      localId: localId,
      sessionId: sid,
      status: "pending",
      createdAt: new Date().toISOString(),
      mime: mimeExtSafe(mime),
      blob: blob,
      uploadId: null,
      turnId: null,
      error: null,
    };
    await VoiceDb.put(record); // durable BEFORE any upload

    setStage("Recording saved locally.", "");
    renderOutboxItem(record);

    // Fire the upload but do not let its rejection bubble up as a "could not save" error -
    // the save already succeeded; an upload failure is shown on the row's own badge.
    processOutboxItem(localId).catch(function () { /* surfaced on the row */ });
  }

  // Drive one stored turn through register -> chunk PUTs -> complete -> poll, updating its
  // persisted status (and the row badge) at each phase. Reads the blob from IndexedDB so
  // it works identically on first attempt and on a Retry after a reload. Reuses the stored
  // uploadId on retry (the server's 409-resume contract makes re-PUTs idempotent).
  async function processOutboxItem(localId) {
    var rec = await VoiceDb.get(localId);
    if (!rec) return; // already uploaded + removed
    if (rec.status === "uploaded") return;

    var sid = rec.sessionId;
    try {
      await setOutboxStatus(localId, "uploading");

      // 1) register the upload once (Idempotency-Key = our local id -> a retry never
      //    duplicates). Reuse a previously-registered uploadId on retry.
      var uploadId = rec.uploadId;
      if (!uploadId) {
        var reg = await api("/sessions/" + sid + "/voice-turn/upload", {
          method: "POST",
          headers: { "Idempotency-Key": localId },
        });
        if (!reg.ok || !reg.data || !reg.data.upload_id) {
          throw new Error("register " + reg.status);
        }
        uploadId = reg.data.upload_id;
        rec = await VoiceDb.update(localId, { uploadId: uploadId });
      }

      // 2) PUT each ordered fragment, sliced from the stored blob.
      var blob = rec.blob;
      var total = Math.max(1, Math.ceil(blob.size / UPLOAD_CHUNK_BYTES));
      for (var i = 0; i < total; i++) {
        var slice = blob.slice(i * UPLOAD_CHUNK_BYTES, (i + 1) * UPLOAD_CHUNK_BYTES);
        var buf = await slice.arrayBuffer();
        var put = await fetch(
          "/sessions/" + sid + "/voice-turn/upload/" + uploadId + "/chunk/" + i, {
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
        body: JSON.stringify({ totalChunks: total, mime: rec.mime }),
      });
      if (comp.status === 409) throw new Error("upload incomplete (server missing chunks)");
      if (comp.status !== 202 || !comp.data || !comp.data.turn_id) {
        throw new Error("complete " + comp.status + " " + (comp.data && comp.data.error || ""));
      }

      var turnId = comp.data.turn_id;
      await VoiceDb.update(localId, { turnId: turnId });
      await setOutboxStatus(localId, "uploaded");

      // The bytes are now durable on the server - drop the local copy and clear the row.
      await VoiceDb.remove(localId);
      removeOutboxItem(localId);

      await pollTurn(sid, turnId);
    } catch (e) {
      await VoiceDb.update(localId, { status: "failed", error: e.message })
        .catch(function () { /* record may be gone; nothing to persist */ });
      renderOutboxFromDb(localId);
      throw e;
    }
  }

  // Persist a status and reflect it on the row.
  async function setOutboxStatus(localId, status) {
    await VoiceDb.update(localId, { status: status, error: null });
    renderOutboxFromDb(localId);
  }

  // Re-read one record and re-render its row (or remove the row if the record is gone).
  async function renderOutboxFromDb(localId) {
    var rec = await VoiceDb.get(localId);
    if (!rec) { removeOutboxItem(localId); return; }
    renderOutboxItem(rec);
  }

  // ===== outbox rendering (issue #425) =====
  // The outbox lists the open session's locally-saved turns that are not yet durably on
  // the server (pending / uploading / failed). An "uploaded" row is shown briefly then
  // its record is removed; the durable reply appears in the history list above.
  var OUTBOX_STATUS = {
    pending:   { label: "Pending",   cls: "pending" },
    uploading: { label: "Uploading", cls: "uploading" },
    uploaded:  { label: "Uploaded",  cls: "uploaded" },
    failed:    { label: "Failed",    cls: "failed" },
    stale:     { label: "Stale",     cls: "stale" },
  };

  // The 30-minute rule (issue #426): a turn still not durably uploaded after this long is
  // shown as "Stale" - but it KEEPS retrying, so Stale is a display state layered over the
  // underlying pending/uploading/failed status, not a terminal one. The threshold can be
  // shortened for proof via window.__VOICE_STALE_MS__ (a test hook only - production uses
  // the 30-minute default).
  var STALE_MS_DEFAULT = 30 * 60 * 1000;
  function staleMs() {
    var override = (typeof window !== "undefined") ? Number(window.__VOICE_STALE_MS__) : NaN;
    return (isFinite(override) && override > 0) ? override : STALE_MS_DEFAULT;
  }

  // A record is stale when it is not yet uploaded and its age exceeds the threshold.
  function isStale(rec) {
    if (!rec || rec.status === "uploaded") return false;
    var created = Date.parse(rec.createdAt || "");
    if (isNaN(created)) return false;
    return (Date.now() - created) >= staleMs();
  }

  // Insert or update the row for one stored turn. Only rows for the currently-open
  // session are shown (a row for another session is ignored here; it is read back when
  // that session is opened).
  function renderOutboxItem(rec) {
    if (!current || rec.sessionId !== current.sessionId) return;
    var list = $("outbox-list");
    var existing = list.querySelector('[data-local-id="' + cssAttr(rec.localId) + '"]');
    var li = existing || document.createElement("li");
    li.setAttribute("data-local-id", rec.localId);
    li.innerHTML = "";

    var main = document.createElement("div");
    main.className = "ob-main";

    var top = document.createElement("div");
    top.className = "ob-top";
    // Stale wins the badge over the underlying status (it is still retrying); the row also
    // keeps a Stale marker so the user understands it has been waiting a long time.
    var stale = isStale(rec);
    var s = stale ? OUTBOX_STATUS.stale : (OUTBOX_STATUS[rec.status] || OUTBOX_STATUS.pending);
    var badge = document.createElement("span");
    badge.className = "ob-badge " + s.cls;
    badge.textContent = s.label;
    top.appendChild(badge);

    var saved = document.createElement("span");
    saved.className = "ob-saved";
    saved.textContent = "Saved locally";
    top.appendChild(saved);
    main.appendChild(top);

    var time = document.createElement("div");
    time.className = "ob-time";
    time.textContent = formatWhen(rec.createdAt);
    main.appendChild(time);

    if (rec.status === "failed" && rec.error) {
      var err = document.createElement("div");
      err.className = "ob-error";
      err.textContent = rec.error;
      main.appendChild(err);
    }

    li.appendChild(main);

    // Retry only on failure: re-runs the resumable upload from the stored blob.
    if (rec.status === "failed") {
      var retry = document.createElement("button");
      retry.className = "secondary ob-retry";
      retry.textContent = "Retry";
      retry.addEventListener("click", function () {
        retry.disabled = true;
        processOutboxItem(rec.localId).catch(function () { /* surfaced on the row */ });
      });
      li.appendChild(retry);
    }

    if (!existing) list.insertBefore(li, list.firstChild);
    updateOutboxEmpty();
  }

  function removeOutboxItem(localId) {
    var list = $("outbox-list");
    var li = list.querySelector('[data-local-id="' + cssAttr(localId) + '"]');
    if (li) li.remove();
    updateOutboxEmpty();
  }

  function updateOutboxEmpty() {
    var list = $("outbox-list");
    $("outbox-empty").classList.toggle("hidden", list.children.length > 0);
  }

  // On opening a session, read its locally-stored turns back from IndexedDB and render
  // them - this is what makes a pending/failed turn survive a page reload. Any turn that
  // is still "uploading" (e.g. the page was closed mid-upload) is treated as resumable:
  // it is shown and its upload is kicked again from the stored blob.
  async function loadOutbox(sid) {
    var list = $("outbox-list");
    list.innerHTML = "";
    updateOutboxEmpty();
    var rows = await VoiceDb.getAll();
    rows.filter(function (r) { return r.sessionId === sid; }).forEach(function (rec) {
      renderOutboxItem(rec);
      if (rec.status === "uploading") {
        processOutboxItem(rec.localId).catch(function () { /* surfaced on the row */ });
      }
    });
  }

  // ===== automatic drain on reconnect (issue #426) =====
  // The outbox is drained automatically - no manual Retry tap - whenever connectivity
  // returns. Three triggers cover the matrix of browser capabilities:
  //   1. Service Worker Background Sync ("sync" event) wakes the page even when backgrounded.
  //   2. The window "online" event drains immediately when the tab is foregrounded.
  //   3. A periodic probe is the floor for browsers without Background Sync (and catches the
  //      case where the connection comes back without an "online" event firing).
  // draining guards against overlapping drains; a re-entrant trigger just no-ops.
  var draining = false;

  // Drain every not-yet-uploaded turn in IndexedDB by running the same resumable upload the
  // manual Retry uses. Returns true when the outbox is empty afterwards (nothing left to
  // upload), false when at least one turn is still pending - the Service Worker uses this to
  // decide whether to ask the browser to retry the Background Sync later.
  async function drainOutbox() {
    if (draining) return false;
    draining = true;
    try {
      var rows = await VoiceDb.getAll();
      var pending = rows.filter(function (r) { return r.status !== "uploaded"; });
      for (var i = 0; i < pending.length; i++) {
        // Sequential, not parallel: each turn's upload is several round-trips and we do not
        // want to hammer a just-restored (possibly still-weak) connection.
        try {
          await processOutboxItem(pending[i].localId);
        } catch (e) {
          // Left "failed" on its row by processOutboxItem; keep draining the rest.
        }
      }
      var after = await VoiceDb.getAll();
      return after.filter(function (r) { return r.status !== "uploaded"; }).length === 0;
    } finally {
      draining = false;
    }
  }

  // Register the Service Worker for the /voice scope and wire the auto-drain triggers. The
  // worker is served at /voice/sw.js with Service-Worker-Allowed:/ so it can control /voice.
  // A browser without Service Worker / Background Sync still gets the online-event + periodic
  // drain below, so offline-first never depends on the worker being present.
  function initAutoDrain() {
    // Trigger 1: Service Worker + Background Sync.
    if ("serviceWorker" in navigator) {
      navigator.serviceWorker.register("/voice/sw.js", { scope: "/voice" })
        .then(function () { return navigator.serviceWorker.ready; })
        .then(function (reg) {
          // The worker asks the page to drain via a MessageChannel; reply with the outcome
          // so it can let the browser retry the sync when work remains.
          navigator.serviceWorker.addEventListener("message", onSwMessage);
          // Register the one-shot sync so a drain fires when connectivity returns even if the
          // tab is backgrounded. Harmless where SyncManager is absent.
          if (reg.sync) reg.sync.register("ccd-voice-drain").catch(function () { });
        })
        .catch(function () {
          // Registration can fail (insecure context, etc.). The online-event + periodic
          // probe below still drain in-page; nothing hidden.
        });
    }

    // Trigger 2: the window comes back online (tab foregrounded). Drain immediately and ask
    // the worker to (re)register a sync so a later background reconnect is covered too.
    window.addEventListener("online", function () {
      drainOutbox().catch(function () { });
      askWorkerToRegisterSync();
    });

    // Trigger 3: periodic probe - the floor for browsers without Background Sync, and a catch
    // for a connection that returns without an "online" event. Only acts when the browser
    // reports it is online and there is something to drain.
    setInterval(function () {
      if (navigator.onLine === false) return;
      VoiceDb.getAll().then(function (rows) {
        var hasPending = rows.some(function (r) { return r.status !== "uploaded"; });
        if (hasPending) drainOutbox().catch(function () { });
      }).catch(function () { });
    }, PROBE_INTERVAL_MS);
  }

  // The periodic-probe cadence. A test hook (window.__VOICE_PROBE_MS__) shortens it for proof
  // so a reconnect drain is observable quickly; production uses the 15-second default.
  var PROBE_INTERVAL_MS = (function () {
    var override = (typeof window !== "undefined") ? Number(window.__VOICE_PROBE_MS__) : NaN;
    return (isFinite(override) && override > 0) ? override : 15 * 1000;
  })();

  // Handle a drain request from the Service Worker: drain, then reply on the worker's port
  // with whether the outbox is now empty so it can let the browser retry the sync if not.
  function onSwMessage(event) {
    var data = event.data || {};
    if (data.type !== "drain-outbox") return;
    var port = event.ports && event.ports[0];
    drainOutbox().then(function (done) {
      if (port) port.postMessage({ done: done });
    }).catch(function () {
      if (port) port.postMessage({ done: false });
    });
  }

  // Ask the active worker to register a Background Sync (used after an online event so a
  // subsequent backgrounded reconnect still drains).
  function askWorkerToRegisterSync() {
    if ("serviceWorker" in navigator && navigator.serviceWorker.controller) {
      navigator.serviceWorker.controller.postMessage({ type: "register-sync" });
    }
  }

  // Re-badge stale rows without a status change: a row crosses the 30-minute threshold purely
  // by the passage of time, so re-render the visible outbox on a timer. Cheap (DOM only).
  function startStaleTicker() {
    setInterval(function () {
      if (!current) return;
      VoiceDb.getAll().then(function (rows) {
        rows.filter(function (r) { return r.sessionId === current.sessionId; })
          .forEach(function (rec) { renderOutboxItem(rec); });
      }).catch(function () { });
    }, STALE_TICK_MS);
  }

  // How often to re-evaluate staleness for the displayed rows. Shortened with the same probe
  // hook so the Stale badge appears promptly in proof; production re-checks every 30 seconds.
  var STALE_TICK_MS = Math.min(PROBE_INTERVAL_MS, 30 * 1000);

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
      nsSetError("No Director is connected. Pair this phone with a running DevThrottle first.");
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

  // ===== Wingman Chat (issue #424) =====
  // Strictly non-committal by default. Free-text questions go to wingman/ask and the
  // quick "read N lines" tool goes to /buffer - neither sends keystrokes to the session,
  // so an agent mid-turn is never advanced by asking. Only the three explicit actions
  // (send text / interrupt / escape) touch the session, and each one is gated by a
  // confirm() the user must accept.
  var wmSession = null; // the session the Wingman chat is bound to

  function openWingman(s) {
    wmSession = s;
    $("wm-title").textContent = sessionTitle(s);
    $("wm-chat").innerHTML = "";
    $("wm-question").value = "";
    $("wm-send-text").value = "";
    wmAppend("wingman", "Ask me anything about this session. I read its terminal, history "
      + "and repo, but I will not type anything into it.", "");
    show("wingman");
  }

  // Append a chat bubble. kind: "you" | "wingman" | "tool" | "error" | "pending".
  // Returns the element so a pending bubble can be replaced in place.
  function wmAppend(kind, text, meta) {
    var chat = $("wm-chat");
    var div = document.createElement("div");
    div.className = "wm-msg " + kind;
    var body = document.createElement("div");
    body.className = "wm-body";
    body.textContent = text;
    div.appendChild(body);
    if (meta) {
      var m = document.createElement("div");
      m.className = "wm-meta";
      m.textContent = meta;
      div.appendChild(m);
    }
    chat.appendChild(div);
    chat.scrollTop = chat.scrollHeight;
    return div;
  }

  // Ask the Wingman a free-text question. READ-ONLY: POST /sessions/{id}/wingman/ask
  // never sends keystrokes to the session.
  async function wmAsk() {
    if (!wmSession) return;
    var q = ($("wm-question").value || "").trim();
    if (!q) return;
    var btn = $("wm-ask-btn");
    btn.disabled = true;
    wmAppend("you", q, "");
    $("wm-question").value = "";
    var pending = wmAppend("pending", "Wingman is thinking...", "");

    var r = await api("/sessions/" + wmSession.sessionId + "/wingman/ask", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question: q }),
    });
    pending.remove();
    btn.disabled = false;

    if (!r.ok || !r.data) {
      wmAppend("error", "Wingman call failed (" + r.status + ").", "");
      return;
    }
    if (r.data.status && r.data.status !== "ok") {
      wmAppend("error", "Wingman: " + (r.data.error || r.data.status), "");
      return;
    }
    var answer = r.data.answer || "(no answer)";
    var meta = [];
    if (r.data.model) meta.push(r.data.model);
    if (r.data.contextDigest) meta.push(r.data.contextDigest);
    wmAppend("wingman", answer, meta.join("  -  "));
  }

  // Quick tool: read the last N lines of the session's terminal. READ-ONLY:
  // GET /sessions/{id}/buffer does not advance the session.
  async function wmReadLines(n) {
    if (!wmSession) return;
    var btn = $("wm-read20-btn");
    btn.disabled = true;
    var pending = wmAppend("pending", "Reading last " + n + " lines...", "");
    var r = await api("/sessions/" + wmSession.sessionId + "/buffer?lines=" + n);
    pending.remove();
    btn.disabled = false;
    if (!r.ok || !r.data) {
      wmAppend("error", "Could not read the terminal (" + r.status + ").", "");
      return;
    }
    var text = (r.data.text || "").replace(/\s+$/, "");
    wmAppend("tool", text || "(terminal is empty)", "last " + n + " lines of the terminal");
  }

  // Explicit action: send text to the session. Gated behind a confirm so nothing reaches
  // the session unless the user accepts. POST /sessions/{id}/prompt.
  async function wmSendText() {
    if (!wmSession) return;
    var text = ($("wm-send-text").value || "").trim();
    if (!text) { wmAppend("error", "Type the text to send first.", ""); return; }
    if (!confirm("Send this text to the session? It WILL reach the agent.\n\n" + text)) return;
    var btn = $("wm-send-btn");
    btn.disabled = true;
    var r = await api("/sessions/" + wmSession.sessionId + "/prompt", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text: text, appendEnter: true }),
    });
    btn.disabled = false;
    if (!r.ok || !r.data || r.data.accepted === false) {
      wmAppend("error", "Send failed (" + r.status + ")"
        + (r.data && r.data.error ? ": " + r.data.error : "") + ".", "");
      return;
    }
    $("wm-send-text").value = "";
    wmAppend("you", text, "sent to the session");
  }

  // Explicit action: interrupt (Ctrl+C) the session. Gated behind a confirm.
  // POST /sessions/{id}/interrupt.
  async function wmInterrupt() {
    if (!wmSession) return;
    if (!confirm("Stop (interrupt) the session? This sends Ctrl+C to the agent.")) return;
    var btn = $("wm-interrupt-btn");
    btn.disabled = true;
    var r = await api("/sessions/" + wmSession.sessionId + "/interrupt", { method: "POST" });
    btn.disabled = false;
    if (!r.ok) { wmAppend("error", "Interrupt failed (" + r.status + ").", ""); return; }
    wmAppend("wingman", "Interrupt (Ctrl+C) sent to the session.", "explicit action");
  }

  // Explicit action: send Escape to the session. Gated behind a confirm.
  // POST /sessions/{id}/escape.
  async function wmEscape() {
    if (!wmSession) return;
    if (!confirm("Send Escape to the session?")) return;
    var btn = $("wm-escape-btn");
    btn.disabled = true;
    var r = await api("/sessions/" + wmSession.sessionId + "/escape", { method: "POST" });
    btn.disabled = false;
    if (!r.ok) { wmAppend("error", "Escape failed (" + r.status + ").", ""); return; }
    wmAppend("wingman", "Escape sent to the session.", "explicit action");
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

  // Wingman Chat (issue #424)
  $("wingman-btn").addEventListener("click", function () { if (current) openWingman(current); });
  $("wm-back-btn").addEventListener("click", function () { show("list"); loadSessions(); });
  $("wm-ask-btn").addEventListener("click", function () { wmAsk(); });
  $("wm-read20-btn").addEventListener("click", function () { wmReadLines(20); });
  $("wm-send-btn").addEventListener("click", function () { wmSendText(); });
  $("wm-interrupt-btn").addEventListener("click", function () { wmInterrupt(); });
  $("wm-escape-btn").addEventListener("click", function () { wmEscape(); });

  // Auto-drain on reconnect (issue #426): register the Service Worker + wire the
  // online/Background-Sync/periodic-probe triggers, and start the ticker that re-badges a
  // turn as Stale once it crosses the 30-minute rule. Both are page-lifetime, set up once.
  initAutoDrain();
  startStaleTicker();

  show("list");
  loadSessions();
})();
