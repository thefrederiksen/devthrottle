"""Phase 6, ruling K1: does the PRODUCT's cue search agree with the research, and does its blanking ever touch speech?

Inputs (local only, never committed - audio and transcripts stay on the machine):
  D:\\ReposFred\\devthrottle_internal\\tools\\transcription-lab\\corpus\\clips\\*.wav
  cue_detector_corpus.csharp.json - written by cue_detector_corpus/ (dotnet run), which calls
                                    CcDirector.Core.Audio.ReadyCue.Find, the recorder's own search.

For every clip this puts side by side:
  research - cue_scan.py's search, reimplemented verbatim below: head = first 1.5 s, middle control = a 1.5 s
             window from the middle of the clip, found when the score is >= 0.5.
  product  - the C# result: head searched to 1550 ms (1.5 s past an assumed 50 ms first buffer), the same
             middle window, and the span it blanks (match - 10 ms to match end + 50 ms).
  speech   - Silero VAD (the lab's detector, as in vad_pass.py and phase 4's cue_to_speech_gap.py) with
             speech_pad_ms=0 so no span is widened by padding; resolution one 32 ms window.

Acceptance (ruling K1):
  1. the product finds the cue in the same clips the research did (any difference listed and explained);
  2. it finds nothing in the three May clips recorded before the cue shipped;
  3. the middle control finds at most the research's 1 of 44;
  4. no blanked span overlaps a detected speech span.
For every blanked clip it also reports the gap from the blank's end to the first speech that starts after it.

Writes cue_detector_corpus.json: numbers and file names only.
"""
import os, glob, json, wave
import numpy as np, torch
from silero_vad import load_silero_vad, get_speech_timestamps

LAB = r"D:\ReposFred\devthrottle_internal\tools\transcription-lab\corpus"
HERE = os.path.dirname(os.path.abspath(__file__))
FOUND = 0.5
MAY_PREFIX = "dictation-20260525-"


# --- cue_scan.py, verbatim ---
def bloop(sr):
    dur, f0, f1, decay, amp, fade = 0.20, 380.0, 1150.0, 26.0, 0.55, 0.006
    n = int(sr * dur)
    t = np.arange(n) / sr
    prog = np.arange(n) / n
    freq = f0 + (f1 - f0) * prog
    phase = np.cumsum(2 * np.pi * freq / sr)
    env = np.exp(-decay * t)
    att = np.minimum(1.0, t / fade)
    rel = np.minimum(1.0, (dur - t) / fade)
    return np.sin(phase) * env * att * rel * amp


def read(path):
    w = wave.open(path, "rb")
    sr, ch, n = w.getframerate(), w.getnchannels(), w.getnframes()
    x = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.float64) / 32768.0
    w.close()
    if ch == 2:
        x = x.reshape(-1, 2).mean(axis=1)
    return sr, x


def best_match(x, tpl):
    m = len(tpl)
    if len(x) < m + 1:
        return 0.0, 0
    tpl = (tpl - tpl.mean()) / (np.linalg.norm(tpl - tpl.mean()) + 1e-12)
    c = np.correlate(x, tpl, mode="valid")
    e = np.sqrt(np.convolve(x * x, np.ones(m), mode="valid")) + 1e-9
    ncc = np.abs(c) / e
    i = int(np.argmax(ncc))
    return float(ncc[i]), i
# --- end cue_scan.py ---


def to16k(x, sr):
    if sr == 16000:
        return x.astype(np.float32)
    idx = np.linspace(0, len(x) - 1, int(len(x) * 16000 / sr))
    return np.interp(idx, np.arange(len(x)), x).astype(np.float32)


