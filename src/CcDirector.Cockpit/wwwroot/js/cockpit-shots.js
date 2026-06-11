// Screenshot gallery helpers for the Cockpit. The image bytes are fetched SAME-ORIGIN from the
// Gateway's per-session proxy (GET /sessions/{sid}/screenshots/file, issue #317), which forwards
// to the owning Director - so these run entirely in the browser (no round-trip through the
// Blazor circuit for the pixels) and the auth cookie rides along automatically.

// Copy an image (by URL) onto the system clipboard as an actual image, not a path/URL string.
// The Clipboard API only reliably writes image/png, so non-PNG sources are converted via a
// canvas first. Returns true on success, false if the clipboard write was blocked/unsupported.
export async function copyImage(url) {
    try {
        const resp = await fetch(url, { cache: "no-cache" });
        if (!resp.ok) return false;
        let blob = await resp.blob();

        if (blob.type !== "image/png") {
            blob = await toPng(blob);
            if (!blob) return false;
        }

        if (!navigator.clipboard || typeof window.ClipboardItem === "undefined") return false;
        await navigator.clipboard.write([new window.ClipboardItem({ "image/png": blob })]);
        return true;
    } catch (e) {
        console.warn("[cockpit-shots] copyImage failed", e);
        return false;
    }
}

// Insert text into a textarea at the current caret (or replacing the current selection), then
// fire an 'input' event so Blazor's @bind picks up the new value. Used to drop a screenshot's
// path into the composer - the web equivalent of the desktop "drag the image onto the prompt".
// The textarea retains selectionStart even when it has lost focus, so a click on a gallery card
// still inserts where the caret last was.
export function insertAtCursor(el, text) {
    if (!el) return false;
    const start = el.selectionStart ?? el.value.length;
    const end = el.selectionEnd ?? el.value.length;
    el.value = el.value.slice(0, start) + text + el.value.slice(end);
    const pos = start + text.length;
    // Tell Blazor first (this is what updates the bound C# field), then restore the caret and
    // focus - Blazor leaves the DOM value untouched when it already matches, so the caret holds.
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.selectionStart = el.selectionEnd = pos;
    el.focus();
    return true;
}

// Decode any image blob and re-encode it as PNG via an offscreen canvas. The source URL carries
// CORS headers, so the canvas is not tainted and toBlob succeeds.
async function toPng(blob) {
    try {
        const bitmap = await createImageBitmap(blob);
        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        const ctx = canvas.getContext("2d");
        ctx.drawImage(bitmap, 0, 0);
        return await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
    } catch (e) {
        console.warn("[cockpit-shots] toPng failed", e);
        return null;
    }
}
