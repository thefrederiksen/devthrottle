// ONE READ AT A TIME (the Factory Inbox end-to-end proof of 6 November 2026, defect 1, and the review of pull request
// 4703). The Factory inbox list and a factory's thread are read again while they are open. The old loop aborted the read
// in flight every 15 seconds and started another; an aborted read keeps running on the Gateway, so a slow read never
// showed an answer and the aborted ones piled up there. This loop:
//
// - never starts a read while one is running, and never aborts a running read to make room for another;
// - starts the next read `everyMs` after the last one ENDS, whether it answered or failed;
// - when a read is asked for while one runs (the composer left a message), starts it as soon as that one ends, so the
//   screen still shows the latest answer;
// - aborts the read in flight only when stopped (the screen closed or moved to another factory).
//
// A read that never settles would hold the loop for ever, so every read given to this loop must carry its own time
// limit (FACTORY_INBOX_READ_TIMEOUT_MS on the transport): a hung read then ends with the transport's timeout error, the
// caller's error path runs, and the loop goes on.

export interface OneReadAtATime {
  /** Read now: at once when no read runs, or as soon as the running one ends. */
  readNow: () => void;
  /** Abort the read in flight and read no more. */
  stop: () => void;
}

/**
 * Start the loop with a first read at once. `read` is given the loop's abort signal and whether this is a background
 * read (every read after the first). It handles its own answer and its own error; the loop only needs it to settle.
 */
export function startOneReadAtATime(
  read: (signal: AbortSignal, background: boolean) => Promise<void>,
  everyMs: number,
): OneReadAtATime {
  const controller = new AbortController();
  let running = false;
  let wanted = false;
  let reads = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const readNow = () => {
    if (controller.signal.aborted) return;
    clearTimeout(timer);
    timer = undefined;
    if (running) {
      wanted = true;
      return;
    }
    running = true;
    wanted = false;
    const background = reads > 0;
    reads += 1;
    const ended = () => {
      if (controller.signal.aborted) return;
      running = false;
      if (wanted) readNow();
      else timer = setTimeout(readNow, everyMs);
    };
    read(controller.signal, background).then(ended, ended);
  };

  readNow();
  return {
    readNow,
    stop: () => {
      controller.abort();
      clearTimeout(timer);
      timer = undefined;
    },
  };
}
