"""Convert HTML files to Markdown with image extraction."""

import base64
import mimetypes
import re
from pathlib import Path

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


def convert_html_to_markdown(
    html_content: str,
    output_path: Path,
    input_dir: Path | None = None,
) -> str:
    """Convert HTML content to Markdown, extracting embedded images.

    Args:
        html_content: Raw HTML string.
        output_path: Where the ``.md`` file will be written (used to derive
            the image output directory).
        input_dir: Directory of the source HTML file, used to resolve
            relative image paths.

    Returns:
        Markdown text with image references pointing to extracted files.
    """
    images: list[ExtractedImage] = []
    img_placeholder_map: dict[str, str] = {}

    # Extract images from <img> tags before conversion
    def _extract_img(match: re.Match) -> str:
        tag = match.group(0)
        src_match = re.search(r'src=["\']([^"\']+)["\']', tag)
        alt_match = re.search(r'alt=["\']([^"\']*)["\']', tag)

        if not src_match:
            return tag

        src = src_match.group(1)
        alt = alt_match.group(1) if alt_match else ""

        # Data URI (base64 embedded)
        data_uri_match = re.match(r"data:image/(\w+);base64,(.+)", src)
        if data_uri_match:
            ext = data_uri_match.group(1)
            if ext == "svg+xml":
                ext = "svg"
            data = base64.b64decode(data_uri_match.group(2))
            placeholder = f"__extracted_image_{len(images)}__"
            images.append(ExtractedImage(
                data=data,
                extension=ext,
                original_name=f"image_{len(images) + 1:03d}.{ext}",
                alt_text=alt,
            ))
            img_placeholder_map[placeholder] = str(len(images) - 1)
            return f'<img src="{placeholder}" alt="{alt}">'

        # Remote URL -- keep as-is
        if src.startswith(("http://", "https://")):
            return tag

        # Local file reference
        if input_dir:
            local_path = input_dir / src
            if local_path.exists():
                data = local_path.read_bytes()
                ext = local_path.suffix.lstrip(".")
                if not ext:
                    guessed = mimetypes.guess_type(str(local_path))[0]
                    ext = guessed.split("/")[1] if guessed else "png"
                placeholder = f"__extracted_image_{len(images)}__"
                images.append(ExtractedImage(
                    data=data,
                    extension=ext,
                    original_name=local_path.name,
                    alt_text=alt,
                ))
                img_placeholder_map[placeholder] = str(len(images) - 1)
                return f'<img src="{placeholder}" alt="{alt}">'

        return tag

    # Replace img tags to extract images before markdownify processes them
    processed_html = re.sub(r"<img\b[^>]*>", _extract_img, html_content)

    # Convert HTML to markdown
    markdown = markdownify(
        processed_html,
        heading_style="ATX",
        bullets="-",
        strip=["script", "style"],
    )

    # Save extracted images and replace placeholders.
    # Use index-aligned paths so two source images with the same filename
    # (for example two different "logo.png" files) do not collide onto one
    # extracted file.
    if images:
        ordered_paths = relative_paths_in_order(images, output_path)
        for placeholder, idx_str in img_placeholder_map.items():
            rel_path = ordered_paths[int(idx_str)]
            markdown = markdown.replace(placeholder, rel_path)

    # Clean up excessive blank lines
    markdown = re.sub(r"\n{3,}", "\n\n", markdown)

    return markdown.strip() + "\n"
