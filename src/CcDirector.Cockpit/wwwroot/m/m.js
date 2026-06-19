// Wingman Voice - standalone mobile screen (issue #531), v19 (issue #553: gesture-safe mobile
// autoplay, voice-before-menu, yellow-until-audio-ready). Plain static JS (not Blazor); recording
// works offline; a network-first service worker loads the page offline.
//
// Voice-first + proactive:
//   - Using voice on a session makes it a "voice session". The gateway then auto-runs the wingman
//     after every turn and keeps a ready spoken summary + audio. The session LIST shows a play
//     triangle on voice-ready sessions; tap it to OPEN the session and auto-play the ready voice
//     (issue #533 - no longer headless from the list); the voice is already made, so entering is
//     instant (no re-read). Tapping the row body opens the session without auto-playing.
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
  var holdBtn=$("hold-btn"), killBtn=$("kill-btn"), killArmTimer=null;

  var current=null, spoken="", busy=false, audioUrl=null, audioReady=false, rec=null, blocked={};
  var CHUNK=64*1024;
  var T={ list:15000, explain:90000, direct:90000, turn:180000, http:30000, transcribe:90000 };
  var COLORS={ red:"#F14C4C", yellow:"#F59E0B", orange:"#F97316", green:"#22C55E", blue:"#3B82F6", purple:"#A855F7" };
  function dotColor(c){ return COLORS[c]||"#6B7280"; }
  // The ONE effective color (mirrors gateway SessionOrdering.EffectiveColor): a session the
  // wingman is reading/summarizing shows yellow ("not ready yet"), a user-requested explain
  // deep dive shows orange, an on-hold session greys out - otherwise the raw status color.
  function effColor(s){
    if(!s)return "unknown";
    if(s.onHold)return "grey";
    if(s.briefingState==="Explaining")return "orange";
    if(s.briefingState==="Briefing"&&(s.statusColor||"").toLowerCase()==="red")return "yellow";
    // Voice-mode color rule (issue #553): a voice-mode session that is waiting for the user must
    // stay YELLOW ("preparing voice / not ready yet") while the wingman is generating OR before the
    // playable audio exists, and only turn RED once there is audio to play. It must never show
    // red/needs-you in voice mode before audio is ready. Non-voice color behavior is unchanged.
    var st=s.assessedState||s.activityState;
    var waiting=(st==="WaitingForInput"||st==="WaitingForPerm");
    if(s.voiceMode && waiting && (s.statusColor||"").toLowerCase()==="red" && (s.voiceGenerating || !s.voiceAudioReady))
      return "yellow";
    return s.statusColor;
  }
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
  function showList(){ stopListen(); clearMenu(); disarmKill(); current=null; sessionView.classList.add("hidden"); listView.classList.remove("hidden"); loadSessions(); }
  // quiet (issue #534): the 5 s auto-refresh ticker and the foreground refresh call loadSessions(true)
  // so it does NOT blank the already-rendered list to "Loading sessions..." while re-fetching, and a
  // transient failure leaves the last good list visible instead of replacing it with an error. The
  // manual Refresh button and first load call loadSessions() (quiet=false) for the usual feedback.
  async function loadSessions(quiet){
    if(!quiet){ listStatus.textContent="Loading sessions..."; sessionList.innerHTML=""; }
    try{
      var env=await fetchJson("/sessions?envelope=true",{},T.list);
      var ready={}; try{ var rr=await fetchJson("/wingman/voice/ready",{},T.list); (rr.sids||[]).forEach(function(x){ ready[x]=1; }); }catch(e){}
      var sessions=(env&&env.sessions)?env.sessions:[];
      sessions.sort(function(a,b){ return (b.lastActivityAt||"").localeCompare(a.lastActivityAt||""); });
      // Only touch the DOM once the fetch has SUCCEEDED, so a mid-refresh failure never wipes the list.
      sessionList.innerHTML="";
      if(!sessions.length){ listStatus.textContent="No sessions running. Start one, then tap Refresh."; return; }
      listStatus.textContent=sessions.length+" session"+(sessions.length===1?"":"s");
      sessions.forEach(function(s){
        var li=document.createElement("li"); li.className="scard";
        var extra=blocked[s.sessionId]?"  -  sending...":"";
        // Voice-ready rows show the play triangle INSTEAD of the chevron (keeps the row from
        // overflowing on a narrow phone, and the triangle is the obvious affordance).
        var tail=ready[s.sessionId]?'<button class="scard-play" type="button" aria-label="Play voice">&#9654;</button>':'<span class="scard-chev">&rsaquo;</span>';
        // In-voice-mode ear (issue #554): a vector ear (outer fold + inner canal) sits before the
        // name when a phone is driving this session by voice (DTO field voiceMode). Never a glyph.
        var ear=s.voiceMode?'<svg class="ear-icon" viewBox="0 0 24 24" fill="none" aria-label="voice mode" role="img"><path d="M7 11 C7 6.6 10.1 4 12.5 4 C15.5 4 18 6.4 18 9.5 C18 12.4 16 13.7 14.7 14.7 C13.7 15.5 13 16.1 13 17.2 C13 18.7 11.9 19.9 10.4 19.9 C9 19.9 7.9 18.8 7.9 17.4" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M10.2 10.4 C10.2 9.1 11.2 8.1 12.5 8.1 C13.8 8.1 14.8 9.1 14.8 10.4 C14.8 11.6 13.9 12.1 13.2 12.7" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>':'';
        li.innerHTML='<span class="dot" style="background:'+dotColor(effColor(s))+'"></span><span class="scard-main"><div class="scard-name">'+ear+'<span class="scard-name-text"></span></div><div class="scard-sub"></div></span>'+tail;
        li.querySelector(".scard-name-text").textContent=titleOf(s);
        li.querySelector(".scard-sub").textContent=(ready[s.sessionId]?"voice ready  -  ":"")+humanState(s.assessedState||s.activityState)+extra;
        // Tapping the row BODY opens the session WITHOUT auto-playing (drive-safe; unchanged).
        li.addEventListener("click", function(){ openSession(s, false); });
        var pb=li.querySelector(".scard-play");
        // Tapping the play triangle opens the session AND auto-plays its ready voice (issue #533 -
        // no longer headless from the list). Mobile autoplay fix (issue #553): iOS/Android only let
        // audioEl.play() run while the tap's user-gesture is still alive, so we START playback HERE -
        // synchronously inside the tap handler, on the session's voice-audio URL, BEFORE any await.
        // That "unlocks" the element so it begins as soon as the bytes arrive; openSession() then
        // syncs the buffered-ready UI state. stopPropagation keeps the row body's open-without-
        // autoplay path intact.
        if(pb) pb.addEventListener("click", function(ev){ ev.stopPropagation(); startGesturePlayback(s.sessionId); openSession(s, true); });
        sessionList.appendChild(li);
      });
    }catch(e){
      // On a quiet auto-refresh keep the last good list on screen (criterion: a transient failure must
      // not blank an already-rendered list); only the explicit Refresh / first load shows the error.
      if(!quiet) listStatus.textContent=e.message;
    }
  }

  // ===== one session =====
  // autoPlay (issue #533): when the list-row play triangle opened this session, auto-play the ready
  // voice as soon as it is buffered. A row-body tap passes false and stays idle (manual Play).
  function openSession(s, autoPlay){
    stopListen();
    current={ sid:s.sessionId, name:titleOf(s), dto:s, onHold:!!s.onHold };
    updateHoldBtn(); disarmKill();
    svName.textContent=current.name; svDot.style.background=dotColor(effColor(s));
    svState.textContent=humanState(s.assessedState||s.activityState); svMachine.textContent=s.machineName||"";
    hideError(); spoken=""; renderText("",null); audioUrl=null; audioReady=false;
    listView.classList.add("hidden"); sessionView.classList.remove("hidden");
    showTalkState();
    openVoice(!!autoPlay);
  }
  // Auto-refresh the visible session's status dot (#sv-dot) and state line (#sv-state) only
  // (issue #534). It deliberately updates NOTHING else: it never re-explains, never touches the
  // spoken summary, the typed text, the menu, or playback, and it skips entirely while the session
  // is mid-action so a tick can't stomp the local yellow/red dot the wingman flow sets or interrupt
  // the user. A transient failure is swallowed - the last good dot/state stays on screen.
  function sessionViewBusy(){
    // Any in-progress action where the local dot/state is authoritative, not the gateway's: the
    // wingman is working (busy), a recording is live (rec), a send is uploading (blocked), a menu is
    // shown, or audio is playing. In all of these the dot is set deliberately and must not be reset.
    return busy || !!rec || !!(current && blocked[current.sid]) || (current && current.menu)
      || !audioEl.paused;
  }
  async function refreshSessionMeta(){
    if(!current || sessionView.classList.contains("hidden")) return;
    if(sessionViewBusy()) return;            // don't disturb an in-progress action (criterion 5)
    var sid=current.sid;
    var env;
    try{ env=await fetchJson("/sessions?envelope=true",{},T.list); }
    catch(e){ return; }                      // transient failure: keep the last good dot/state
    if(!current || current.sid!==sid || sessionViewBusy()) return;   // re-check after the await
    var sessions=(env&&env.sessions)?env.sessions:[];
    var s=null;
    for(var i=0;i<sessions.length;i++){ if(sessions[i].sessionId===sid){ s=sessions[i]; break; } }
    if(!s) return;                           // session gone from the list: leave the view as-is
    svDot.style.background=dotColor(effColor(s));
    svState.textContent=humanState(s.assessedState||s.activityState);
    current.dto=s; current.onHold=!!s.onHold; updateHoldBtn();   // keep the Hold button in sync with reality
  }

  // ===== session lifecycle (issue #545): on-hold toggle (gray, in place) + kill (confirm -> list) =====
  function updateHoldBtn(){
    if(!holdBtn) return; var h=!!(current&&current.onHold);
    holdBtn.textContent = h ? "Take off hold" : "Put on hold";
    holdBtn.classList.toggle("held", h);
  }
  function disarmKill(){
    if(killArmTimer){ clearTimeout(killArmTimer); killArmTimer=null; }
    if(killBtn){ killBtn.classList.remove("armed"); killBtn.textContent="Kill session"; killBtn.disabled=false; }
    if(holdBtn) holdBtn.disabled=false;
  }
  async function toggleHold(){
    if(!current) return; var sid=current.sid, want=!current.onHold;
    holdBtn.disabled=true; disarmKill();
    try{
      await postJson("/sessions/"+encodeURIComponent(sid)+"/hold", {onHold:want}, T.http);
      if(current&&current.sid===sid){
        current.onHold=want; if(current.dto) current.dto.onHold=want;
        updateHoldBtn();
        svDot.style.background=dotColor(effColor(current.dto||{onHold:want}));
        heroStatus.textContent = want ? "On hold." : "Off hold.";
      }
    }catch(e){ heroStatus.textContent="Couldn't change hold: "+e.message; }
    holdBtn.disabled=false;
  }
  // Kill arms on the first tap (drive-safe: one extra tap, no typing) and kills on the second.
  function killTap(){
    if(!current) return;
    if(!killBtn.classList.contains("armed")){
      killBtn.classList.add("armed"); killBtn.textContent="Tap again to kill";
      killArmTimer=setTimeout(disarmKill, 4000);
      return;
    }
    disarmKill(); killSession();
  }
  async function killSession(){
    if(!current) return; var sid=current.sid;
    killBtn.disabled=true; heroStatus.textContent="Killing session...";
    try{
      await fetchJson("/sessions/"+encodeURIComponent(sid), {method:"DELETE"}, T.http);
      showList();                              // gone -> back to the list
    }catch(e){ heroStatus.textContent="Kill failed: "+e.message; killBtn.disabled=false; }
  }

  // ===== auto-refresh: 5 s poll + immediate on foreground (issue #534) =====
  // One ticker drives whichever screen is visible. It pauses while the page is hidden (no /sessions
  // requests in the background) and resumes - with an immediate refresh - the instant the app returns
  // to the foreground (phone wake / unlock / tab re-focus), within ~1 s, not on the next 5 s tick.
  var POLL_MS=5000, pollTimer=null;
  function refreshVisible(quiet){
    if(document.hidden) return;
    if(!listView.classList.contains("hidden")) loadSessions(quiet!==false);
    else if(!sessionView.classList.contains("hidden")) refreshSessionMeta();
  }
  function pollTick(){ refreshVisible(true); }
  function startPolling(){ if(pollTimer===null) pollTimer=setInterval(pollTick, POLL_MS); }
  function stopPolling(){ if(pollTimer!==null){ clearInterval(pollTimer); pollTimer=null; } }
  function onForeground(){
    // Foreground (visibilitychange visible / pageshow / focus): refresh now and (re)start the ticker.
    if(document.hidden) return;
    startPolling();
    refreshVisible(true);
  }
  function onBackground(){ if(document.hidden) stopPolling(); }
  // Entering a session NEVER auto-explains (and never while it is working). Show the cached voice
  // if one exists; otherwise just sit idle and wait for the person to tap Explain. When autoPlay is
  // set (the triangle entry path, issue #533) the cached voice starts playing once buffered.
  async function openVoice(autoPlay){
    if(!current) return;
    var sid=current.sid;
    showIdle();   // neutral: no summary, button says "Explain"
    clearMenu();
    // Issue #553: playback must NOT be gated behind menu detection. The /wingman/menu call can take
    // up to 90 s (a brain call when the terminal looks like a menu), and previously it ran FIRST,
    // blocking the cached-voice fetch entirely. Now both fetches run IN PARALLEL: the moment the
    // cached voice resolves AND it is ready, we show it (and, if autoPlay was requested from the
    // triangle, begin playback - gesture-safe per startGesturePlayback). The menu, when present,
    // still wins the on-screen surface, but only the menu render waits on the menu call.
    var voiceP=fetchJson("/sessions/"+encodeURIComponent(sid)+"/wingman/voice",{},T.http)
      .then(function(v){ return v; }, function(){ return null; });
    var menuP=fetchJson("/sessions/"+encodeURIComponent(sid)+"/wingman/menu",{},T.explain)
      .then(function(m){ return m; }, function(){ return null; });

    // Cached voice first / in parallel: surface it as soon as it lands so playback is not delayed by
    // the menu call. A later menu (below) overrides the on-screen surface if one is actually present.
    var v=await voiceP;
    if(current && current.sid===sid && v && v.ready){ heroReady(v.spoken, v.reply, !!autoPlay); }

    // A pending on-screen MENU takes priority - show the options to tap or say. The server gates this
    // behind a cheap look, so it only costs a brain call when the terminal actually shows a menu.
    var m=await menuP;
    if(current && current.sid===sid && m && m.isMenu){ stopListen(); showMenu(m); }
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
  // autoPlay (issue #533): the triangle entry path asks for the ready voice to start playing once it
  // is buffered. Every other caller (entry-without-triangle, explain, turns) omits it and stays idle.
  function heroReady(spokenText, reply, autoPlay){ busy=false; busyBar.classList.add("hidden"); setDot("red"); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; spoken=spokenText||""; renderText(spoken, reply); explainBtn.textContent = spoken ? "Explain again" : "Explain"; if(spoken) preparePlaybackUrl(voiceAudioUrl(current.sid), !!autoPlay); else { playBtn.classList.remove("loading","ready"); playBtn.disabled=true; heroStatus.textContent=""; } }
  function heroFailed(msg, retryFn){ busy=false; busyBar.classList.add("hidden"); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; playBtn.classList.remove("loading","ready"); playBtn.disabled=true; heroStatus.textContent=""; showError(msg, retryFn); }

  async function explain(){ if(!current)return; heroLoading("Reading the session..."); try{ var r=await postJson("/sessions/"+encodeURIComponent(current.sid)+"/wingman/explain",{},T.explain); heroReady(r.spoken,r.reply); }catch(e){ heroFailed(e.message, explain); } }
  // Collapse the bottom text panel so the top "working"/status row (#hero-status + #busy-bar) is in
  // view - the user just typed at the bottom inside the collapsed <details>, so the only confirmation
  // is off-screen otherwise (issue #532).
  function collapseTextSection(){ if(textSection) textSection.open=false; }
  // Send a turn to the AGENT (text or transcribed voice). Issue #535: the agent path is "fire it and
  // let it run" - the caller drops back to the LIST on send (so the user watches the session's dot),
  // so this MUST NOT rely on the session view being open to keep the turn alive or to surface a
  // failure. It blocks the session row (blocked[sid] -> the list shows "sending...") and retries the
  // delivery on a connection/server failure until it lands, so a navigated-away agent send is never
  // silently lost (no-silent-failure rule). On success it clears the block and refreshes whichever
  // screen is visible; if the user is still on this session it also shows the spoken summary / menu.
  async function runAgentTurn(sid,text){
    if(!sid||!text||!text.trim())return;
    function onThis(){ return current && current.sid===sid; }
    blocked[sid]=true;
    if(onThis()){ heroLoading("Working on it - the wingman will summarize when the agent finishes..."); showTalkState(); }
    var backoff=1000;
    while(true){
      try{
        var r=await postJson("/sessions/"+encodeURIComponent(sid)+"/wingman/voice-turn",{text:text.trim()},T.turn);
        delete blocked[sid];
        if(onThis()){ if(r.needsChoice && r.menu){ showMenu(r.menu, r.spoken); } else { clearMenu(); heroReady(r.spoken,r.reply); refreshMenu(); } }
        else { showTalkState(); refreshVisible(true); }   // back on the list: clear "sending..." + reflect the dot
        return;
      }catch(e){
        // Keep the row honest while we keep trying. On the session view show the failure with a Retry
        // (it can be tapped to fail fast); off-view the persistent "sending..." on the row plus the
        // automatic retry guarantee the message is not lost with no indication.
        if(onThis()){ heroFailed(e.message, function(){ runAgentTurn(sid,text); }); return; }
        showTalkState();
        await delay(backoff); backoff=Math.min(backoff*1.6,8000);
      }
    }
  }
  async function runWingmanDirect(text){ if(busy||!text||!text.trim()){ if(!text||!text.trim()) heroStatus.textContent="Type a question first."; return; } if(typedText.value===text) typedText.value=""; collapseTextSection(); heroLoading("Asking the wingman..."); try{ var r=await postJson("/wingman/ask-direct",{text:text.trim()},T.direct); busy=false; busyBar.classList.add("hidden"); spoken=r.spoken||""; renderText(spoken,null); explainBtn.disabled=false; askAgentBtn.disabled=false; askWingmanBtn.disabled=false; if(spoken) preparePlaybackText(spoken); }catch(e){ heroFailed(e.message, function(){ runWingmanDirect(text); }); } }

  // ===== play (ready only when the audio is fully buffered) =====
  // Gesture-safe playback (issue #553): on a phone, audioEl.play() only succeeds while the tap's
  // user-gesture is still live. preparePlaybackUrl plays only AFTER an awaited buffer, by which time
  // the gesture is gone and the play() promise silently rejects. So when the user TAPS (the list
  // triangle, or the in-session Play button on a not-yet-buffered session) we call this FIRST,
  // synchronously inside the handler: set the element's src to the session's voice-audio URL and
  // call play() right here. The element unlocks and starts as soon as bytes arrive; preparePlaybackUrl
  // (kicked off by openVoice/heroReady) then flips the button to "ready"/"speaking" once buffered.
  // A rejection (no audio yet, or the URL 404s) is swallowed - the normal "Audio not ready" UI shows.
  function startGesturePlayback(sid){
    if(!sid) return;
    var url=voiceAudioUrl(sid);
    try{ if(audioEl.src!==url) audioEl.src=url; }catch(e){}
    audioUrl=url; audioReady=false;
    playBtn.classList.add("speaking"); playBtn.classList.remove("ready");
    var p=audioEl.play();
    if(p&&p.catch) p.catch(function(){ /* not buffered yet / no audio - the buffered-ready path retakes the UI */ });
  }
  // waitPlayable resolves once the element can play <url>. Issue #553: if a gesture already pointed
  // the element at this exact url (startGesturePlayback) we must NOT reset src+load() again - that
  // would tear down the in-flight, gesture-authorized playback. So we only (re)assign src when it
  // differs, and if the element is already buffered enough we resolve immediately.
  function waitPlayable(url){ return new Promise(function(res,rej){ var done=false; function ok(){ if(done)return; done=true; cleanup(); res(); } function bad(){ if(done)return; done=true; cleanup(); rej(new Error("audio load failed")); } function cleanup(){ audioEl.removeEventListener("canplaythrough",ok); audioEl.removeEventListener("loadeddata",soft); audioEl.removeEventListener("error",bad); clearTimeout(t); } function soft(){ setTimeout(ok,1200); } audioEl.addEventListener("canplaythrough",ok); audioEl.addEventListener("loadeddata",soft); audioEl.addEventListener("error",bad); var t=setTimeout(ok,12000); if(audioEl.src!==url){ audioEl.src=url; audioEl.load(); } else if(audioEl.readyState>=3){ ok(); } }); }
  // autoPlay (issue #533): once the audio is buffered, start playing without a further tap (the
  // session must already be the one the user opened from the triangle). On a buffering failure the
  // existing "Audio not ready - tap the triangle to try again." state is shown, not hidden. Issue
  // #553: when the gesture already started playback on this url (audioEl is mid-play), we keep it
  // going - we only call play() if it is not already running, so the buffered-ready transition does
  // not restart audio that is already speaking.
  async function preparePlaybackUrl(url, autoPlay){
    var sidAtStart=current?current.sid:null;
    var gestureRunning=(audioEl.src===url && !audioEl.paused);
    if(gestureRunning){ playBtn.classList.add("speaking"); playBtn.classList.remove("ready","loading"); }
    else { playBtn.classList.add("loading"); playBtn.classList.remove("ready","speaking"); playBtn.disabled=true; heroStatus.textContent="Preparing audio..."; }
    try{ await waitPlayable(url); audioUrl=url; audioReady=true; playBtn.classList.remove("loading"); playBtn.disabled=false; if(!audioEl.paused){ playBtn.classList.add("speaking"); playBtn.classList.remove("ready"); heroStatus.textContent="Speaking..."; } else { playBtn.classList.add("ready"); playBtn.classList.remove("speaking"); heroStatus.textContent="Tap to listen."; if(autoPlay && current && current.sid===sidAtStart) play(); } }
    catch(e){ playBtn.classList.remove("loading","ready","speaking"); playBtn.disabled=false; heroStatus.textContent="Audio not ready - tap the triangle to try again."; }
  }
  // Direct-wingman answers are not stored server-side; synthesize them on the spot via /wingman/tts.
  async function preparePlaybackText(text){ playBtn.classList.add("loading"); playBtn.classList.remove("ready","speaking"); playBtn.disabled=true; heroStatus.textContent="Preparing audio..."; try{ var ctrl=new AbortController(); var timer=setTimeout(function(){ctrl.abort();},T.http); var resp; try{ resp=await fetch("/wingman/tts",{method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json"},body:JSON.stringify({text:text}),signal:ctrl.signal}); } finally{ clearTimeout(timer); } if(!resp.ok) throw new Error("tts"); var url=URL.createObjectURL(await resp.blob()); await waitPlayable(url); audioUrl=url; audioReady=true; playBtn.classList.remove("loading"); playBtn.classList.add("ready"); playBtn.disabled=false; heroStatus.textContent="Tap to listen."; }catch(e){ playBtn.classList.remove("loading","ready"); playBtn.disabled=false; heroStatus.textContent="Audio not ready - tap to try again."; } }
  // play() runs on the in-session Play button tap (and on autoPlay). Issue #553: when the audio is
  // not yet buffered we must STILL start it inside this tap's gesture or a phone will reject the
  // play() that would otherwise fire after the awaited buffer. So we call startGesturePlayback FIRST
  // (synchronous play() on the session's voice url), then kick preparePlaybackUrl to drive the
  // buffered-ready UI. For an already-buffered url we just play (or stop if already speaking).
  function play(){
    if(!audioReady||!audioUrl){ if(spoken&&current){ startGesturePlayback(current.sid); preparePlaybackUrl(voiceAudioUrl(current.sid)); } return; }
    if(!audioEl.paused){ stopListen(); return; }
    if(audioEl.src!==audioUrl){ try{ audioEl.src=audioUrl; }catch(e){} }
    playBtn.classList.add("speaking"); playBtn.classList.remove("ready"); audioEl.play().catch(function(){ endAudioUi(); });
  }
  function endAudioUi(){ playBtn.classList.remove("speaking"); if(audioReady) playBtn.classList.add("ready"); }
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
  async function onRecordingStopped(){
    var r=rec; rec=null; try{ r.stream.getTracks().forEach(function(t){ t.stop(); }); }catch(e){} showTalkState();
    if(r.action!=="send") return;
    var blob=new Blob(r.chunks,{type:r.mime}); if(!blob.size){ heroStatus.textContent="Did not catch any audio - tap Talk and try again."; return; }
    var entry={ id:uid(), sid:current.sid, name:current.name, mime:r.mime, blob:blob, ts:Date.now(), mode:r.mode||"agent" };
    try{ await outboxPut(entry); }catch(e){}
    sendEntry(entry);
    // Issue #535: a voice recording TO THE AGENT returns to the list promptly once Send is tapped -
    // the upload/transcribe/send continues in the background via the durable outbox (sendEntry blocks
    // the row -> the list shows "sending..."). A recording TO THE WINGMAN stays on the session view to
    // hear the spoken answer.
    if(entry.mode==="agent") showList();
  }

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
  // original text stays captured in `t` so a Retry re-sends it (issue #532).
  // Issue #535 - post-send DESTINATION differs by target:
  //   Send to agent  -> fire the turn, then return to the LIST promptly (on send, not after the turn
  //                     completes); runAgentTurn keeps the delivery alive + shows "sending..." on the
  //                     row, so leaving immediately is safe and a failure is never silently lost.
  //   Ask the wingman -> STAY on the session view to hear the spoken answer (runWingmanDirect).
  askAgentBtn.addEventListener("click", function(){ var t=typedText.value; if(!t||!t.trim()){ heroStatus.textContent="Type or record something first."; return; } var sid=current&&current.sid; if(!sid) return; typedText.value=""; runAgentTurn(sid, t); showList(); });
  askWingmanBtn.addEventListener("click", function(){ var t=typedText.value; if(busy||!t||!t.trim()){ if(!t||!t.trim()) heroStatus.textContent="Type a question first."; return; } typedText.value=""; runWingmanDirect(t); });
  talkBtn.addEventListener("click", function(){ startRecording("agent"); });
  talkWingmanBtn.addEventListener("click", function(){ startRecording("wingman"); });
  cancelBtn.addEventListener("click", function(){ finishRecording("cancel"); });
  sendBtn.addEventListener("click", function(){ finishRecording("send"); });
  holdBtn.addEventListener("click", toggleHold);
  killBtn.addEventListener("click", killTap);

  // Auto-refresh wiring (issue #534): pause polling when the page is hidden, refresh immediately and
  // resume when it returns to the foreground. visibilitychange covers tab switch / phone lock; pageshow
  // covers the bfcache wake on some mobile browsers; focus covers desktop tab re-focus.
  document.addEventListener("visibilitychange", function(){ if(document.hidden) onBackground(); else onForeground(); });
  window.addEventListener("pageshow", onForeground);
  window.addEventListener("focus", onForeground);

  showList();
  resumeOutbox();
  startPolling();
})();
