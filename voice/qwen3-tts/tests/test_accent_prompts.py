from __future__ import annotations

import json
import unittest
from pathlib import Path


PROMPTS = Path(__file__).with_name("english_accent_prompts.json")


class EnglishAccentPromptTests(unittest.TestCase):
    def test_prompt_matrix_is_fixed_and_nonempty(self):
        prompts = json.loads(PROMPTS.read_text(encoding="utf-8"))
        self.assertIsInstance(prompts, list)
        self.assertGreaterEqual(len(prompts), 10)
        self.assertEqual(len(prompts), len(set(prompts)))
        self.assertTrue(all(isinstance(prompt, str) and prompt.strip() for prompt in prompts))


if __name__ == "__main__":
    unittest.main()
