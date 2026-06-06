"""Tests for video transcription functions."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.transcriber import get_api_key, segments_to_transcript


class TestGetApiKey:
    """Tests for get_api_key function."""

    @patch.dict("os.environ", {"OPENAI_API_KEY": "test-key-transcribe"})
    def test_returns_key_from_env(self):
        """Test that API key is returned from environment."""
        key = get_api_key()
        assert key == "test-key-transcribe"

    @patch.dict("os.environ", {}, clear=True)
    def test_raises_without_key(self):
        """Test error when no API key set."""
        import os
        os.environ.pop("OPENAI_API_KEY", None)
        with pytest.raises(RuntimeError) as exc:
            get_api_key()
        assert "OPENAI_API_KEY" in str(exc.value)


class TestSegmentsToTranscript:
    """Tests for segments_to_transcript function."""

    def test_empty_segments(self):
        """Test with empty segments list."""
        result = segments_to_transcript([])
        assert result == ""

    def test_single_segment(self):
        """Test with single segment."""
        segments = [{"start": 0, "end": 5, "text": "Hello world"}]
        result = segments_to_transcript(segments)
        assert "[00:00] Hello world" == result

    def test_multiple_segments(self):
        """Test with multiple segments."""
        segments = [
            {"start": 0, "end": 5, "text": "First segment"},
            {"start": 5, "end": 10, "text": "Second segment"},
            {"start": 65, "end": 70, "text": "Third segment"},
        ]
        result = segments_to_transcript(segments)
        lines = result.split("\n")
        assert len(lines) == 3
        assert "[00:00] First segment" == lines[0]
        assert "[00:05] Second segment" == lines[1]
        assert "[01:05] Third segment" == lines[2]

    def test_timestamps_formatting(self):
        """Test that timestamps are formatted correctly."""
        segments = [
            {"start": 0, "text": "Zero"},
            {"start": 59, "text": "59 seconds"},
            {"start": 60, "text": "One minute"},
            {"start": 125, "text": "Two min five sec"},
            {"start": 3661, "text": "Over an hour"},
        ]
        result = segments_to_transcript(segments)
        assert "[00:00]" in result
        assert "[00:59]" in result
        assert "[01:00]" in result
        assert "[02:05]" in result
        assert "[61:01]" in result  # 3661 seconds = 61 minutes, 1 second

    def test_strips_whitespace(self):
        """Test that text whitespace is stripped."""
        segments = [{"start": 0, "text": "  Hello world  "}]
        result = segments_to_transcript(segments)
        assert "[00:00] Hello world" == result

    def test_skips_empty_text(self):
        """Test that empty text segments are skipped."""
        segments = [
            {"start": 0, "text": "First"},
            {"start": 5, "text": ""},
            {"start": 10, "text": "  "},
            {"start": 15, "text": "Third"},
        ]
        result = segments_to_transcript(segments)
        lines = result.split("\n")
        assert len(lines) == 2
        assert "First" in lines[0]
        assert "Third" in lines[1]


class TestTranscribeVideo:
    """Tests for transcribe_video function."""

    def test_video_not_found(self, tmp_path):
        """Test error for missing video file."""
        from src.transcriber import transcribe_video
        with pytest.raises(FileNotFoundError):
            transcribe_video(Path("/nonexistent/video.mp4"), tmp_path)

    @patch("src.transcriber.get_video_duration")
    @patch("src.transcriber.extract_audio")
    @patch("src.transcriber.whisper_transcribe")
    @patch("src.transcriber.extract_screenshots")
    def test_creates_output_files(
        self, mock_screenshots, mock_whisper, mock_audio, mock_duration, tmp_path
    ):
        """Test that output files are created."""
        from src.transcriber import transcribe_video

        # Create fake video file
        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake video data")
        output_dir = tmp_path / "output"
        output_dir.mkdir(parents=True, exist_ok=True)

        # Setup mocks - extract_audio needs to create the actual file
        mock_duration.return_value = 60.0

        def create_audio_file(video_path, audio_path, **kwargs):  # kwargs: extract_audio passes bitrate
            audio_path.write_bytes(b"fake audio content")
            return audio_path

        mock_audio.side_effect = create_audio_file
        mock_whisper.return_value = {
            "text": "Full transcription",
            "segments": [{"start": 0, "end": 5, "text": "Test segment"}],
            "duration": 60.0,
        }
        mock_screenshots.return_value = (5, [])

        result = transcribe_video(video, output_dir, extract_screenshots_flag=False)

        assert result.transcript_path.exists()
        assert (output_dir / "transcript.json").exists()
        assert "Test segment" in result.transcript

    @patch("src.transcriber.get_video_duration")
    @patch("src.transcriber.extract_audio")
    @patch("src.transcriber.whisper_transcribe")
    @patch("src.transcriber.extract_screenshots")
    def test_returns_result_object(
        self, mock_screenshots, mock_whisper, mock_audio, mock_duration, tmp_path
    ):
        """Test that TranscribeResult is returned."""
        from src.transcriber import transcribe_video, TranscribeResult

        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake")
        output_dir = tmp_path / "output"
        output_dir.mkdir(parents=True, exist_ok=True)

        mock_duration.return_value = 120.0

        def create_audio_file(video_path, audio_path, **kwargs):  # kwargs: extract_audio passes bitrate
            audio_path.write_bytes(b"fake audio content")
            return audio_path

        mock_audio.side_effect = create_audio_file
        mock_whisper.return_value = {
            "text": "Test text with more words here",
            "segments": [{"start": 0, "end": 5, "text": "Test text"}],
            "duration": 120.0,
        }
        mock_screenshots.return_value = (0, [])

        result = transcribe_video(video, output_dir, extract_screenshots_flag=False)

        assert isinstance(result, TranscribeResult)
        assert result.duration == 120.0
        assert result.word_count > 0
        assert result.transcript_path.exists()
