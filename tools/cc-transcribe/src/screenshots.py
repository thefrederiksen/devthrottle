"""Screenshot extraction using SSIM-based content change detection."""

from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


@dataclass
class ScreenshotInfo:
    """Information about an extracted screenshot."""
    filepath: Path
    timestamp: float
    reason: str
    similarity_score: float | None = None


def format_timestamp(seconds: float) -> str:
    """Convert seconds to HH-MM-SS format."""
    hours = int(seconds // 3600)
    minutes = int((seconds % 3600) // 60)
    secs = int(seconds % 60)
    return f"{hours:02d}-{minutes:02d}-{secs:02d}"


def ssim(img1: np.ndarray, img2: np.ndarray) -> float:
    """SSIM for uint8 grayscale images (Wang et al. 2004) using only cv2 + numpy.

    Replaces skimage.metrics.structural_similarity and matches its defaults
    (uniform 7x7 window, unbiased covariance, border-cropped mean) to within
    floating-point noise, so scikit-image (and its scipy stack, ~52 MB in the
    tools bundle) is not needed for this one function (issue #174).
    """
    win = 7
    pad = win // 2
    a = img1.astype(np.float64)
    b = img2.astype(np.float64)
    c1 = (0.01 * 255.0) ** 2
    c2 = (0.03 * 255.0) ** 2

    def f(x: np.ndarray) -> np.ndarray:
        return cv2.boxFilter(x, -1, (win, win), borderType=cv2.BORDER_REFLECT)

    ua, ub = f(a), f(b)
    uaa, ubb, uab = f(a * a), f(b * b), f(a * b)
    norm = win * win / (win * win - 1.0)  # unbiased covariance, as skimage
    va = norm * (uaa - ua * ua)
    vb = norm * (ubb - ub * ub)
    vab = norm * (uab - ua * ub)
    s = ((2 * ua * ub + c1) * (2 * vab + c2)) / ((ua * ua + ub * ub + c1) * (va + vb + c2))
    return float(s[pad:-pad, pad:-pad].mean())


def calculate_similarity(frame1: np.ndarray, frame2: np.ndarray) -> float:
    """Calculate SSIM between two frames."""
    gray1 = cv2.cvtColor(frame1, cv2.COLOR_BGR2GRAY)
    gray2 = cv2.cvtColor(frame2, cv2.COLOR_BGR2GRAY)

    # Resize for performance
    height, width = gray1.shape
    if width > 1280:
        scale = 1280 / width
        new_size = (1280, int(height * scale))
        gray1 = cv2.resize(gray1, new_size)
        gray2 = cv2.resize(gray2, new_size)

    return ssim(gray1, gray2)


def extract_screenshots(
    video_path: Path,
    output_dir: Path,
    threshold: float = 0.92,
    min_interval: float = 1.0,
) -> tuple[int, list[ScreenshotInfo]]:
    """
    Extract screenshots when content changes significantly.

    Args:
        video_path: Path to video file
        output_dir: Directory for screenshots
        threshold: SSIM threshold (0-1, lower = more sensitive)
        min_interval: Minimum seconds between screenshots

    Returns:
        Tuple of (count, list of ScreenshotInfo)
    """
    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise ValueError(f"Could not open video: {video_path}")

    fps = cap.get(cv2.CAP_PROP_FPS)
    frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    duration = frame_count / fps

    print(f"[INFO] Video duration: {int(duration//60)}m {int(duration%60)}s")

    output_dir.mkdir(parents=True, exist_ok=True)

    screenshots: list[ScreenshotInfo] = []
    last_frame = None
    last_save_time = -min_interval
    frame_num = 0

    try:
        while True:
            ret, frame = cap.read()
            if not ret:
                break

            current_time = frame_num / fps

            if current_time - last_save_time >= min_interval:
                save_frame = False
                reason = ""
                similarity = None

                if last_frame is None:
                    save_frame = True
                    reason = "first_frame"
                else:
                    similarity = calculate_similarity(frame, last_frame)
                    if similarity < threshold:
                        save_frame = True
                        reason = "content_change"

                if save_frame:
                    ts_str = format_timestamp(current_time)
                    filepath = output_dir / f"screenshot_{ts_str}.png"

                    rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                    Image.fromarray(rgb).save(filepath, "PNG")

                    screenshots.append(ScreenshotInfo(
                        filepath=filepath,
                        timestamp=current_time,
                        reason=reason,
                        similarity_score=similarity
                    ))

                    last_frame = frame.copy()
                    last_save_time = current_time
                    print(f"[OK] Screenshot at {ts_str}")

            frame_num += 1
    finally:
        cap.release()

    print(f"[OK] Extracted {len(screenshots)} screenshots")
    return len(screenshots), screenshots
