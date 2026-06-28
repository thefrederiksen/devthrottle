// Raw key byte sequences sent to a session's PTY via POST /sessions/{sid}/prompt with
// AppendEnter=false (so the bytes are written verbatim, no submit newline appended). These match
// the sequences the desktop Terminal and the MAUI phone client send: a bare carriage return for
// Enter and the standard VT100 cursor-key escape sequences for the arrows (ESC "[" + letter).
//
// ESC is the ASCII Escape control byte (decimal 27 / 0x1B). It is built from its code point with
// String.fromCharCode so the source file stays pure printable ASCII (no embedded control byte and
// no fragile escape literal). Kept as a pure module so the mapping is one reviewable source.
const ESC = String.fromCharCode(27);

export const KEY_ENTER = "\r"; // carriage return (a bare Enter at the prompt)
export const KEY_ARROW_UP = `${ESC}[A`;
export const KEY_ARROW_DOWN = `${ESC}[B`;
export const KEY_ARROW_RIGHT = `${ESC}[C`;
export const KEY_ARROW_LEFT = `${ESC}[D`;
