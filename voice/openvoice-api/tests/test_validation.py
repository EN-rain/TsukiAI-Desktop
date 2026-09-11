import unittest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from validation import normalize_language, normalize_text, normalize_voice_id


class ValidationTests(unittest.TestCase):
    def test_text_is_trimmed_and_whitespace_collapsed(self):
        self.assertEqual(normalize_text("  hello   world ", 20), "hello world")

    def test_blank_and_oversized_text_are_rejected(self):
        with self.assertRaises(ValueError):
            normalize_text("  \n\t", 20)
        with self.assertRaises(ValueError):
            normalize_text("12345", 4)

    def test_language_aliases_are_normalized(self):
        self.assertEqual(normalize_language("en"), "EN")
        self.assertEqual(normalize_language("JP"), "JA")

    def test_unknown_language_is_rejected(self):
        with self.assertRaises(ValueError):
            normalize_language("fr")

    def test_only_configured_voice_is_accepted(self):
        self.assertEqual(normalize_voice_id("tsuki", "tsuki"), "tsuki")
        with self.assertRaises(ValueError):
            normalize_voice_id("../other", "tsuki")
        with self.assertRaises(ValueError):
            normalize_voice_id("other", "tsuki")


if __name__ == "__main__":
    unittest.main()
