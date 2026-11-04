import math
import unittest

from surface_devicekit.dotnet import clean, equals_ignore_case, escape_string, fmt, format_double, round_half_away, round_half_even, trim


class RoundingTests(unittest.TestCase):
    def test_r_rounds_half_away_from_zero_in_double_arithmetic_as_net_does(self):
        # Decimal(repr(v)) would give 0.15, 1.01 and -0.15 for the first three; .NET scales and truncates in binary and lands on the other side.
        cases = {0.145: 0.14, 1.005: 1.0, -0.145: -0.14, -1.005: -1.0, 2.675: 2.68, 62.995: 63.0, 15.995: 16.0, 28.791: 28.79, 1.115: 1.12, 8.285: 8.29}
        for value, expected in cases.items():
            with self.subTest(value):
                self.assertEqual(round_half_away(value), expected)

    def test_exact_ties_go_away_from_zero(self):
        for value, expected in {0.005: 0.01, 0.015: 0.02, 0.025: 0.03, 2.5: 2.5, -0.005: -0.01}.items():
            with self.subTest(value):
                self.assertEqual(round_half_away(value), expected)

    def test_the_probe_rounds_ties_to_even_instead(self):
        cases = {0.125: 0.12, 0.375: 0.38, 0.625: 0.62, 62.5: 62.5, 0.145: 0.14, 1.005: 1.0, 127.9999: 128.0}
        for value, expected in cases.items():
            with self.subTest(value):
                self.assertEqual(round_half_even(value), expected)

    def test_negative_zero_and_values_beyond_the_double_fraction_are_left_alone(self):
        self.assertEqual(math.copysign(1.0, round_half_away(-0.0)), -1.0)
        self.assertEqual(math.copysign(1.0, round_half_even(-0.0)), -1.0)
        for rounder in (round_half_away, round_half_even):
            self.assertEqual(rounder(1e16), 1e16)
            self.assertEqual(rounder(float("inf")), float("inf"))
            self.assertTrue(math.isnan(rounder(float("nan"))))


class NumberFormatTests(unittest.TestCase):
    def test_custom_formats_round_half_away_from_zero_after_15_significant_digits(self):
        cases = [(2.25, 1, "2.3"), (0.25, 1, "0.3"), (0.05, 1, "0.1"), (1.45, 1, "1.5"), (2.5, 0, "3"), (3.5, 0, "4"), (0.5, 0, "1"), (60.5, 1, "60.5"), (28.79, 1, "28.8")]
        for value, decimals, expected in cases:
            with self.subTest(value=value, decimals=decimals):
                self.assertEqual(fmt(value, decimals), expected)
        self.assertEqual(fmt(-0.04, 1), "-0.0")
        self.assertEqual(fmt(-0.05, 1), "-0.1")

    def test_an_optional_decimal_is_dropped_when_it_is_zero(self):
        cases = {7.0: "7", 7.04: "7", 7.05: "7.1", 15.6: "15.6", 128: "128"}
        for value, expected in cases.items():
            with self.subTest(value):
                self.assertEqual(fmt(value, 1, optional=True), expected)

    def test_json_numbers_are_the_shortest_text_that_round_trips(self):
        cases = [
            (128.0, "128"),
            (62.5, "62.5"),
            (0.0, "0"),
            (-0.0, "-0"),
            (1 / 3, "0.3333333333333333"),
            (0.0001, "0.0001"),
            (0.00012, "0.00012"),
            (1e-5, "1E-05"),
            (5e-324, "5E-324"),
            (1e15, "1000000000000000"),
            (1e16, "10000000000000000"),
            (1e17, "1E+17"),
            (1.5e17, "1.5E+17"),
            (123456789012345678.0, "1.2345678901234568E+17"),
            (1e21, "1E+21"),
        ]
        for value, expected in cases:
            with self.subTest(value):
                self.assertEqual(format_double(value), expected)
                self.assertEqual(float(expected), value)

    def test_json_cannot_hold_infinity_or_nan(self):
        for value in (float("inf"), float("-inf"), float("nan")):
            with self.subTest(value), self.assertRaises(ValueError):
                format_double(value)


class TextTests(unittest.TestCase):
    def test_trim_removes_the_whitespace_of_net_not_that_of_python(self):
        self.assertEqual(trim("\u3000x\xa0\u2003\x85"), "x")
        # str.strip() removes the information separators U+001C-U+001F, string.Trim() keeps them; U+200B is not white space either.
        self.assertEqual(trim("\x1cx\x1c"), "\x1cx\x1c")
        self.assertEqual(trim("\u200bx"), "\u200bx")

    def test_clean_collapses_every_run_of_whitespace_to_one_space(self):
        self.assertEqual(clean("  NVIDIA \t RTX\u00a0Spark\r\n N1X  "), "NVIDIA RTX Spark N1X")
        self.assertEqual(clean("\x1c a"), "\x1c a")
        self.assertEqual(clean(None), "")
        self.assertEqual(clean(" \t "), "")

    def test_names_are_equal_ignoring_case_one_character_at_a_time(self):
        self.assertTrue(equals_ignore_case("Qualcomm(R) Hexagon(TM) NPU", "QUALCOMM(R) HEXAGON(TM) NPU"))
        self.assertTrue(equals_ignore_case("straße", "STRAßE"))
        self.assertFalse(equals_ignore_case("straße", "STRASSE"))
        self.assertFalse(equals_ignore_case("ß", "ss"))
        self.assertFalse(equals_ignore_case("NPU", "NPU "))
        # .NET leaves the dotless i and the long s out of its case mapping, but pairs the Greek letters with ypogegrammeni with their title-case forms.
        self.assertFalse(equals_ignore_case("\u0131", "I"))
        self.assertFalse(equals_ignore_case("\u017f", "s"))
        self.assertTrue(equals_ignore_case("\u1fb3", "\u1fbc"))

    def test_strings_are_escaped_as_the_default_json_encoder_writes_them(self):
        cases = {
            'a"b': '"a\\u0022b"',
            "<a href='x'>&+`</a>": '"\\u003Ca href=\\u0027x\\u0027\\u003E\\u0026\\u002B\\u0060\\u003C/a\\u003E"',
            "N1X · Spark": '"N1X \\u00B7 Spark"',
            "😀": '"\\uD83D\\uDE00"',
            "\x7f": '"\\u007F"',
            "line\nbreak\ttab\\slash": '"line\\nbreak\\ttab\\\\slash"',
            "\x00\x1f": '"\\u0000\\u001F"',
            "": '""',
        }
        for text, expected in cases.items():
            with self.subTest(text):
                self.assertEqual(escape_string(text), expected)
