"""Tests for cc_shared lazy submodule imports (PEP 562).

The package __init__ must NOT eagerly import .config / .llm (which pull in
cc_storage), so the document tools (cc-pdf / cc-html / cc-word) that only need
.themes / .markdown_parser / .image_extractor do not inherit that dependency.
Attribute access and the direct submodule forms must still resolve.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


class TestLazyImports:
    def test_init_does_not_eagerly_import_config_or_llm(self):
        # Importing the package alone must not drag config/llm into sys.modules.
        # Save and restore the affected sys.modules entries so this test does not
        # pollute global import state for other test modules (e.g. test_llm.py,
        # which patches the cc_shared.llm module object).
        names = ("cc_shared", "cc_shared.config", "cc_shared.llm")
        saved = {name: sys.modules.get(name) for name in names}
        for name in names:
            sys.modules.pop(name, None)
        try:
            import cc_shared  # noqa: F401

            assert "cc_shared.config" not in sys.modules
            assert "cc_shared.llm" not in sys.modules
        finally:
            for name, module in saved.items():
                if module is not None:
                    sys.modules[name] = module
                else:
                    sys.modules.pop(name, None)

    def test_attribute_access_resolves_get_config(self):
        import cc_shared

        # cc_shared.get_config must resolve lazily via __getattr__.
        assert callable(cc_shared.get_config)
        assert cc_shared.CCDirectorConfig is not None

    def test_attribute_access_resolves_llm_provider(self):
        import cc_shared

        assert callable(cc_shared.get_llm_provider)
        assert cc_shared.LLMProvider is not None

    def test_attribute_access_resolves_markdown_parser(self):
        import cc_shared

        assert callable(cc_shared.parse_markdown)
        assert cc_shared.ParsedMarkdown is not None

    def test_direct_submodule_import_still_works(self):
        from cc_shared.config import get_config  # noqa: F401

        assert callable(get_config)

    def test_unknown_attribute_raises(self):
        import cc_shared

        try:
            cc_shared.does_not_exist
            assert False, "expected AttributeError"
        except AttributeError:
            pass
