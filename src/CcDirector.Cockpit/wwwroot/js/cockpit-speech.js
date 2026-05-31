// Cockpit Speak: browser dictation that fills the composer. The user reviews the text and
// then chooses Send or Queue, so dictation never auto-sends. Uses the browser's built-in
// SpeechRecognition (Chromium/Brave). Results are pushed to the Blazor component which drops
// them into the textarea.
window.cockpitSpeech = (function () {
  let rec = null;
  let ref = null;

  function start(dotNetRef) {
    const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!SR) {
      if (dotNetRef) dotNetRef.invokeMethodAsync('OnDictationEnded');
      throw new Error('SpeechRecognition not supported in this browser');
    }
    ref = dotNetRef;
    rec = new SR();
    rec.continuous = true;
    rec.interimResults = false;
    rec.lang = 'en-US';
    rec.onresult = (e) => {
      let text = '';
      for (let i = e.resultIndex; i < e.results.length; i++) {
        if (e.results[i].isFinal) text += e.results[i][0].transcript;
      }
      text = text.trim();
      if (text && ref) ref.invokeMethodAsync('OnDictation', text);
    };
    rec.onend = () => { if (ref) ref.invokeMethodAsync('OnDictationEnded'); };
    rec.onerror = () => { /* onend fires next; nothing to do */ };
    rec.start();
  }

  function stop() {
    if (rec) { try { rec.stop(); } catch (e) {} rec = null; }
  }

  return { start, stop };
})();
