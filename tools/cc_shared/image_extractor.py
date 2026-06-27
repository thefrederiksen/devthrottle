"""Shared image extraction utilities for to-markdown conversions.

Provides consistent image saving and markdown reference generation
across all cc-* document tools.
"""

import re
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class ExtractedImage:
    """An image extracted from a document during to-markdown conversion."""

    data: bytes
    extension: str
    original_name: str = ""
    alt_text: str = ""


def sanitize_filename(name: str) -> str:
    """Sanitize a filename by removing unsafe characters.

    Args:
        name: Original filename or label.

    Returns:
        Safe filename string with only alphanumeric, dash, underscore, and dot.
    """
    # Replace spaces with underscores
    name = name.replace(" ", "_")
    # Remove anything that isn't alphanumeric, dash, underscore, or dot
    name = re.sub(r"[^\w\-.]", "", name)
    # Collapse multiple underscores
    name = re.sub(r"_+", "_", name)
    return name.strip("_.")


def save_extracted_images(
    images: list[ExtractedImage],
    output_md_path: Path,
) -> dict[str, str]:
    """Save extracted images to a sibling directory and return markdown paths.

    Creates a directory named ``{stem}_images/`` next to the output markdown file.
    Each image is saved with a sequential filename (``image_001.png``, etc.) unless
    the image already has a meaningful original name.

    Args:
        images: List of extracted images to save.
        output_md_path: Path to the output ``.md`` file (used to derive the
            image directory name).

    Returns:
        Mapping of unique key -> relative markdown path for use in
        ``![alt](path)`` references.  The key is the image's ``original_name``
        when that name is unique across the batch; when two images share the
        same ``original_name`` (or have none), an index-qualified key of the
        form ``"<original_name>#<index>"`` (or ``"#<index>"``) is used so no
        entry is silently overwritten.  Callers that need a guaranteed-stable
        lookup should index the returned mapping by position via
        :func:`relative_paths_in_order`.
    """
    if not images:
        return {}

    stem = output_md_path.stem
    images_dir = output_md_path.parent / f"{stem}_images"
    images_dir.mkdir(parents=True, exist_ok=True)

    # Count how often each original_name occurs so we can detect collisions.
    name_counts: dict[str, int] = {}
    for img in images:
        name_counts[img.original_name] = name_counts.get(img.original_name, 0) + 1

    path_map: dict[str, str] = {}
    used_filenames: set[str] = set()

    for idx, img in enumerate(images, start=1):
        ext = img.extension.lstrip(".")
        if not ext:
            ext = "png"

        # Build filename
        if img.original_name:
            safe_name = sanitize_filename(Path(img.original_name).stem)
            if not safe_name:
                safe_name = f"image_{idx:03d}"
            filename = f"{safe_name}.{ext}"
        else:
            filename = f"image_{idx:03d}.{ext}"

        # Ensure the on-disk filename is unique (two images can share a name).
        dest = images_dir / filename
        if filename in used_filenames or dest.exists():
            filename = f"image_{idx:03d}_{Path(filename).stem}.{ext}"
            dest = images_dir / filename
        used_filenames.add(filename)

        dest.write_bytes(img.data)

        # Build a unique mapping key. Use original_name directly only when it is
        # unique in the batch; otherwise qualify with the index so duplicate
        # source names do not collapse onto one another.
        relative_path = f"{stem}_images/{filename}"
        if img.original_name and name_counts.get(img.original_name, 0) == 1:
            key = img.original_name
        else:
            key = f"{img.original_name}#{idx}"
        path_map[key] = relative_path

    return path_map


def relative_paths_in_order(
    images: list[ExtractedImage],
    output_md_path: Path,
) -> list[str]:
    """Save images and return their relative markdown paths in input order.

    This is the collision-proof companion to :func:`save_extracted_images`:
    the returned list is index-aligned with ``images`` so callers never have to
    key on ``original_name`` (which can be duplicated across images).

    Args:
        images: List of extracted images to save.
        output_md_path: Path to the output ``.md`` file.

    Returns:
        List of relative markdown paths, one per image, in the same order as
        ``images``.
    """
    if not images:
        return []

    path_map = save_extracted_images(images, output_md_path)

    # Reconstruct per-index paths using the same keying rule as above.
    name_counts: dict[str, int] = {}
    for img in images:
        name_counts[img.original_name] = name_counts.get(img.original_name, 0) + 1

    ordered: list[str] = []
    for idx, img in enumerate(images, start=1):
        if img.original_name and name_counts.get(img.original_name, 0) == 1:
            key = img.original_name
        else:
            key = f"{img.original_name}#{idx}"
        ordered.append(path_map[key])
    return ordered
