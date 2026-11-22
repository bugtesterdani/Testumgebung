"""Synthetic extraction regressions; no manufacturer material."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('extractor', Path(__file__).with_name('extract-cab-manual-examples.py'))
extractor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(extractor)


class WrappedCodeTests(unittest.TestCase):
    def test_commands_after_text_are_separate(self):
        source, wraps = extractor.join_wrapped_code(['T 0,0,0,3,3;demo', 'A[?]', 'Mr', 'Ms', 'A1'])
        self.assertEqual('T 0,0,0,3,3;demo\rA[?]\rMr\rMs\rA1', source)
        self.assertEqual(0, wraps)

    def test_binary_option_wrap_does_not_insert_a_data_character(self):
        self.assertEqual(('T 0,0,0,3,3;[BIN:$01,$02]', 1),
                         extractor.join_wrapped_code(['T 0,0,0,3,3;[BIN:$01,', '$02]']))

    def test_replace_wrap_preserves_the_split_word(self):
        self.assertEqual(('R Data;first-part', 1), extractor.join_wrapped_code(['R Data;first-', 'part']))

    def test_html_tag_wrap_is_joined_but_html_rows_remain(self):
        self.assertEqual(('W 0,0,0,10,10,3;<font color=red>demo</font>\rA1', 1),
                         extractor.join_wrapped_code(['W 0,0,0,10,10,3;<font', 'color=red>demo</font>', 'A1']))

    def test_plain_text_wrap_separates_words(self):
        self.assertEqual(('T 0,0,0,3,3;first second', 1),
                         extractor.join_wrapped_code(['T 0,0,0,3,3;first', 'second']))


if __name__ == '__main__':
    unittest.main()
