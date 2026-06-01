/*
 * AudioWorklet processor that converts the audio-thread's float32 stream to PCM16
 * (signed little-endian) and posts each render quantum back to the main thread.
 *
 * Vendored from the Director's /dictate-worklet.js so the Cockpit can load it
 * SAME-ORIGIN (an AudioWorklet module loaded cross-origin from the Director would
 * be blocked by CORS). The OpenAI Realtime transcription API expects PCM16 at
 * 24 kHz; the main thread creates the AudioContext with sampleRate: 24000.
 */
class Pcm16WriterProcessor extends AudioWorkletProcessor {
    process(inputs) {
        const input = inputs[0];
        if (!input || input.length === 0) return true;
        const channel = input[0];
        if (!channel || channel.length === 0) return true;

        const pcm16 = new Int16Array(channel.length);
        for (let i = 0; i < channel.length; i++) {
            const s = Math.max(-1, Math.min(1, channel[i]));
            pcm16[i] = s < 0 ? Math.round(s * 0x8000) : Math.round(s * 0x7FFF);
        }
        this.port.postMessage(pcm16.buffer, [pcm16.buffer]);
        return true;
    }
}

registerProcessor('pcm16-writer', Pcm16WriterProcessor);
