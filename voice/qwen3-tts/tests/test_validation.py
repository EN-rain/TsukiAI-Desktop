import unittest

from validation import audio_stats, normalize_language, validate_text


class ValidationTests(unittest.TestCase):
    def test_language_aliases_are_explicit(self):
        self.assertEqual(normalize_language("EN"), "English")
        self.assertEqual(normalize_language("JP"), "Japanese")
        with self.assertRaises(ValueError):
            normalize_language("French")

    def test_text_is_trimmed_and_bounded(self):
        self.assertEqual(validate_text("  hello  ", 10), "hello")
        with self.assertRaises(ValueError):
            validate_text("123456", 5)

    def test_audio_stats_rejects_empty_shape(self):
        class Empty:
            def __len__(self):
                return 0

        self.assertEqual(audio_stats(Empty(), 24000), (0.0, 0.0, 0.0))


if __name__ == "__main__":
    unittest.main()
