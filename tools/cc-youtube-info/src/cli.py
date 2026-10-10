"""CLI for cc-youtube-info - Extract transcripts, metadata, and information from YouTube videos."""

import json
import sys
from pathlib import Path
from typing import Optional

# Fix Windows console encoding for Unicode output
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

import typer
from rich.console import Console
from rich.table import Table

try:
    from . import __version__
    from .youtube import (
        YouTubeError,
        InvalidURLError,
        VideoNotFoundError,
        NoSubtitlesError,
        VideoInfo,
        get_video_info,
        download_transcript,
        download_transcript_formatted,
        list_languages,
        extract_video_id,
    )
    from .subtitle_parser import format_as_paragraphs
except ImportError:
    from src import __version__
    from src.youtube import (
        YouTubeError,
        InvalidURLError,
        VideoNotFoundError,
        NoSubtitlesError,
        VideoInfo,
        get_video_info,
        download_transcript,
        download_transcript_formatted,
        list_languages,
        extract_video_id,
    )
    from src.subtitle_parser import format_as_paragraphs


app = typer.Typer(
    name="cc-youtube-info",
    help="Extract transcripts, metadata, and information from YouTube videos.",
    add_completion=False,
)

console = Console()
stderr_console = Console(stderr=True)


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-youtube-info version {__version__}")
        raise typer.Exit()


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    version: bool = typer.Option(
        False,
        "--version",
        "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
):
    """Extract transcripts, metadata, and information from YouTube videos."""
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


def format_number(n: Optional[int]) -> str:
    """Format large numbers with commas."""
    if n is None:
        return "N/A"
    return f"{n:,}"


def _output_text(text: str, output: Optional[Path], show_stats: bool = True) -> None:
    """Output text to file or stdout."""
    if output:
        output.write_text(text, encoding="utf-8")
        stderr_console.print(f"[green]Saved:[/green] {output}")
        if show_stats:
            stderr_console.print(
                f"[dim]({len(text.split())} words, {len(text.splitlines())} lines)[/dim]"
            )
    else:
        print(text)


def _handle_youtube_error(e: Exception, use_stderr: bool = False) -> None:
    """Handle YouTube errors with consistent formatting."""
    out = stderr_console if use_stderr else console
    out.print(f"[red]ERROR:[/red] {e}")


def _build_info_json(video_info: VideoInfo) -> dict:
    """Build JSON data dict from video info."""
    return {
        "id": video_info.id,
        "title": video_info.title,
        "channel": video_info.channel,
        "channel_follower_count": video_info.channel_follower_count,
        "duration_seconds": video_info.duration_seconds,
        "duration_formatted": video_info.duration_formatted,
        "upload_date": video_info.upload_date,
        "view_count": video_info.view_count,
        "like_count": video_info.like_count,
        "comment_count": video_info.comment_count,
        "description": video_info.description,
        "thumbnail_url": video_info.thumbnail_url,
        "has_captions": video_info.has_captions,
        "has_auto_captions": video_info.has_auto_captions,
        "categories": video_info.categories,
        "tags": video_info.tags,
        "live_status": video_info.live_status,
        "age_limit": video_info.age_limit,
        "chapters": [
            {"start_time": ch.start_time, "end_time": ch.end_time, "title": ch.title}
            for ch in video_info.chapters
        ] if video_info.chapters else [],
    }


def _build_info_table(video_info: VideoInfo) -> Table:
    """Build rich Table from video info."""
    table = Table(title="Video Information")
    table.add_column("Property", style="cyan")
    table.add_column("Value")

    table.add_row("ID", video_info.id)
    table.add_row("Title", video_info.title)
    table.add_row("Channel", video_info.channel)
    if video_info.channel_follower_count:
        table.add_row("Subscribers", format_number(video_info.channel_follower_count))
    table.add_row("Duration", video_info.duration_formatted)
    if video_info.upload_date:
        table.add_row("Uploaded", video_info.upload_date)
    table.add_row("Views", format_number(video_info.view_count))
    table.add_row("Likes", format_number(video_info.like_count))
    table.add_row("Comments", format_number(video_info.comment_count))
    if video_info.live_status and video_info.live_status != "not_live":
        table.add_row("Live Status", video_info.live_status.replace("_", " ").title())
    if video_info.categories:
        table.add_row("Categories", ", ".join(video_info.categories))
    table.add_row(
        "Manual Captions",
        "[green]Yes[/green]" if video_info.has_captions else "[yellow]No[/yellow]",
    )
    table.add_row(
        "Auto Captions",
        "[green]Yes[/green]" if video_info.has_auto_captions else "[yellow]No[/yellow]",
    )
    if video_info.chapters:
        table.add_row("Chapters", str(len(video_info.chapters)))

    return table


@app.command()
def info(
    url: str = typer.Argument(..., help="YouTube video URL"),
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output as JSON"
    ),
):
    """
    Get video metadata (title, channel, duration, stats, description).

    Example:
        cc-youtube-info info "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
    """
    try:
        video_info = get_video_info(url)

        if json_output:
            console.print(json.dumps(_build_info_json(video_info), indent=2, ensure_ascii=False))
        else:
            console.print(_build_info_table(video_info))
            if video_info.tags:
                console.print(f"\n[cyan]Tags:[/cyan] {', '.join(video_info.tags[:10])}")
                if len(video_info.tags) > 10:
                    console.print(f"[dim]  ... and {len(video_info.tags) - 10} more[/dim]")
            if video_info.description:
                desc = video_info.description
                preview = desc[:300] + "..." if len(desc) > 300 else desc
                console.print(f"\n[cyan]Description:[/cyan]\n{preview}")
            if video_info.age_limit > 0:
                console.print(f"\n[yellow]WARNING:[/yellow] Age-restricted ({video_info.age_limit}+)")
            if not video_info.has_captions and not video_info.has_auto_captions:
                console.print("\n[yellow]WARNING:[/yellow] No captions available for this video.")

    except (InvalidURLError, VideoNotFoundError, YouTubeError) as e:
        _handle_youtube_error(e)
        raise typer.Exit(1)


