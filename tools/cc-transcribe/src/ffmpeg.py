"""FFmpeg utilities for audio extraction and video info."""

import math
import os
import re
import shutil
import subprocess
from pathlib import Path
from typing import Literal, Optional


AudioFormat = Literal["mp3", "wav", "aac", "flac", "ogg"]


def find_ffmpeg() -> str:
    """Find ffmpeg executable on the system."""
    ffmpeg_path = shutil.which("ffmpeg")
    if ffmpeg_path:
        return ffmpeg_path

    common_paths = [
        "C:/ffmpeg/bin/ffmpeg.exe",
        "C:/Program Files/ffmpeg/bin/ffmpeg.exe",
        "C:/Program Files (x86)/ffmpeg/bin/ffmpeg.exe",
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
    ]

    for path in common_paths:
        if Path(path).exists():
            return path

    raise FileNotFoundError(
        "ffmpeg not found. Please install ffmpeg and add it to your PATH.\n"
        "Download from: https://ffmpeg.org/download.html\n"
        "Windows: choco install ffmpeg\n"
        "Mac: brew install ffmpeg\n"
        "Linux: apt install ffmpeg"
    )


def extract_audio(
    video_path: Path,
    output_path: Optional[Path] = None,
    format: AudioFormat = "mp3",
    bitrate: str = "64k",
    mono: bool = False,
) -> Path:
    """Extract audio from video file.

    Defaults are tuned for speech-to-text (64k mono is plenty for Whisper).
    Pass a higher bitrate and mono=False for music or general-purpose use.
    """
    if not video_path.exists():
        raise FileNotFoundError(f"Video file not found: {video_path}")

    if output_path is None:
        output_path = video_path.with_suffix(f".{format}")

    output_path.parent.mkdir(parents=True, exist_ok=True)

    ffmpeg = find_ffmpeg()
    codec = {"mp3": "libmp3lame", "wav": "pcm_s16le", "aac": "aac", "flac": "flac", "ogg": "libvorbis"}.get(format, "libmp3lame")

    cmd = [ffmpeg, "-i", str(video_path), "-vn", "-acodec", codec, "-ab", bitrate]
    if mono:
        cmd += ["-ac", "1"]
    cmd += ["-y", str(output_path)]
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed: {result.stderr}")

    return output_path


def split_audio_by_size(audio_path: Path, max_size_mb: float = 24.0) -> list[Path]:
    """Split an audio file into parts each under max_size_mb.

    Used to stay below the OpenAI Whisper API's 25 MB upload limit.
    Returns the list of part paths in order. If the file is already
    under the limit, returns [audio_path] unchanged.
    """
    if not audio_path.exists():
        raise FileNotFoundError(f"Audio file not found: {audio_path}")

    file_size = audio_path.stat().st_size
    max_bytes = int(max_size_mb * 1024 * 1024)

    if file_size <= max_bytes:
        return [audio_path]

    duration = get_video_duration(audio_path)
    num_parts = math.ceil(file_size / max_bytes)
    segment_length = duration / num_parts

    ffmpeg = find_ffmpeg()
    parts: list[Path] = []
    for i in range(num_parts):
        start = i * segment_length
        out_path = audio_path.parent / f"{audio_path.stem}_part{i + 1}{audio_path.suffix}"
        cmd = [
            ffmpeg, "-y",
            "-ss", str(start),
            "-t", str(segment_length),
            "-i", str(audio_path),
            "-c:a", "copy",
            str(out_path),
        ]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"ffmpeg split failed for part {i + 1}: {result.stderr}")
        parts.append(out_path)

    return parts


def get_video_duration(video_path: Path) -> float:
    """Get video duration in seconds."""
    if not video_path.exists():
        raise FileNotFoundError(f"Video file not found: {video_path}")

    ffmpeg = find_ffmpeg()

    # Try ffprobe first
    ffprobe = shutil.which("ffprobe")
    if ffprobe:
        cmd = [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", str(video_path)]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0:
            return float(result.stdout.strip())

    # Fallback: parse ffmpeg output
    cmd = [ffmpeg, "-i", str(video_path), "-f", "null", "-"]
    result = subprocess.run(cmd, capture_output=True, text=True)

    match = re.search(r"Duration: (\d+):(\d+):(\d+)\.(\d+)", result.stderr)
    if match:
        h, m, s, cs = map(int, match.groups())
        return h * 3600 + m * 60 + s + cs / 100

    raise RuntimeError("Could not determine video duration")


def get_video_info(video_path: Path) -> dict:
    """Get video file information."""
    if not video_path.exists():
        raise FileNotFoundError(f"Video file not found: {video_path}")

    return {
        "path": str(video_path),
        "name": video_path.name,
        "duration": get_video_duration(video_path),
        "size_bytes": video_path.stat().st_size,
        "format": video_path.suffix.lstrip("."),
    }
