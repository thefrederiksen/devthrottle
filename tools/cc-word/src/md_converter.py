"""Convert Word (DOCX) files to Markdown with image extraction."""

import re
from pathlib import Path

import mammoth
from markdownify import markdownify

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


def convert_docx_to_markdown(
    input_path: Path,
    output_path: Path,
    force: bool = False,
) -> str:
    """Convert a Word document to Markdown, extracting embedded images.

    Uses mammoth to convert DOCX -> HTML (with image callbacks), then
    markdownify to convert HTML -> Markdown.

    Args:
        input_path: Path to the ``.docx`` file.
        output_path: Path to the output ``.md`` file (used to derive
            the image directory).
        force: When True (a ``--force`` re-run), clear the sibling
            ``{stem}_images/`` directory first so repeated conversions do not
            accumulate duplicate image files.

    Returns:
        Markdown string with image references pointing to extracted files.
    """
    images: list[ExtractedImage] = []
    image_counter = 0

    def _convert_image(image):
        """Mammoth image conversion callback."""
        nonlocal image_counter
        image_counter += 1

        with image.open() as img_stream:
            data = img_stream.read()

        content_type = image.content_type or "image/png"
        ext = content_type.split("/")[-1]
        if ext == "jpeg":
            ext = "jpg"
        if ext == "svg+xml":
            ext = "svg"

        alt = getattr(image, "alt_text", "") or ""
        name = f"image_{image_counter:03d}.{ext}"

        images.append(ExtractedImage(
            data=data,
            extension=ext,
            original_name=name,
            alt_text=alt,
        ))

        # Return a placeholder src that we'll replace after saving
        return {"src": f"__docx_image_{image_counter}__"}

    # Convert DOCX to HTML via mammoth
    with open(input_path, "rb") as f:
        result = mammoth.convert_to_html(
            f,
            convert_image=mammoth.images.img_element(_convert_image),
        )

    html = result.value

    # Save extracted images and build replacement map. Use index-aligned paths
    # so two images that happen to share an original_name do not collide onto
    # one extracted file.
    if images:
        ordered_paths = relative_paths_in_order(images, output_path, clear_existing=force)

        # Replace placeholders with actual paths. Placeholders are 1-based; the
        # ordered path list is 0-based and aligned with the images list.
        for idx in range(1, len(images) + 1):
            placeholder = f"__docx_image_{idx}__"
            rel_path = ordered_paths[idx - 1]
            html = html.replace(placeholder, rel_path)

    # Convert HTML to Markdown
    markdown = markdownify(
        html,
        heading_style="ATX",
        bullets="-",
    )

    # Clean up excessive blank lines
    markdown = re.sub(r"\n{3,}", "\n\n", markdown)

    return markdown.strip() + "\n"
