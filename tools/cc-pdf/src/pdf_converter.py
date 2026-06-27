"""PDF conversion using a Chromium-based browser in headless mode."""

import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Optional


# Page size dimensions (kept for reference / validation).
PAGE_SIZES = {
    "a4": {"width": "8.27in", "height": "11.69in"},
    "letter": {"width": "8.5in", "height": "11in"},
}

# Map our page-size option to the CSS @page named size that Chrome honors.
_PAGE_NAMES = {
    "a4": "A4",
    "letter": "Letter",
}

# Environment variable a user can set to point cc-pdf at a specific browser
# executable when auto-discovery does not find one.
BROWSER_ENV_VAR = "CC_PDF_BROWSER"


def find_chrome() -> Optional[str]:
    """Find a Chromium-based browser executable for headless PDF printing.

    Looks (in order) at the CC_PDF_BROWSER environment override, then common
    install locations for Google Chrome, Chromium, Microsoft Edge, and Brave,
    then falls back to a PATH lookup. Microsoft Edge and Brave are
    Chromium-based and fully support headless ``--print-to-pdf``.

    Returns:
        Absolute path to a usable browser executable, or None if none found.
    """
    # 1. Explicit environment override wins.
    override = os.getenv(BROWSER_ENV_VAR, "").strip()
    if override:
        if os.path.exists(override):
            return override
        # An override that does not exist is a configuration error; surface it
        # rather than silently falling back to a different browser.
        raise RuntimeError(
            f"{BROWSER_ENV_VAR} is set to '{override}' but that file does not exist."
        )

    local_app_data = os.environ.get("LOCALAPPDATA", "")

    candidate_paths = [
        # Google Chrome - Windows
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        # Microsoft Edge - Windows
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        # Brave - Windows
        r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
        r"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
        # Linux
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser",
        "/usr/bin/microsoft-edge",
        "/usr/bin/microsoft-edge-stable",
        "/usr/bin/brave-browser",
        # macOS
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
    ]

    # Per-user install locations under %LOCALAPPDATA% (avoids hand-building
    # C:\Users\{USERNAME}\... which breaks when USERNAME is empty).
    if local_app_data:
        candidate_paths.extend([
            os.path.join(local_app_data, "Google", "Chrome", "Application", "chrome.exe"),
            os.path.join(local_app_data, "Microsoft", "Edge", "Application", "msedge.exe"),
            os.path.join(local_app_data, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        ])

    for path in candidate_paths:
        if os.path.exists(path):
            return path

    # 2. Fall back to a PATH lookup by common executable names.
    for name in ("chrome", "google-chrome", "google-chrome-stable",
                 "chromium", "chromium-browser", "msedge", "microsoft-edge",
                 "brave", "brave-browser"):
        found = shutil.which(name)
        if found:
            return found

    return None


def _build_page_style(page_size: str, margin: str) -> str:
    """Build a dynamic @page style block from page size and margin.

    Chrome honors the CSS ``@page { size: A4; margin: 1in; }`` rule when
    printing to PDF, so this is how the --page-size and --margin options take
    effect (the print CSS no longer hardcodes them).
    """
    page_name = _PAGE_NAMES[page_size]
    safe_margin = margin.strip() or "1in"
    return (
        "<style>\n"
        f"@page {{ size: {page_name}; margin: {safe_margin}; }}\n"
        "</style>\n"
    )


def _inject_page_style(html_content: str, page_style: str) -> str:
    """Insert the @page style block into the HTML document head."""
    lowered = html_content.lower()
    head_close = lowered.find("</head>")
    if head_close != -1:
        return html_content[:head_close] + page_style + html_content[head_close:]
    # No </head> -- prepend so the rule is still present.
    return page_style + html_content


def convert_to_pdf(
    html_content: str,
    output_path: Path,
    page_size: str = "a4",
    margin: str = "1in",
) -> None:
    """Convert HTML to PDF using a headless Chromium-based browser.

    Args:
        html_content: Complete HTML document string
        output_path: Path for output PDF file
        page_size: Page size ('a4' or 'letter')
        margin: Page margin (e.g., '1in', '2cm')

    Raises:
        ValueError: If the page size is not recognized.
        RuntimeError: If no browser is found or conversion fails.
    """
    # Validate page size
    page_size = page_size.lower()
    if page_size not in PAGE_SIZES:
        raise ValueError(f"Unknown page size: {page_size}. Use 'a4' or 'letter'.")

    # Find a browser
    chrome_exe = find_chrome()
    if not chrome_exe:
        raise RuntimeError(
            "No Chromium-based browser found for PDF conversion. cc-pdf can use "
            "Google Chrome, Chromium, Microsoft Edge, or Brave.\n"
            f"Install one, or set the {BROWSER_ENV_VAR} environment variable to "
            "the full path of a browser executable.\n"
            "Download Chrome from: https://www.google.com/chrome/"
        )

    # Apply the page size and margin via a dynamic @page rule.
    html_content = _inject_page_style(html_content, _build_page_style(page_size, margin))

    # Ensure output directory exists
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Write HTML to temp file, and use a dedicated throwaway user-data-dir so
    # headless printing does not collide with an in-use default profile.
    temp_html = None
    temp_profile_dir = tempfile.mkdtemp(prefix="cc-pdf-profile-")
    try:
        with tempfile.NamedTemporaryFile(
            mode='w', suffix='.html', delete=False, encoding='utf-8'
        ) as f:
            f.write(html_content)
            temp_html = f.name

        abs_html_path = os.path.abspath(temp_html)
        abs_output_path = os.path.abspath(str(output_path))

        # Convert to file URI (Chrome requires this format)
        unix_path = abs_html_path.replace('\\', '/')
        file_uri = f"file:///{unix_path}"

        # Build the browser command
        cmd = [
            chrome_exe,
            '--headless',
            '--disable-gpu',
            '--disable-software-rasterizer',
            '--disable-dev-shm-usage',
            '--no-sandbox',
            '--no-pdf-header-footer',
            '--allow-file-access-from-files',
            f'--user-data-dir={temp_profile_dir}',
            f'--print-to-pdf={abs_output_path}',
            '--run-all-compositor-stages-before-draw',
            file_uri,
        ]

        timeout_seconds = 60
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=timeout_seconds,
            )
        except subprocess.TimeoutExpired:
            # A wedged headless browser (profile lock, GPU init stall) would
            # otherwise dump a raw traceback. Surface a clean, named error.
            raise RuntimeError(
                f"PDF conversion timed out after {timeout_seconds} seconds. "
                "The headless browser did not finish printing - it may be wedged "
                "(profile lock or initialization stall). Close any stray browser "
                "processes and try again."
            )

        if result.returncode != 0 or not os.path.exists(abs_output_path):
            error_msg = result.stderr if result.stderr else "Unknown error"
            raise RuntimeError(f"PDF conversion failed: {error_msg}")

    finally:
        if temp_html and os.path.exists(temp_html):
            os.unlink(temp_html)
        shutil.rmtree(temp_profile_dir, ignore_errors=True)
