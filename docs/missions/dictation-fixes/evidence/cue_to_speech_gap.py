"""Phase 4, ruling H1: how long after the ready cue ends does the owner start speaking?

The recorder's cue boundary is B0 + bytes(T1 - T0) + 50 ms ring-down + 50 ms first-buffer margin.
Everything before the boundary is written as zeros. That is only safe if real speech starts later
than 100 ms after the cue's end. This measures that gap on the local corpus.

Inputs (local only, never committed - audio and transcripts stay on the machine):
  D:\ReposFred\devthrottle_internal\tools\transcription-lab\corpus\clips\*.wav

Cue: the exact water-drop bloop DesktopAudioCue builds (0.20 s), found by normalised
cross-correlation in the first 1.5 s, the same search as the research's cue_scan.py. A clip counts
as "cue found" when its head score is >= 0.5; on this corpus the same search over a window from the
middle of each clip (where no cue can be) reaches 0.5 on only 1 of 44 clips.

Speech start, two independent instruments:
  1. Silero VAD (the lab's detector, vad_pass.py) with speech_pad_ms=0, so a start is not moved
     earlier by padding. Resolution is one 32 ms window.
  2. Energy: 10 ms frames from the cue's end; first frame whose RMS is 15 dB above the quietest
     10 ms frames of the clip AND within 25 dB of the clip's loudest frame, and which is SUSTAINED:
     at least 8 of the 10 frames from it (100 ms) stay within 6 dB of that threshold. The sustain
     test stops a last bump of the cue's own decay counting as a rise. Coarse; 10 ms resolution.

Gap = speech start - cue end, in ms. A negative gap means the instrument placed speech inside
the cue (the cue itself, or words over it).

Prints and writes cue_to_speech_gap.json: per-clip numbers (clip file name, scores, times - no
text) and the distribution: count, minimum, 5th percentile, median.
"""
import os, glob, json, wave
import numpy as np, torch
from silero_vad import load_silero_vad, get_speech_timestamps

LAB = r"D:\ReposFred\devthrottle_internal\tools\transcription-lab\corpus"
HERE = os.path.dirname(os.path.abspath(__file__))
CUE_S = 0.20
FOUND = 0.5

def bloop(sr):
    dur, f0, f1, decay, amp, fade = CUE_S, 380.0, 1150.0, 26.0, 0.55, 0.006
    n = int(sr * dur); t = np.arange(n) / sr; prog = np.arange(n) / n
    phase = np.cumsum(2 * np.pi * (f0 + (f1 - f0) * prog) / sr)
    return np.sin(phase) * np.exp(-decay * t) * np.minimum(1.0, t / fade) * np.minimum(1.0, (dur - t) / fade) * amp

def read(path):
    w = wave.open(path, "rb"); sr, ch, n = w.getframerate(), w.getnchannels(), w.getnframes()
    x = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.float64) / 32768.0; w.close()
    if ch == 2: x = x.reshape(-1, 2).mean(axis=1)
    return sr, x

def best_match(x, tpl):
    m = len(tpl)
    tpl = (tpl - tpl.mean()) / (np.linalg.norm(tpl - tpl.mean()) + 1e-12)
    c = np.correlate(x, tpl, mode="valid")
    e = np.sqrt(np.convolve(x * x, np.ones(m), mode="valid")) + 1e-9
    ncc = np.abs(c) / e; i = int(np.argmax(ncc))
    return float(ncc[i]), i

def to16k(x, sr):
    if sr == 16000: return x.astype(np.float32)
    idx = np.linspace(0, len(x) - 1, int(len(x) * 16000 / sr))
    return np.interp(idx, np.arange(len(x)), x).astype(np.float32)

def vad_start_after(x, sr, model, cue_end):
    ts = get_speech_timestamps(torch.from_numpy(to16k(x, sr)), model, sampling_rate=16000,
                               speech_pad_ms=0, return_seconds=False)
    for t in ts:
        s, e = t["start"] / 16000.0, t["end"] / 16000.0
        if e > cue_end:          # first speech span still going after the cue ends
            return s
    return None

def energy_start_after(x, sr, cue_end):
    f = int(0.010 * sr); nfr = len(x) // f
    rms = np.sqrt((x[: nfr * f].reshape(nfr, f) ** 2).mean(axis=1)) + 1e-9
    db = 20 * np.log10(rms)
    floor = np.percentile(db, 5); top = db.max()
    thr = max(floor + 15.0, top - 25.0)
    for k in range(int(np.ceil(cue_end / 0.010)), nfr - 10):
        if db[k] >= thr and int((db[k:k + 10] >= thr - 6.0).sum()) >= 8:
            return k * 0.010
    return None

def dist(v):
    a = np.array(sorted(v), dtype=float)
    if len(a) == 0: return {"count": 0}
    return {"count": int(len(a)), "min_ms": round(float(a[0]), 1),
            "p5_ms": round(float(np.percentile(a, 5)), 1), "median_ms": round(float(np.median(a)), 1)}

def main():
    torch.set_num_threads(1)
    model = load_silero_vad()
    rows = []
    for path in sorted(glob.glob(os.path.join(LAB, "clips", "*.wav"))):
        sr, x = read(path)
        score, i = best_match(x[: int(1.5 * sr)], bloop(sr))
        name = os.path.basename(path)
        if score < FOUND:
            rows.append({"clip": name, "cue_score": round(score, 2), "cue_found": False}); continue
        cue_end = (i + int(CUE_S * sr)) / sr
        v = vad_start_after(x, sr, model, cue_end)
        en = energy_start_after(x, sr, cue_end)
        rows.append({"clip": name, "cue_score": round(score, 2), "cue_found": True,
                     "cue_end_s": round(cue_end, 3),
                     "vad_speech_start_s": None if v is None else round(v, 3),
                     "vad_gap_ms": None if v is None else round((v - cue_end) * 1000, 1),
                     "energy_speech_start_s": None if en is None else round(en, 3),
                     "energy_gap_ms": None if en is None else round((en - cue_end) * 1000, 1)})
    found = [r for r in rows if r["cue_found"]]
    vad = [r["vad_gap_ms"] for r in found if r["vad_gap_ms"] is not None]
    en = [r["energy_gap_ms"] for r in found if r["energy_gap_ms"] is not None]
    summary = {"clips": len(rows), "cue_found": len(found),
               "vad": dist(vad), "vad_no_speech_after_cue": sum(1 for r in found if r["vad_gap_ms"] is None),
               "energy": dist(en), "energy_no_rise_after_cue": sum(1 for r in found if r["energy_gap_ms"] is None),
               "rule": "keep the 50 ms margin only if the 5th percentile gap is >= 150 ms"}
    for r in found:
        print(f"{r['clip']:45} score={r['cue_score']:.2f} cue_end={r['cue_end_s']:.3f}s vad_gap={r['vad_gap_ms']} energy_gap={r['energy_gap_ms']}")
    print(json.dumps(summary, indent=1))
    json.dump({"summary": summary, "clips": rows}, open(os.path.join(HERE, "cue_to_speech_gap.json"), "w", encoding="utf-8"), indent=1)

if __name__ == "__main__":
    main()
