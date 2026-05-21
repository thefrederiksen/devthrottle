/*
 * AudioWorklet processor that converts the audio-thread's float32 stream
 * to PCM16 (signed little-endian) and posts each render quantum (128
 * samples) back to the main thread.
 *
 * Used by /dictate.html when mode=streaming. The OpenAI Realtime
 * transcription API expects PCM16 at 24 kHz; the main thread is expected
 * to create the AudioContext with sampleRate: 24000 so the worklet
 * receives audio at that rate. Browsers may not honor the requested
 * sample rate on every platform, so the AudioContext.sampleRate should
 * be checked and the audio resampled by the browser as needed before it
 * reaches this processor.
 */
class Pcm16WriterProcessor extends AudioWorkletProcessor {
    process(inputs) {
        const input = inputs[0];
        if (!input || input.length === 0) return true;
        const channel = input[0];
        if (!channel || channel.length === 0) return true;

        const pcm16 = new Int16Array(channel.length);
        for (let i = 0; i < channel.length; i++) {
            // Clamp to [-1, 1] then scale to int16 range.
            const s = Math.max(-1, Math.min(1, channel[i]));
            pcm16[i] = s < 0 ? Math.round(s * 0x8000) : Math.round(s * 0x7FFF);
        }
        // Transfer the underlying buffer to avoid a copy.
        this.port.postMessage(pcm16.buffer, [pcm16.buffer]);
        return true;
    }
}

registerProcessor('pcm16-writer', Pcm16WriterProcessor);
