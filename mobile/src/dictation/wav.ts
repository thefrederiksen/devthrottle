// Transcode a captured audio Blob (Opus-in-WebM or whatever MediaRecorder produced) into a
// 16 kHz mono 16-bit PCM WAV Blob for the transcription upload (issue #817).
//
// Why WAV: the Gateway's batch transcription pipeline accepts a clip in every mode, but in the
// default Local (in-process Whisper.net) mode it feeds the bytes straight into a WAV reader - it
// does not decode Opus/WebM. Encoding WAV here makes dictation work in Local mode AND the remote
// (byo / devthrottle / OpenAI) modes from one client path, instead of only working when a remote
// key happens to be configured. The browser decodes its own recording with decodeAudioData and an
// OfflineAudioContext resamples + downmixes to 16 kHz mono.

const TARGET_SAMPLE_RATE = 16000;

/** The WAV plus the capture-health facts the decode already revealed (issue #863): the decoded
 *  audio duration (the actual amount of sound captured) and the source blob size. The caller
 *  compares decodedSeconds to the recording wall-clock to detect dropped audio. */
export interface TranscodeResult {
  wav: Blob;
  decodedSeconds: number;
  sourceBytes: number;
}

export async function blobToWav16kMono(blob: Blob): Promise<TranscodeResult> {
  const arrayBuffer = await blob.arrayBuffer();
  if (arrayBuffer.byteLength === 0) throw new Error("No audio was captured.");
  const sourceBytes = arrayBuffer.byteLength;

  const AudioCtor = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
  const decodeCtx = new AudioCtor();
  let decoded: AudioBuffer;
  try {
    decoded = await decodeCtx.decodeAudioData(arrayBuffer.slice(0));
  } finally {
    void decodeCtx.close();
  }

  // Resample to 16 kHz and downmix to mono. An OfflineAudioContext destination with 1 channel
  // mixes a stereo source down for us when the source is connected to it.
  const frameCount = Math.max(1, Math.ceil(decoded.duration * TARGET_SAMPLE_RATE));
  const offline = new OfflineAudioContext(1, frameCount, TARGET_SAMPLE_RATE);
  const source = offline.createBufferSource();
  source.buffer = decoded;
  source.connect(offline.destination);
  source.start(0);
  const rendered = await offline.startRendering();

  return {
    wav: encodeWav(rendered.getChannelData(0), TARGET_SAMPLE_RATE),
    decodedSeconds: decoded.duration,
    sourceBytes,
  };
}

// Build a canonical 16-bit PCM WAV (RIFF) blob from mono float samples in -1..1.
function encodeWav(samples: Float32Array, sampleRate: number): Blob {
  const bytesPerSample = 2;
  const dataLength = samples.length * bytesPerSample;
  const buffer = new ArrayBuffer(44 + dataLength);
  const view = new DataView(buffer);

  writeAscii(view, 0, "RIFF");
  view.setUint32(4, 36 + dataLength, true);
  writeAscii(view, 8, "WAVE");
  writeAscii(view, 12, "fmt ");
  view.setUint32(16, 16, true); // PCM fmt chunk size
  view.setUint16(20, 1, true); // PCM format
  view.setUint16(22, 1, true); // mono
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * bytesPerSample, true); // byte rate
  view.setUint16(32, bytesPerSample, true); // block align
  view.setUint16(34, 16, true); // bits per sample
  writeAscii(view, 36, "data");
  view.setUint32(40, dataLength, true);

  let offset = 44;
  for (let i = 0; i < samples.length; i++) {
    const clamped = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
    offset += bytesPerSample;
  }

  return new Blob([buffer], { type: "audio/wav" });
}

function writeAscii(view: DataView, offset: number, text: string): void {
  for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
}
