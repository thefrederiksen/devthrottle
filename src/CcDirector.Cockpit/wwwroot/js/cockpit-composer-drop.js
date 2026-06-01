// Composer drag-drop: lets the user drop an image straight onto the message composer
// (desktop parity with dropping onto the prompt box). Blazor's drop event can't read file
// bytes, so we route the dropped image files into the composer's hidden <InputFile>
// (.composer-drop-input) and dispatch a change event - that runs the exact same server-side
// OnDropImages upload+inject path the Screenshots panel uses. Text drops are left to the
// textarea's native behaviour.
//
// Wired ONCE at the document level (event delegation) so it survives every composer
// re-render (session switches) without re-binding. init() is idempotent.

export function init() {
  if (window.__cockpitComposerDropInit) return;
  window.__cockpitComposerDropInit = true;

  const composerOf = (t) => (t && t.closest) ? t.closest('.composer') : null;
  const hasFiles = (dt) => dt && Array.from(dt.types || []).includes('Files');

  document.addEventListener('dragover', (e) => {
    const c = composerOf(e.target);
    if (!c || !hasFiles(e.dataTransfer)) return;
    e.preventDefault();                       // allow the drop
    try { e.dataTransfer.dropEffect = 'copy'; } catch (_) {}
    c.classList.add('drag-over');
  });

  document.addEventListener('dragleave', (e) => {
    const c = composerOf(e.target);
    if (c && e.target === c) c.classList.remove('drag-over');
  });

  document.addEventListener('drop', (e) => {
    const c = composerOf(e.target);
    if (!c) return;
    c.classList.remove('drag-over');
    const files = e.dataTransfer && e.dataTransfer.files;
    if (!files || !files.length) return;      // no files (e.g. dragged text) -> native behaviour
    const imgs = Array.from(files).filter((f) => f.type && f.type.startsWith('image/'));
    if (!imgs.length) return;                 // non-image files -> leave the textarea alone
    e.preventDefault();

    const input = c.querySelector('.composer-drop-input');
    if (!input) return;
    // Hand the dropped images to the hidden InputFile and fire change so Blazor picks them up.
    const dt = new DataTransfer();
    imgs.forEach((f) => dt.items.add(f));
    input.files = dt.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
  });
}
