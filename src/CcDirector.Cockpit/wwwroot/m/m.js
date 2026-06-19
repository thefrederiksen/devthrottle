// Wingman Voice - standalone mobile screen (issue #531), v8. Plain static JS (not Blazor); recording
// works offline; a network-first service worker loads the page offline.
//
// Voice-first + proactive:
//   - Using voice on a session makes it a "voice session". The gateway then auto-runs the wingman
//     after every turn and keeps a ready spoken summary + audio. The session LIST shows a play
//     triangle on voice-ready sessions; tap it to hear it WITHOUT entering, and entering is instant
//     (the voice is already made - no re-read).
//   - Big Play + Talk buttons stay put (drive-safe); text is collapsed at the bottom.
//   - Tap Talk to record (interrupts playback, works offline); it records until Send/Cancel. On Send
//     the session is blocked and the recording uploads in pieces, retrying forever on its own.
(function () {
  "use strict";
  if ("serviceWorker" in navigator) { try { navigator.serviceWorker.register("/m/sw.js"); } catch (e) {} }

  var $ = function (id) { return document.getElementById(id); };
  var listView=$("list-view"), sessionView=$("session-view"), listStatus=$("list-status"), sessionList=$("session-list");
  var svName=$("sv-name"), svDot=$("sv-dot"), svState=$("sv-state"), svMachine=$("sv-machine");
  var playBtn=$("play-btn"), heroStatus=$("hero-status"), heroSummary=$("hero-summary"), fullWrap=$("full-wrap"), fullText=$("full-text"), explainBtn=$("explain-btn");
  var talkBtn=$("talk-btn"), typedText=$("typed-text"), askAgentBtn=$("ask-agent-btn"), askWingmanBtn=$("ask-wingman-btn");
  var talkWingmanBtn=$("talk-wingman-btn"), recControls=$("rec-controls"), recTime=$("rec-time"), recTarget=$("rec-target"), cancelBtn=$("cancel-btn"), sendBtn=$("send-btn");
  var sendingStatus=$("sending-status"), sendingText=$("sending-text"), busyBar=$("busy-bar"), busyText=$("busy-text"), errorBox=$("error-box"), audioEl=$("tts-audio");
  var menuBox=$("menu-box"), textSection=$("text-section");

  var current=null, spoken="", busy=false, audioUrl=null, audioReady=false, rec=null, blocked={};
  var listPlayBtn=null;   // the list-row triangle currently playing
  var CHUNK=64*1024;
  var T={ list:15000, explain:90000, direct:90000, turn:180000, http:30000, transcribe:90000 };
  var COLORS={ red:"#F14C4C", yellow:"#F59E0B", orange:"#F97316", green:"#22C55E", blue:"#3B82F6", purple:"#A855F7" };
  function dotColor(c){ return COLORS[c]||"#6B7280"; }
  // The ONE effective color (mirrors gateway SessionOrdering.EffectiveColor): a session the
  // wingman is reading/summarizing shows yellow ("not ready yet"), a user-requested explain
  // deep dive shows orange, an on-hold session greys out - otherwise the raw status color.
  function effColor(s){ if(!s)return "unknown"; if(s.onHold)return "grey"; if(s.briefingState==="Explaining")return "orange"; if(s.briefingState==="Briefing"&&(s.statusColor||"").toLowerCase()==="red")return "yellow"; return s.statusColor; }
  function repoBase(p){ if(!p)return "(no repo)"; var n=p.replace(/\\/g,"/").replace(/\/+$/,""); var i=n.lastIndexOf("/"); return i>=0?n.slice(i+1):n; }
  function titleOf(s){ return (s.name&&s.name.trim())?s.name.trim():repoBase(s.repoPath); }
  function humanState(st){ if(st==="WaitingForInput")return "waiting for input"; if(st==="WaitingForPerm")return "waiting for permission"; return st||"-"; }
  function uid(){ return Date.now().toString(36)+"-"+Math.random().toString(36).slice(2,9); }
  function delay(ms){ return new Promise(function(r){ setTimeout(r,ms); }); }
  function voiceAudioUrl(sid){ return "/sessions/"+encodeURIComponent(sid)+"/wingman/voice/audio"; }

  // ===== IndexedDB outbox (a recording is never lost) =====
  var _db=null;
  function db(){ if(_db)return Promise.resolve(_db); return new Promise(function(res,rej){ var r=indexedDB.open("wingman-voice",1); r.onupgradeneeded=function(){ r.result.createObjectStore("outbox",{keyPath:"id"}); }; r.onsuccess=function(){ _db=r.result; res(_db); }; r.onerror=function(){ rej(r.error); }; }); }
  function ostore(m){ return db().then(function(d){ return d.transaction("outbox",m).objectStore("outbox"); }); }
  async function outboxPut(e){ var s=await ostore("readwrite"); return new Promise(function(res,rej){ var r=s.put(e); r.onsuccess=function(){res();}; r.onerror=function(){rej(r.error);}; }); }
  async function outboxDel(id){ var s=await ostore("readwrite"); return new Promise(function(res){ var r=s.delete(id); r.onsuccess=function(){res();}; r.onerror=function(){res();}; }); }
  async function outboxAll(){ var s=await ostore("readonly"); return new Promise(function(res){ var r=s.getAll(); r.onsuccess=function(){res(r.result||[]);}; r.onerror=function(){res([]);}; }); }

  async function fetchJson(url,opts,timeoutMs){ var ctrl=new AbortController(); var timer=setTimeout(function(){ctrl.abort();},timeoutMs||30000); var resp; try{ resp=await fetch(url,Object.assign({credentials:"same-origin",signal:ctrl.signal},opts)); }catch(e){ clearTimeout(timer); throw new Error(e&&e.name==="AbortError"?"Timed out.":"No connection to the gateway."); } clearTimeout(timer); var data=null; try{ data=await resp.json(); }catch(e){} if(!resp.ok) throw new Error((data&&data.error)?data.error:("Gateway error "+resp.status)); return data||{}; }
  function postJson(url,p,t){ return fetchJson(url,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(p||{})},t); }

  // ===== session list (with voice-ready play triangles) =====
  function showList(){ stopListen(); clearMenu(); current=null; sessionView.classList.add("hidden"); listView.classList.remove("hidden"); loadSessions(); }
  async function loadSessions(){
    listStatus.textContent="Loading sessions..."; sessionList.innerHTML=""; listPlayBtn=null;
    try{
      var env=await fetchJson("/sessions?envelope=true",{},T.list);
      var ready={}; try{ var rr=await fetchJson("/wingman/voice/ready",{},T.list); (rr.sids||[]).forEach(function(x){ ready[x]=1; }); }catch(e){}
      var sessions=(env&&env.sessions)?env.sessions:[];
      sessions.sort(function(a,b){ return (b.lastActivityAt||"").localeCompare(a.lastActivityAt||""); });
      if(!sessions.length){ listStatus.textContent="No sessions running. Start one, then tap Refresh."; return; }
      listStatus.textContent=sessions.length+" session"+(sessions.length===1?"":"s");
      sessions.forEach(function(s){
        var li=document.createElement("li"); li.className="scard";
        var extra=blocked[s.sessionId]?"  -  sending...":"";
        // Voice-ready rows show the play triangle INSTEAD of the chevron (keeps the row from
        // overflowing on a narrow phone, and the triangle is the obvious affordance).
        var tail=ready[s.sessionId]?'<button class="scard-play" type="button" aria-label="Play voice">&#9654;</button>':'<span class="scard-chev">&rsaquo;</span>';
        li.innerHTML='<span class="dot" style="background:'+dotColor(effColor(s))+'"></span><span class="scard-main"><div class="scard-name"></div><div class="scard-sub"></div></span>'+tail;
        li.querySelector(".scard-name").textContent=titleOf(s);
        li.querySelector(".scard-sub").textContent=(ready[s.sessionId]?"voice ready  -  ":"")+humanState(s.assessedState||s.activityState)+extra;
        li.addEventListener("click", function(){ openSession(s); });
        var pb=li.querySelector(".scard-play");
        if(pb) pb.addEventListener("click", function(ev){ ev.stopPropagation(); playListAudio(s.sessionId, pb); });
        sessionList.appendChild(li);
      });
    }catch(e){ listStatus.textContent=e.message; }
  }
  // Play a session's ready voice straight from the list, without entering it.
  function playListAudio(sid, btn){
    var url=voiceAudioUrl(sid);
    if(listPlayBtn && listPlayBtn!==btn){ try{audioEl.pause();}catch(e){} listPlayBtn.classList.remove("playing"); }
    if(!audioEl.paused && audioEl.src.indexOf(url)>=0){ audioEl.pause(); btn.classList.remove("playing"); listPlayBtn=null; return; }
    audioEl.src=url; listPlayBtn=btn; btn.classList.add("playing");
    audioEl.play().catch(function(){ btn.classList.remove("playing"); listPlayBtn=null; });
  }

  // ===== one session =====
  function openSession(s){
    stopListen();
    current={ sid:s.sessionId, name:titleOf(s) };
    svName.textContent=current.name; svDot.style.background=dotColor(effColor(s));
    svState.textContent=humanState(s.assessedState||s.activityState); svMachine.textContent=s.machineName||"";
    hideError(); spoken=""; renderText("",null); audioUrl=null; audioReady=false;
    listView.classList.add("hidden"); sessionView.classList.remove("hidden");
    showTalkState();
    openVoice();
  }
  // Entering a session NEVER auto-explains (and never while it is working). Show the cached voice
  // if one exists; otherwise just sit idle and wait for the person to tap Explain.
  async function openVoice(){
    if(!current) return;
    showIdle();   // neutral: no summary, button says "Explain"
    clearMenu();
    // A pending on-screen MENU takes priority - show the options to tap or say. The server gates this
    // behind a cheap look, so it only costs a brain call when the terminal actually shows a menu.
    try{
      var m=await fetchJson("/sessions/"+encodeURIComponent(current.sid)+"/wingman/menu",{},T.explain);
      if(m && m.isMenu && current){ showMenu(m); return; }
    }catch(e){ /* no menu / unreachable -> fall through to cached voice */ }
    try{
      var v=await fetchJson("/sessions/"+encodeURIComponent(current.sid)+"/wingman/voice",{},T.http);
      if(v && v.ready){ heroReady(v.spoken, v.reply); }   // cached -> show it (button becomes "Explain again")
    }catch(e){ /* leave idle; do not auto-explain */ }
  }

  // ===== on-screen menu: read the options, take a spoken/tapped choice, press it =====
  function clearMenu(){ if(current) current.menu=null; menuBox.classList.add("hidden"); menuBox.innerHTML=""; }
  function renderMenu(menu){
    if(current) current.menu=menu;
    menuBox.innerHTML="";
    (menu.options||[]).forEach(function(o,i){
      var b=document.createElement("button"); b.type="button";
      b.className="menu-opt"+(o.recommended?" recommended":"");
      var key=document.createElement("span"); key.className="mo-key"; key.textContent=o.key||("Option "+(i+1)); b.appendChild(key);
      if(o.recommended){ var rec=document.createElement("span"); rec.className="mo-rec"; rec.textContent="recommended"; b.appendChild(rec); }
      if(o.note){ var nt=document.createElement("span"); nt.className="mo-note"; nt.textContent=o.note; b.appendChild(nt); }
      b.addEventListener("click", function(){ pressMenuOption(o, menu); });
      menuBox.appendChild(b);
    });
    menuBox.classList.remove("hidden");
  }
  // Show the menu AND make its spoken reading playable (drive-safe: we do not auto-play; tap to hear).
  function showMenu(menu, spokenOverride){
    renderMenu(menu);
    busy=false; busyBar.classList.add("hidden"); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false;
    spoken=spokenOverride||menu.spoken||""; renderText(spoken, null);
    explainBtn.textContent="Explain";
    heroStatus.textContent="Say your pick, or tap one. Tap the triangle to hear the choices.";
    if(spoken) preparePlaybackText(spoken); else { playBtn.classList.remove("loading","ready"); playBtn.disabled=true; }
  }
  // After a turn, surface a (possibly new) menu without disturbing the spoken summary already shown.
  async function refreshMenu(){
    if(!current) return; var sid=current.sid;
    try{
      var m=await fetchJson("/sessions/"+encodeURIComponent(sid)+"/wingman/menu",{},T.explain);
      if(current&&current.sid===sid){ if(m&&m.isMenu){ renderMenu(m); } else { clearMenu(); } }
    }catch(e){}
  }
  async function pressMenuOption(o, menu){
    if(!current||busy) return; var sid=current.sid; stopListen();
    heroLoading("Selecting "+((o.key||"option").replace(/^\W*(?:\d{1,2}|[A-Za-z])[.)]\s*/,""))+"...");
    var submit=(menu.selectionMode==="multiple")?(menu.submit||""):"";
    try{
      var r=await postJson("/sessions/"+encodeURIComponent(sid)+"/wingman/menu-press",{send:o.send,submit:submit},T.turn);
      if(current&&current.sid===sid){ clearMenu(); heroReady(r.spoken,r.reply); refreshMenu(); }
    }catch(e){ if(current&&current.sid===sid) heroFailed(e.message, function(){ pressMenuOption(o, menu); }); }
  }
  // Idle state on entry when there is nothing cached: no summary, play disabled, button "Explain".
  function showIdle(){
    busy=false; explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; busyBar.classList.add("hidden");
    spoken=""; renderText("", null); audioUrl=null; audioReady=false;
    playBtn.classList.remove("loading","ready","speaking"); playBtn.disabled=true;
    heroStatus.textContent="Tap Explain for a summary, or talk to the agent.";
    explainBtn.textContent="Explain";
  }

  function renderText(summaryText, reply){ heroSummary.textContent=summaryText||""; if(reply&&reply.trim()){ fullText.textContent=reply; fullWrap.classList.remove("hidden"); } else fullWrap.classList.add("hidden"); }
  function showTalkState(){ var sending=!!(current&&blocked[current.sid]), recording=!!rec; var hideTalk=recording||sending; talkBtn.classList.toggle("hidden", hideTalk); talkWingmanBtn.classList.toggle("hidden", hideTalk); recControls.classList.toggle("hidden", !recording); sendingStatus.classList.toggle("hidden", !(sending&&!recording)); }
  function lockUi(on){ busy=on; explainBtn.disabled=on; askAgentBtn.disabled=on; askWingmanBtn.disabled=on; }
  // While the wingman runs, the session is "not ready yet" -> show the header dot yellow; when it
  // lands (a summary the user can act on) it is waiting for the user -> red. The list re-render
  // reconciles to the gateway's authoritative color on the next refresh (issue #531 voice mode).
  function setDot(color){ if(svDot) svDot.style.background=dotColor(color); }
  function heroLoading(msg){ lockUi(true); setDot("yellow"); playBtn.disabled=true; playBtn.classList.add("loading"); playBtn.classList.remove("ready","speaking"); audioReady=false; var m=msg||"Working..."; heroStatus.textContent=m; busyText.textContent=m; busyBar.classList.remove("hidden"); hideError(); }
  function heroReady(spokenText, reply){ busy=false; busyBar.classList.add("hidden"); setDot("red"); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; spoken=spokenText||""; renderText(spoken, reply); explainBtn.textContent = spoken ? "Explain again" : "Explain"; if(spoken) preparePlaybackUrl(voiceAudioUrl(current.sid)); else { playBtn.classList.remove("loading","ready"); playBtn.disabled=true; heroStatus.textContent=""; } }
  function heroFailed(msg, retryFn){ busy=false; busyBar.classList.add("hidden"); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; playBtn.classList.remove("loading","ready"); playBtn.disabled=true; heroStatus.textContent=""; showError(msg, retryFn); }

  async function explain(){ if(!current)return; heroLoading("Reading the session..."); try{ var r=await postJson("/sessions/"+encodeURIComponent(current.sid)+"/wingman/explain",{},T.explain); heroReady(r.spoken,r.reply); }catch(e){ heroFailed(e.message, explain); } }
  // Collapse the bottom text panel so the top "working"/status row (#hero-status + #busy-bar) is in
  // view - the user just typed at the bottom inside the collapsed <details>, so the only confirmation
  // is off-screen otherwise (issue #532).
  function collapseTextSection(){ if(textSection) textSection.open=false; }
  async function runAgentTurn(sid,text){ if(!sid||!text||!text.trim())return; if(current&&current.sid===sid) heroLoading("Working on it - the wingman will summarize when the agent finishes..."); try{ var r=await postJson("/sessions/"+encodeURIComponent(sid)+"/wingman/voice-turn",{text:text.trim()},T.turn); if(current&&current.sid===sid){ if(r.needsChoice && r.menu){ showMenu(r.menu, r.spoken); } else { clearMenu(); heroReady(r.spoken,r.reply); refreshMenu(); } } }catch(e){ if(current&&current.sid===sid) heroFailed(e.message, function(){ runAgentTurn(sid,text); }); } }
  async function runWingmanDirect(text){ if(busy||!text||!text.trim()){ if(!text||!text.trim()) heroStatus.textContent="Type a question first."; return; } if(typedText.value===text) typedText.value=""; collapseTextSection(); heroLoading("Asking the wingman..."); try{ var r=await postJson("/wingman/ask-direct",{text:text.trim()},T.direct); busy=false; busyBar.classList.add("hidden"); spoken=r.spoken||""; renderText(spoken,null); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; if(spoken) preparePlaybackText(spoken); }catch(e){ heroFailed(e.message, function(){ runWingmanDirect(text); }); } }

  // ===== play (ready only when the audio is fully buffered) =====
  function waitPlayable(url){ return new Promise(function(res,rej){ var done=false; function ok(){ if(done)return; done=true; cleanup(); res(); } function bad(){ if(done)return; done=true; cleanup(); rej(new Error("audio load failed")); } function cleanup(){ audioEl.removeEventListener("canplaythrough",ok); audioEl.removeEventListener("loadeddata",soft); audioEl.removeEventListener("error",bad); clearTimeout(t); } function soft(){ setTimeout(ok,1200); } audioEl.addEventListener("canplaythrough",ok); audioEl.addEventListener("loadeddata",soft); audioEl.addEventListener("error",bad); var t=setTimeout(ok,12000); audioEl.src=url; audioEl.load(); }); }
  async function preparePlaybackUrl(url){
    playBtn.classList.add("loading"); playBtn.classList.remove("ready","speaking"); playBtn.disabled=true; heroStatus.textContent="Preparing audio...";
    try{ await waitPlayable(url); audioUrl=url; audioReady=true; playBtn.classList.remove("loading"); playBtn.classList.add("ready"); playBtn.disabled=false; heroStatus.textContent="Tap to listen."; }
    catch(e){ playBtn.classList.remove("loading","ready"); playBtn.disabled=false; heroStatus.textContent="Audio not ready - tap the triangle to try again."; }
  }
  // Direct-wingman answers are not stored server-side; synthesize them on the spot via /wingman/tts.
  async function preparePlaybackText(text){ playBtn.classList.add("loading"); playBtn.classList.remove("ready","speaking"); playBtn.disabled=true; heroStatus.textContent="Preparing audio..."; try{ var ctrl=new AbortController(); var timer=setTimeout(function(){ctrl.abort();},T.http); var resp; try{ resp=await fetch("/wingman/tts",{method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json"},body:JSON.stringify({text:text}),signal:ctrl.signal}); } finally{ clearTimeout(timer); } if(!resp.ok) throw new Error("tts"); var url=URL.createObjectURL(await resp.blob()); await waitPlayable(url); audioUrl=url; audioReady=true; playBtn.classList.remove("loading"); playBtn.classList.add("ready"); playBtn.disabled=false; heroStatus.textContent="Tap to listen."; }catch(e){ playBtn.classList.remove("loading","ready"); playBtn.disabled=false; heroStatus.textContent="Audio not ready - tap to try again."; } }
  function play(){ if(!audioReady||!audioUrl){ if(spoken&&current) preparePlaybackUrl(voiceAudioUrl(current.sid)); return; } if(!audioEl.paused){ stopListen(); return; } if(audioEl.src!==audioUrl){ try{ audioEl.src=audioUrl; }catch(e){} } playBtn.classList.add("speaking"); playBtn.classList.remove("ready"); audioEl.play().catch(function(){ endAudioUi(); }); }
  function endAudioUi(){ playBtn.classList.remove("speaking"); if(audioReady) playBtn.classList.add("ready"); if(listPlayBtn){ listPlayBtn.classList.remove("playing"); listPlayBtn=null; } }
  function stopListen(){ try{ audioEl.pause(); }catch(e){} endAudioUi(); }

  // ===== record (instant, interrupts playback, runs until Send/Cancel) =====
  function pickMime(){ var c=["audio/webm;codecs=opus","audio/webm","audio/mp4","audio/ogg"]; for(var i=0;i<c.length;i++){ if(window.MediaRecorder&&MediaRecorder.isTypeSupported(c[i])) return c[i]; } return ""; }
  function extOf(m){ if(/webm/i.test(m))return "webm"; if(/mp4|m4a/i.test(m))return "m4a"; if(/ogg/i.test(m))return "ogg"; return "webm"; }
  function fmtTime(ms){ var s=Math.floor(ms/1000); return Math.floor(s/60)+":"+("0"+(s%60)).slice(-2); }
  async function startRecording(mode){
    if(!current || blocked[current.sid] || rec) return; stopListen();
    if(!navigator.mediaDevices||!window.MediaRecorder){ heroStatus.textContent="Recording is not supported here - use the text box below."; return; }
    var stream; try{ stream=await navigator.mediaDevices.getUserMedia({audio:true}); }catch(e){ heroStatus.textContent="Allow microphone access to talk (or use the text box below)."; return; }
    var mime=pickMime(); var recorder=mime?new MediaRecorder(stream,{mimeType:mime}):new MediaRecorder(stream);
    rec={ recorder:recorder, stream:stream, chunks:[], mime:recorder.mimeType||mime||"audio/webm", start:Date.now(), timer:null, action:null, mode:(mode==="wingman"?"wingman":"agent") };
    recorder.ondataavailable=function(e){ if(e.data&&e.data.size) rec.chunks.push(e.data); }; recorder.onstop=onRecordingStopped;
    recorder.start(1000); rec.timer=setInterval(function(){ recTime.textContent=fmtTime(Date.now()-rec.start); },500); recTime.textContent="0:00";
    if(recTarget) recTarget.textContent=(rec.mode==="wingman"?"Recording for the wingman":"Recording for the agent");
    showTalkState();
  }
  function finishRecording(action){ if(!rec)return; rec.action=action; if(rec.timer) clearInterval(rec.timer); try{ if(rec.recorder.state!=="inactive") rec.recorder.stop(); else onRecordingStopped(); }catch(e){ onRecordingStopped(); } }
  async function onRecordingStopped(){ var r=rec; rec=null; try{ r.stream.getTracks().forEach(function(t){ t.stop(); }); }catch(e){} showTalkState(); if(r.action!=="send") return; var blob=new Blob(r.chunks,{type:r.mime}); if(!blob.size){ heroStatus.textContent="Did not catch any audio - tap Talk and try again."; return; } var entry={ id:uid(), sid:current.sid, name:current.name, mime:r.mime, blob:blob, ts:Date.now(), mode:r.mode||"agent" }; try{ await outboxPut(entry); }catch(e){} sendEntry(entry); }

  // ===== transcribe the recording, then route it to the agent (or the wingman) =====
  // A network failure (offline) is tagged .net -> keep the recording and wait forever. An HTTP
  // error from a reachable server is NOT a connection problem -> show it, do not loop silently.
  function netErr(){ var e=new Error("offline"); e.net=true; return e; }
  async function transcribeBlob(blob, mime){
    var fd=new FormData(); fd.append("audio", blob, "audio."+extOf(mime));
    var ctrl=new AbortController(); var timer=setTimeout(function(){ctrl.abort();}, T.transcribe);
    var resp;
    try{ resp=await fetch("/wingman/transcribe",{ method:"POST", credentials:"same-origin", body:fd, signal:ctrl.signal }); }
    catch(e){ clearTimeout(timer); throw netErr(); }
    clearTimeout(timer);
    if(!resp.ok){ var j=null; try{ j=await resp.json(); }catch(e){} throw new Error((j&&j.error)||("transcription error "+resp.status)); }
    var d=await resp.json(); return (d&&d.transcript)||"";
  }
  async function sendEntry(entry){
    blocked[entry.sid]=true;
    function onThis(){ return current && current.sid===entry.sid; }
    function done(){ delete blocked[entry.sid]; if(onThis()){ showTalkState(); } }
    if(onThis()){ showTalkState(); sendingText.textContent="Transcribing what you said..."; }
    var backoff=1000, serverFails=0;
    while(true){
      try{
        var transcript=await transcribeBlob(entry.blob, entry.mime);
        await outboxDel(entry.id); done();
        if(!transcript || !transcript.trim()){ if(onThis()) heroStatus.textContent="Didn't catch any words - tap Talk and try again."; return; }
        // Route what you said: to the agent (default) or straight to the wingman.
        if(entry.mode==="wingman"){ if(onThis()) runWingmanDirect(transcript); }
        else { runAgentTurn(entry.sid, transcript); }
        return;
      }catch(e){
        // Never discard a recording you just made: keep it saved in the outbox and keep trying,
        // whether the problem is the connection or a server hiccup. (Truly stale recordings are
        // dropped only by the 30-minute purge on load.) So once you hit Send, it is saved and sent
        // no matter how you navigate or reload.
        serverFails++;
        if(onThis()) sendingText.textContent = (e && e.net)
          ? "Waiting for a connection... your message is saved and will send automatically."
          : "Still sending... your message is saved and will keep trying.";
        await delay(backoff); backoff=Math.min(backoff*1.6,8000);
      }
    }
  }
  async function resumeOutbox(){
    try{
      var all=await outboxAll(); var now=Date.now();
      for(var i=0;i<all.length;i++){
        var e=all[i];
        // Drop recordings older than 30 minutes rather than surprise-sending a stale one.
        if(!e.ts || (now-e.ts) > 30*60*1000){ await outboxDel(e.id); continue; }
        // Wingman-mode recordings only make sense in their session view; skip stale background ones.
        if(e.mode==="wingman" && !(current && current.sid===e.sid)){ await outboxDel(e.id); continue; }
        if(!blocked[e.sid]) sendEntry(e);
      }
    }catch(e){}
  }

  function showError(msg,retryFn){ errorBox.innerHTML='<span class="err-msg"></span>'; errorBox.querySelector(".err-msg").textContent=msg; if(retryFn){ var b=document.createElement("button"); b.className="retry"; b.textContent="Retry"; b.addEventListener("click",function(){ hideError(); retryFn(); }); errorBox.appendChild(b); } errorBox.classList.remove("hidden"); }
  function hideError(){ errorBox.classList.add("hidden"); errorBox.innerHTML=""; }

  // ===== wiring =====
  audioEl.addEventListener("ended", endAudioUi);
  $("refresh-btn").addEventListener("click", loadSessions);
  $("back-btn").addEventListener("click", showList);
  playBtn.addEventListener("click", function(){ if(playBtn.classList.contains("speaking")) stopListen(); else play(); });
  explainBtn.addEventListener("click", explain);
  // Both text sends clear the box immediately on tap (the sent text is no longer relevant); the
  // original text stays captured in `t` so a Retry re-sends it (issue #532). The agent path's
  // post-send destination is owned by #535; here it only clears on tap.
  askAgentBtn.addEventListener("click", function(){ var t=typedText.value; if(!t||!t.trim()){ heroStatus.textContent="Type or record something first."; return; } typedText.value=""; runAgentTurn(current&&current.sid, t); });
  askWingmanBtn.addEventListener("click", function(){ var t=typedText.value; if(busy||!t||!t.trim()){ if(!t||!t.trim()) heroStatus.textContent="Type a question first."; return; } typedText.value=""; runWingmanDirect(t); });
  talkBtn.addEventListener("click", function(){ startRecording("agent"); });
  talkWingmanBtn.addEventListener("click", function(){ startRecording("wingman"); });
  cancelBtn.addEventListener("click", function(){ finishRecording("cancel"); });
  sendBtn.addEventListener("click", function(){ finishRecording("send"); });

  showList();
  resumeOutbox();
})();