def main():
    torch.set_num_threads(1)
    model = load_silero_vad()
    cs = json.load(open(os.path.join(HERE, "cue_detector_corpus.csharp.json"), encoding="utf-8"))
    product = {r["clip"]: r for r in cs["clips"]}

    rows = []
    for path in sorted(glob.glob(os.path.join(LAB, "clips", "*.wav"))):
        name = os.path.basename(path)
        sr, x = read(path)
        tpl = bloop(sr)
        s_head, i_head = best_match(x[: int(1.5 * sr)], tpl)
        mid0 = max(int(1.5 * sr), len(x) // 2 - int(0.75 * sr))
        s_mid, _ = best_match(x[mid0: mid0 + int(1.5 * sr)], tpl)
        p = product[name]

        speech = [(t["start"] / 16000.0, t["end"] / 16000.0) for t in
                  get_speech_timestamps(torch.from_numpy(to16k(x, sr)), model, sampling_rate=16000,
                                        speech_pad_ms=0, return_seconds=False)]
        row = {
            "clip": name,
            "recorded_before_the_cue": name.startswith(MAY_PREFIX),
            "research_head_score": round(s_head, 4), "research_found": s_head >= FOUND,
            "research_match_ms": round(i_head * 1000.0 / sr, 1),
            "research_mid_score": round(s_mid, 4), "research_mid_found": s_mid >= FOUND,
            "product_head_score": p["head_score"], "product_found": p["head_found"],
            "product_match_ms": p["head_match_ms"],
            "product_mid_score": p["mid_score"], "product_mid_found": p["mid_found"],
            "first_speech_start_ms": round(speech[0][0] * 1000, 1) if speech else None,
        }
        if p["head_found"]:
            b0, b1 = p["blank_start_ms"] / 1000.0, p["blank_end_ms"] / 1000.0
            overlaps = [(round(s * 1000, 1), round(e * 1000, 1)) for s, e in speech if s < b1 and e > b0]
            after = [s for s, _ in speech if s >= b1]
            row.update({
                "blank_start_ms": p["blank_start_ms"], "blank_end_ms": p["blank_end_ms"],
                "speech_spans_overlapping_blank_ms": overlaps,
                "gap_blank_end_to_next_speech_ms": round((after[0] - b1) * 1000, 1) if after else None,
            })
        rows.append(row)

    def names(pred):
        return [r["clip"] for r in rows if pred(r)]

    gaps = sorted(r["gap_blank_end_to_next_speech_ms"] for r in rows
                  if r["product_found"] and r["gap_blank_end_to_next_speech_ms"] is not None)
    summary = {
        "clips": len(rows),
        "research_found": sum(r["research_found"] for r in rows),
        "product_found": sum(r["product_found"] for r in rows),
        "found_by_research_only": names(lambda r: r["research_found"] and not r["product_found"]),
        "found_by_product_only": names(lambda r: r["product_found"] and not r["research_found"]),
        "match_position_differs_by_more_than_1ms": names(
            lambda r: r["research_found"] and r["product_found"] and abs(r["research_match_ms"] - r["product_match_ms"]) > 1.0),
        "max_head_score_difference": round(max(abs(r["research_head_score"] - r["product_head_score"]) for r in rows), 4),
        "may_clips": len(names(lambda r: r["recorded_before_the_cue"])),
        "may_clips_found_by_product": names(lambda r: r["recorded_before_the_cue"] and r["product_found"]),
        "research_mid_found": names(lambda r: r["research_mid_found"]),
        "product_mid_found": names(lambda r: r["product_mid_found"]),
        "blanked_clips_with_speech_overlap": names(lambda r: r["product_found"] and r["speech_spans_overlapping_blank_ms"]),
        "blanked_clips_with_no_speech_after_blank": names(
            lambda r: r["product_found"] and r["gap_blank_end_to_next_speech_ms"] is None),
        "gap_blank_end_to_next_speech_ms": {
            "count": len(gaps),
            "min": gaps[0] if gaps else None,
            "p5": round(float(np.percentile(gaps, 5)), 1) if gaps else None,
            "median": round(float(np.median(gaps)), 1) if gaps else None,
        },
    }
    for r in rows:
        print(f"{r['clip']:45} research={r['research_head_score']:.2f}@{r['research_match_ms']:7.1f} "
              f"product={r['product_head_score']:.2f}@{r['product_match_ms']} "
              f"mid={r['research_mid_score']:.2f}/{r['product_mid_score']:.2f} "
              f"blank={r.get('blank_start_ms')}-{r.get('blank_end_ms')} overlap={r.get('speech_spans_overlapping_blank_ms')} "
              f"gap={r.get('gap_blank_end_to_next_speech_ms')}")
    print(json.dumps(summary, indent=1))
    json.dump({"summary": summary, "clips": rows},
              open(os.path.join(HERE, "cue_detector_corpus.json"), "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main()