def _fetch_transcript(url: str, language: str, format: str, auto_only: bool, no_timestamps: bool) -> str:
    """Fetch transcript in the requested format."""
    if format in ("srt", "vtt"):
        return download_transcript_formatted(url, language=language, format=format, prefer_manual=not auto_only)
    return download_transcript(url, language=language, prefer_manual=not auto_only, include_timestamps=not no_timestamps)


def _try_get_video_info(url: str) -> Optional[VideoInfo]:
    """Try to get video info, returning None on failure."""
    try:
        return get_video_info(url)
    except (InvalidURLError, VideoNotFoundError, YouTubeError) as e:
        stderr_console.print(f"[yellow]WARNING:[/yellow] Could not fetch video metadata: {e}")
        stderr_console.print("[dim]Continuing with transcript download...[/dim]")
        return None


@app.command()
def transcript(
    url: str = typer.Argument(..., help="YouTube video URL"),
    output: Optional[Path] = typer.Option(None, "-o", "--output", help="Output file path (default: stdout)"),
    language: str = typer.Option("en", "-l", "--lang", help="Subtitle language code"),
    format: str = typer.Option("txt", "-f", "--format", help="Output format: txt, srt, vtt"),
    paragraphs: bool = typer.Option(False, "-p", "--paragraphs", help="Format as paragraphs (txt only)"),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON with metadata"),
    auto_only: bool = typer.Option(False, "--auto-only", help="Use only auto-generated captions"),
    no_timestamps: bool = typer.Option(False, "--no-timestamps", help="Remove timestamps from output (txt only)"),
):
    """
    Download transcript from YouTube video.

    By default, prefers manual captions over auto-generated.
    Use --auto-only to force auto-generated captions.
    """
    try:
        video_info = _try_get_video_info(url) if json_output else None
        stderr_console.print("[blue]Downloading transcript...[/blue]")

        text = _fetch_transcript(url, language, format, auto_only, no_timestamps)
        if paragraphs and format == "txt":
            text = format_as_paragraphs(text)

        if json_output:
            video_id = video_info.id if video_info else (extract_video_id(url) or "unknown")
            data = {
                "video_id": video_id,
                "title": video_info.title if video_info else None,
                "channel": video_info.channel if video_info else None,
                "duration_seconds": video_info.duration_seconds if video_info else None,
                "language": language,
                "transcript": text,
            }
            _output_text(json.dumps(data, indent=2, ensure_ascii=False), output, show_stats=False)
        else:
            _output_text(text, output)

    except NoSubtitlesError as e:
        _handle_youtube_error(e, use_stderr=True)
        stderr_console.print("\n[yellow]TIP:[/yellow] Not all videos have captions.")
        raise typer.Exit(1)
    except (InvalidURLError, VideoNotFoundError, YouTubeError) as e:
        _handle_youtube_error(e, use_stderr=True)
        raise typer.Exit(1)


@app.command()
def languages(
    url: str = typer.Argument(..., help="YouTube video URL"),
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output as JSON"
    ),
):
    """
    List available transcript languages for a video.

    Example:
        cc-youtube-info languages "https://www.youtube.com/watch?v=VIDEO_ID"
    """
    try:
        langs = list_languages(url)

        if json_output:
            data = [
                {
                    "code": lang.code,
                    "name": lang.name,
                    "is_generated": lang.is_generated,
                    "is_translatable": lang.is_translatable,
                }
                for lang in langs
            ]
            console.print(json.dumps(data, indent=2, ensure_ascii=False))
        else:
            console.print("[cyan]Available Languages:[/cyan]")
            for lang in langs:
                # Avoid duplicating "(auto-generated)" if already in name
                if lang.is_generated and "(auto-generated)" not in lang.name:
                    gen_marker = " [dim](auto-generated)[/dim]"
                else:
                    gen_marker = ""
                console.print(f"  {lang.code}: {lang.name}{gen_marker}")

    except (InvalidURLError, VideoNotFoundError, NoSubtitlesError, YouTubeError) as e:
        _handle_youtube_error(e)
        raise typer.Exit(1)


@app.command()
def chapters(
    url: str = typer.Argument(..., help="YouTube video URL"),
    json_output: bool = typer.Option(
        False, "--json", "-j", help="Output as JSON"
    ),
):
    """
    List video chapters with timestamps.

    Example:
        cc-youtube-info chapters "https://www.youtube.com/watch?v=VIDEO_ID"
    """
    try:
        video_info = get_video_info(url)

        if not video_info.chapters:
            console.print("[yellow]No chapters found for this video.[/yellow]")
            raise typer.Exit(0)

        if json_output:
            data = [
                {
                    "start_time": ch.start_time,
                    "start_formatted": ch.start_formatted,
                    "end_time": ch.end_time,
                    "title": ch.title,
                }
                for ch in video_info.chapters
            ]
            console.print(json.dumps(data, indent=2, ensure_ascii=False))
        else:
            console.print(f"[cyan]Chapters ({len(video_info.chapters)}):[/cyan]")
            for ch in video_info.chapters:
                console.print(f"  {ch.start_formatted}  {ch.title}")

    except (InvalidURLError, VideoNotFoundError, YouTubeError) as e:
        _handle_youtube_error(e)
        raise typer.Exit(1)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-youtube-info", app=app)


if __name__ == "__main__":
    tool_main()
