"""Tests for cc_shared image extraction key uniqueness."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.image_extractor import (
    ExtractedImage,
    save_extracted_images,
    relative_paths_in_order,
)


def _img(name: str, data: bytes) -> ExtractedImage:
    return ExtractedImage(data=data, extension="png", original_name=name)


class TestSaveExtractedImages:
    def test_same_named_images_do_not_collapse(self, tmp_path):
        # Arrange: two distinct images that share an original name.
        images = [_img("logo.png", b"AAAA"), _img("logo.png", b"BBBB")]
        out_md = tmp_path / "doc.md"
        # Act
        path_map = save_extracted_images(images, out_md)
        # Assert: both images are represented by distinct keys and files.
        assert len(path_map) == 2
        rel_paths = set(path_map.values())
        assert len(rel_paths) == 2
        # Both files exist with the correct distinct bytes.
        files = sorted((tmp_path / "doc_images").iterdir())
        contents = {f.read_bytes() for f in files}
        assert contents == {b"AAAA", b"BBBB"}

    def test_unique_names_keyed_by_original_name(self, tmp_path):
        images = [_img("a.png", b"AAAA"), _img("b.png", b"BBBB")]
        out_md = tmp_path / "doc.md"
        path_map = save_extracted_images(images, out_md)
        assert "a.png" in path_map
        assert "b.png" in path_map


class TestRelativePathsInOrder:
    def test_index_aligned_paths_for_duplicates(self, tmp_path):
        images = [_img("logo.png", b"AAAA"), _img("logo.png", b"BBBB")]
        out_md = tmp_path / "doc.md"
        ordered = relative_paths_in_order(images, out_md)
        assert len(ordered) == 2
        # Index-aligned and distinct so neither image is lost.
        assert ordered[0] != ordered[1]

    def test_empty_list(self, tmp_path):
        assert relative_paths_in_order([], tmp_path / "doc.md") == []
