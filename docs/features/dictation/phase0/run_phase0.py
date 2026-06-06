"""
Phase 0 proof of fact for the cc-director dictation library.

Verifies that OpenAI gpt-4o-transcribe with the prompt parameter, plus a
Claude Haiku cleanup pass, correctly transcribes company-specific terms.

PASS if all expected company terms appear correctly in the cleaned
transcript for every test clip. FAIL otherwise.

Outputs to docs/features/dictation/phase0/:
  clip{N}.mp3       generated TTS audio
  transcripts.json  raw and cleaned transcripts for all variants
  REPORT.md         human-readable summary with verdict
"""

import json
import os
import subprocess
import sys
from pathlib import Path

from openai import OpenAI

OUT_DIR = Path(__file__).resolve().parent
CLAUDE_EXE = os.path.expanduser(r"~\.local\bin\claude.exe")

CLIPS = [
    "I sent the cc-director patch to mindzie before the CenCon review.",
    "Soren Frederiksen needs the Avalonia changes for ConPTY tested by Friday.",
    "Tell mindzie that the CenCon report is ready and ping the cc-director gateway team.",
]

TERMS = [
    "mindzie",
    "CenCon",
    "ConPTY",
    "cc-director",
    "Avalonia",
    "Soren Frederiksen",
]

# Known mistranscription patterns the user has observed over time and
# curated into the dictionary. This is the "knowledge that would otherwise
# go in a regex table" expressed as natural language for the LLM to use.
COMMON_MISTRANSCRIPTIONS = {
    "mindzie": ["Minzy", "Mindsy", "Mindzy", "Mindzie"],
    "CenCon": ["SenCon", "SENCON", "Sencon"],
    "ConPTY": ["Contui", "ContUI", "ContiUI", "Conty"],
    "cc-director": ["CC Director", "See Director", "CC director"],
    "Soren Frederiksen": ["Soren Fredriksen", "Soeren Frederiksen"],
}

STT_PROMPT = (
    "Glossary of names and terms used by the speaker: "
    + ", ".join(TERMS)
    + "."
)


def build_cleanup_instruction():
    lines = []
    lines.append(
        "You are cleaning up a voice dictation transcript from a software "
        "engineer."
    )
    lines.append("")
    lines.append(
        "The speaker uses these technical terms and proper nouns, which "
        "MUST appear correctly in the output (exact capitalization and "
        "punctuation):"
    )
    for t in TERMS:
        lines.append(f"  - {t}")
    lines.append("")
    lines.append(
        "Speech-to-text often mishears these terms. Here are common "
        "mistranscription patterns observed in real use. When you see "
        "one of these in the transcript, replace it with the canonical "
        "term on the left:"
    )
    for canonical, variants in COMMON_MISTRANSCRIPTIONS.items():
        variants_str = ", ".join(f'"{v}"' for v in variants)
        lines.append(f"  - {canonical} : {variants_str}")
    lines.append("")
    lines.append(
        "This list is not exhaustive. If you see a word that is not a "
        "standard English word AND is a plausible near-miss for one of "
        "the listed terms, also replace it. When unsure between two "
        "possible matches, pick the one that fits the sentence context. "
        "If a word is truly ambiguous and you have no way to decide, "
        "leave it alone rather than guessing."
    )
    lines.append("")
    lines.append(
        "Also fix obvious filler words (uh, um, like). Preserve all "
        "other words exactly as they appear."
    )
    lines.append("")
    lines.append(
        "Return ONLY the cleaned transcript text on a single line. No "
        "commentary, no quotes, no preamble."
    )
    return "\n".join(lines)


CLEANUP_INSTRUCTION = build_cleanup_instruction()


def generate_audio(client, text, dest):
    print(f"  Generating audio: {dest.name}", flush=True)
    with client.audio.speech.with_streaming_response.create(
        model="tts-1",
        voice="alloy",
        input=text,
    ) as resp:
        resp.stream_to_file(dest)


def transcribe(client, audio_path, prompt=None):
    with open(audio_path, "rb") as f:
        kwargs = {"model": "gpt-4o-transcribe", "file": f}
        if prompt:
            kwargs["prompt"] = prompt
        resp = client.audio.transcriptions.create(**kwargs)
    return resp.text


