"""Convert PDF files to Markdown with image extraction."""

import re
from pathlib import Path

import fitz as pymupdf

try:
    from cc_shared.image_extractor import ExtractedImage, relative_paths_in_order
except ImportError:
    import sys
    import os
    if hasattr(sys, '_MEIPASS'):
        sys.path.insert(0, os.path.join(sys._MEIPASS, 'cc_shared'))
    else:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "cc_shared"))
    from image_extractor import ExtractedImage, relative_paths_in_order


# Font size thresholds for heading detection (relative to body text)
_HEADING1_RATIO = 1.6
_HEADING2_RATIO = 1.3
_HEADING3_RATIO = 1.15


def convert_pdf_to_markdown(
    input_path: Path,
    output_path: Path,
    force: bool = False,
) -> str:
    """Convert a PDF document to Markdown with image extraction.

    Uses pymupdf (fitz) for text block extraction and font-size heuristics
    to detect headings.  Images are extracted via ``page.get_images()``.

    Args:
        input_path: Path to the ``.pdf`` file.
        output_path: Path to the output ``.md`` file.
        force: When True (a ``--force`` re-run), clear the sibling
            ``{stem}_images/`` directory first so repeated conversions do not
            accumulate duplicate image files.

    Returns:
        Markdown string.
    """
    doc = pymupdf.open(str(input_path))
    images: list[ExtractedImage] = []
    pages_md: list[str] = []

    # First pass: determine median font size across the document
    all_font_sizes: list[float] = []
    for page in doc:
        blocks = page.get_text("dict", flags=pymupdf.TEXT_PRESERVE_WHITESPACE)["blocks"]
        for block in blocks:
            if block["type"] != 0:  # text blocks only
                continue
            for line in block["lines"]:
                for span in line["spans"]:
                    size = span["size"]
                    text = span["text"].strip()
                    if text and size > 0:
                        all_font_sizes.append(size)

    body_size = _median(all_font_sizes) if all_font_sizes else 12.0

    # Second pass: extract content
    for page_num, page in enumerate(doc):
        page_lines: list[str] = []

        # Extract images from this page
        image_list = page.get_images(full=True)
        for img_idx, img_info in enumerate(image_list):
            xref = img_info[0]
            base_image = doc.extract_image(xref)
            if base_image:
                ext = base_image.get("ext", "png")
                data = base_image["image"]
                name = f"page{page_num + 1}_image_{img_idx + 1:03d}.{ext}"
                img_index = len(images)
                images.append(ExtractedImage(
                    data=data,
                    extension=ext,
                    original_name=name,
                    alt_text="",
                ))
                page_lines.append(f"![]({{__pdf_image_{img_index}__}})")

        # Extract text blocks
        blocks = page.get_text("dict", flags=pymupdf.TEXT_PRESERVE_WHITESPACE)["blocks"]
        for block in blocks:
            if block["type"] != 0:
                continue

            block_text = ""
            block_max_size = 0.0
            is_bold = False

            for line in block["lines"]:
                line_text = ""
                for span in line["spans"]:
                    text = span["text"]
                    size = span["size"]
                    flags = span["flags"]
                    if size > block_max_size:
                        block_max_size = size
                    # flags bit 4 = bold
                    if flags & (1 << 4):
                        is_bold = True
                    line_text += text
                # Strip trailing whitespace but preserve structure
                block_text += line_text.rstrip() + "\n"

            block_text = block_text.strip()
            if not block_text:
                continue

            # Detect headings by font size ratio
            ratio = block_max_size / body_size if body_size > 0 else 1.0

            if ratio >= _HEADING1_RATIO:
                page_lines.append(f"# {block_text}")
            elif ratio >= _HEADING2_RATIO:
                page_lines.append(f"## {block_text}")
            elif ratio >= _HEADING3_RATIO and is_bold:
                page_lines.append(f"### {block_text}")
            else:
                page_lines.append(block_text)

        pages_md.append("\n\n".join(page_lines))

    doc.close()

    markdown = "\n\n".join(pages_md)

    # Replace image placeholders. Use index-aligned paths so two extracted
    # images that happen to share an original_name do not collide onto one file.
    if images:
        ordered_paths = relative_paths_in_order(images, output_path, clear_existing=force)
        for img_idx, _img in enumerate(images):
            placeholder = f"{{__pdf_image_{img_idx}__}}"
            rel_path = ordered_paths[img_idx]
            markdown = markdown.replace(placeholder, rel_path)

    # Clean up excessive blank lines
    markdown = re.sub(r"\n{3,}", "\n\n", markdown)

    return markdown.strip() + "\n"


def _median(values: list[float]) -> float:
    """Return the median of a list of floats."""
    if not values:
        return 0.0
    sorted_vals = sorted(values)
    n = len(sorted_vals)
    mid = n // 2
    if n % 2 == 0:
        return (sorted_vals[mid - 1] + sorted_vals[mid]) / 2.0
    return sorted_vals[mid]
