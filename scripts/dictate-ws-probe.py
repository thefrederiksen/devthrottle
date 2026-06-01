"""
One-shot probe of the Director's /dictate WebSocket pipeline, mimicking exactly
what the Cockpit's cockpit-speech.js does: handshake, stream 24 kHz mono PCM16
frames, send stop, collect the final cleaned transcript.

Feeds a real speech WAV (docs/features/voice-mode/audio/utterance_what_changed.wav)
as the audio source so we verify the full server pipeline (OpenAI realtime +
dictionary + verbatim cleanup) end to end. NOT a unit test; a live smoke probe.

Usage: python scripts/dictate-ws-probe.py ws://localhost:7884/dictate path/to.wav
"""
import asyncio
import json
import sys
import wave

import numpy as np
import websockets


async def main(ws_url: str, wav_path: str) -> int:
    # Load WAV, downsample 48k -> 24k mono PCM16 (decimate by 2) to match the
    # AudioWorklet's 24 kHz output.
    w = wave.open(wav_path, "rb")
    rate = w.getframerate()
    ch = w.getnchannels()
    raw = w.readframes(w.getnframes())
    w.close()
    samples = np.frombuffer(raw, dtype="<i2")
    if ch == 2:
        samples = samples.reshape(-1, 2).mean(axis=1).astype("<i2")
    if rate == 48000:
        samples = samples[::2]  # 48k -> 24k
    elif rate != 24000:
        print(f"WARN unexpected rate {rate}; sending as-is")
    pcm = samples.astype("<i2").tobytes()
    print(f"[probe] loaded {wav_path}: {len(pcm)} bytes PCM16 @24k ({len(pcm)/48000:.1f}s)")

    final = {}
    partials = []
    async with websockets.connect(ws_url, max_size=None) as ws:
        # Wait for ready, then start.
        async def recv_loop():
            async for msg in ws:
                if isinstance(msg, bytes):
                    continue
                m = json.loads(msg)
                t = m.get("type")
                if t == "ready":
                    await ws.send(json.dumps({"type": "start", "profile": "default"}))
                    print("[probe] -> start")
                elif t == "started":
                    print("[probe] <- started; streaming audio")
                    # Stream in 4800-byte frames (~50ms) with light pacing so the
                    # realtime API sees a natural cadence.
                    frame = 4800
                    for i in range(0, len(pcm), frame):
                        await ws.send(pcm[i:i + frame])
                        await asyncio.sleep(0.02)
                    await ws.send(json.dumps({"type": "stop"}))
                    print("[probe] -> stop")
                elif t == "partial":
                    partials.append(m.get("text", ""))
                elif t == "transcribing":
                    print("[probe] <- transcribing")
                elif t == "final":
                    final.update(m)
                    print("[probe] <- final")
                    break
                elif t == "error":
                    print(f"[probe] <- ERROR: {m.get('message')}")
                    break
        await asyncio.wait_for(recv_loop(), timeout=60)

    print("\n===== RESULT =====")
    if partials:
        print("last partial:", partials[-1])
    print("raw    :", final.get("raw"))
    print("cleaned:", final.get("cleaned"))
    print("cleanupApplied:", final.get("cleanupApplied"))
    return 0 if final.get("cleaned") or final.get("raw") else 2


if __name__ == "__main__":
    url = sys.argv[1] if len(sys.argv) > 1 else "ws://localhost:7884/dictate"
    wav = sys.argv[2] if len(sys.argv) > 2 else "docs/features/voice-mode/audio/utterance_what_changed.wav"
    sys.exit(asyncio.run(main(url, wav)))
