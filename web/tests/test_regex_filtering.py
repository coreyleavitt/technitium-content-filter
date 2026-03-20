"""Unit tests for _regex_matches in filtering module."""

import pytest

from technitium_content_filter.filtering import _regex_matches


@pytest.mark.unit
class TestRegexMatches:
    def test_match(self):
        assert _regex_matches([r"^ads?\d*\."], "ad.example.com") == r"^ads?\d*\."

    def test_no_match(self):
        assert _regex_matches([r"^ads?\d*\."], "safe.example.com") is None

    def test_case_insensitive(self):
        assert _regex_matches([r"example\.com"], "EXAMPLE.COM") == r"example\.com"

    def test_invalid_pattern_skipped(self):
        assert _regex_matches([r"[invalid", r"valid\.com"], "valid.com") == r"valid\.com"

    def test_all_invalid_returns_none(self):
        assert _regex_matches([r"[invalid", r"(unclosed"], "anything.com") is None

    def test_empty_patterns_returns_none(self):
        assert _regex_matches([], "anything.com") is None

    def test_trailing_dot_trimmed(self):
        assert _regex_matches([r"example\.com$"], "example.com.") == r"example\.com$"

    def test_multiple_patterns_first_match_wins(self):
        result = _regex_matches([r"first\.com", r"second\.com"], "first.com")
        assert result == r"first\.com"

    def test_partial_match(self):
        # re.search matches anywhere, not just start
        assert _regex_matches([r"example"], "sub.example.com") == r"example"