def haiku_cleanup(raw_text):
    full_prompt = (
        f"{CLEANUP_INSTRUCTION}\n\nTranscript to clean:\n{raw_text}"
    )
    env = os.environ.copy()
    for k in (
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CC_SESSION_ID",
        "GIT_EDITOR",
    ):
        env.pop(k, None)
    result = subprocess.run(
        [
            CLAUDE_EXE,
            "--print",
            "--model",
            "haiku",
            "--no-session-persistence",
            "--tools",
            "",
            "--dangerously-skip-permissions",
            "--output-format",
            "text",
            full_prompt,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        env=env,
        timeout=180,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"claude haiku failed (exit {result.returncode}): {result.stderr}"
        )
    return result.stdout.strip()


def missing_terms(text, terms):
    text_lower = text.lower()
    return [t for t in terms if t.lower() not in text_lower]


def main():
    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        print("ERROR: OPENAI_API_KEY not set", file=sys.stderr)
        sys.exit(1)

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    client = OpenAI(api_key=api_key)
    results = []

    for i, sentence in enumerate(CLIPS, start=1):
        clip_terms = [t for t in TERMS if t.lower() in sentence.lower()]
        audio_file = OUT_DIR / f"clip{i}.mp3"

        print(f"\nClip {i}: '{sentence}'", flush=True)
        if not audio_file.exists():
            generate_audio(client, sentence, audio_file)
        else:
            print(f"  (audio cached at {audio_file.name})", flush=True)

        print("  Variant 1: no prompt", flush=True)
        v1 = transcribe(client, audio_file, prompt=None)
        print(f"    -> {v1}", flush=True)

        print("  Variant 2: with prompt", flush=True)
        v2 = transcribe(client, audio_file, prompt=STT_PROMPT)
        print(f"    -> {v2}", flush=True)

        print("  Variant 3: with prompt + Haiku cleanup", flush=True)
        v3 = haiku_cleanup(v2)
        print(f"    -> {v3}", flush=True)

        results.append(
            {
                "clip": i,
                "expected_sentence": sentence,
                "expected_terms": clip_terms,
                "variants": {
                    "no_prompt": {
                        "transcript": v1,
                        "missing_terms": missing_terms(v1, clip_terms),
                    },
                    "with_prompt": {
                        "transcript": v2,
                        "missing_terms": missing_terms(v2, clip_terms),
                    },
                    "with_prompt_plus_cleanup": {
                        "transcript": v3,
                        "missing_terms": missing_terms(v3, clip_terms),
                    },
                },
            }
        )

    transcripts_path = OUT_DIR / "transcripts.json"
    transcripts_path.write_text(
        json.dumps(results, indent=2), encoding="utf-8"
    )
    print(f"\nWrote {transcripts_path}", flush=True)

    total = 0
    passes = 0
    for r in results:
        for t in r["expected_terms"]:
            total += 1
            if t not in r["variants"]["with_prompt_plus_cleanup"]["missing_terms"]:
                passes += 1
    verdict = "PASS" if passes == total else "FAIL"

    lines = []
    lines.append("# Phase 0 Report")
    lines.append("")
    lines.append(
        f"Verdict: {verdict} ({passes}/{total} expected company-term occurrences "
        f"recovered in the final variant)"
    )
    lines.append("")
    lines.append("## Method")
    lines.append("")
    lines.append(
        f"Generated {len(CLIPS)} synthetic clips with OpenAI tts-1 (voice=alloy)."
    )
    lines.append(
        "Each clip transcribed with gpt-4o-transcribe in three variants:"
    )
    lines.append("")
    lines.append("1. No prompt parameter (baseline).")
    lines.append(
        "2. With the prompt parameter packed with the company term glossary."
    )
    lines.append(
        "3. Variant 2 transcript run through Claude Haiku with the term list "
        "in the system prompt."
    )
    lines.append("")
    lines.append(
        "Pass criterion: every expected company term appears in the variant 3 "
        "transcript for every clip (case-insensitive substring match)."
    )
    lines.append("")
    lines.append("## Results")
    lines.append("")

    for r in results:
        lines.append(f"### Clip {r['clip']}")
        lines.append("")
        lines.append(f"Expected sentence: `{r['expected_sentence']}`")
        lines.append("")
        lines.append(
            f"Expected company terms: {', '.join(r['expected_terms'])}"
        )
        lines.append("")
        for name, payload in r["variants"].items():
            ms = payload["missing_terms"]
            status = "OK" if not ms else f"MISSING: {', '.join(ms)}"
            lines.append(f"**{name}** [{status}]")
            lines.append("")
            lines.append(f"> {payload['transcript']}")
            lines.append("")

    lines.append("## Interpretation")
    lines.append("")
    if verdict == "PASS":
        lines.append(
            "OpenAI gpt-4o-transcribe with the prompt parameter, followed by "
            "a Claude Haiku cleanup pass that has the term list in its system "
            "prompt, reliably recovers all expected company terms across the "
            "synthetic test clips."
        )
        lines.append("")
        lines.append(
            "The dictionary mechanism described in PLAN.md is sound. Proceed "
            "to Phase 1."
        )
    else:
        lines.append(
            "Variant 3 did not recover every expected company term. Inspect "
            "transcripts.json. Possible follow-ups before committing to Phase 1:"
        )
        lines.append("")
        lines.append(
            "- Refine the Haiku cleanup prompt with explicit positive and "
            "negative examples for the terms that slipped through."
        )
        lines.append(
            "- Try gpt-4o-mini-transcribe for comparison (cheaper and may "
            "behave differently with the prompt parameter)."
        )
        lines.append(
            "- Note: TTS pronunciation may not match how a human says these "
            "terms. Real-voice Phase 2 testing could land closer to one side "
            "or the other."
        )
        lines.append(
            "- Reconsider AssemblyAI keyterm boosting if the gap is "
            "irreducible (out of scope per PLAN.md, but a known fallback)."
        )
    lines.append("")

    report_path = OUT_DIR / "REPORT.md"
    report_path.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {report_path}", flush=True)
    print(f"\nVerdict: {verdict} ({passes}/{total} terms)", flush=True)


if __name__ == "__main__":
    main()
