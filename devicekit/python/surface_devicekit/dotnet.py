"""Number and text behaviour of the .NET reference that Python does not share: rounding, custom numeric formats and System.Text.Json output.

Each function here was checked against the .NET runtime on the same inputs, so a Python report prints exactly what the C# one prints."""

from __future__ import annotations

import math
import re
from decimal import ROUND_HALF_UP, Context, Decimal

_ROUND_LIMIT = 1e16
_JUST_BELOW_HALF = 0.49999999999999994
_WIDE = Context(prec=400)
_SHORT_ESCAPES = {"\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t", "\\": "\\\\"}
_ESCAPED_ASCII = frozenset("\"&'+<>`")

# char.IsWhiteSpace in .NET, which is also what the regex class \s and string.Trim() use. Python's str.strip() differs on a few control characters.
WHITESPACE = "\t\n\v\f\r \x85\xa0\u1680" + "".join(map(chr, range(0x2000, 0x200B))) + "\u2028\u2029\u202f\u205f\u3000"
_WHITESPACE_RUN = re.compile("[" + WHITESPACE + "]+")


def trim(text: str) -> str:
    return text.strip(WHITESPACE)


def clean(text: str | None) -> str:
    """Collapse every run of whitespace to one space and trim the ends."""
    return trim(_WHITESPACE_RUN.sub(" ", text or ""))


def _simple_upper(ch: str) -> str:
    # .NET leaves the dotless i and the long s out of its invariant case mapping.
    if ch in "\u0131\u017f":
        return ch
    upper = ch.upper()
    if len(upper) != 1:
        # The Greek letters with ypogegrammeni have a one-character upper-case (their title-case form) next to the multi-character full one.
        title = ch.title()
        return title if len(title) == 1 else ch
    return upper


def equals_ignore_case(a: str, b: str) -> bool:
    """string.Equals(a, b, StringComparison.OrdinalIgnoreCase): upper-cases one character at a time, so unlike str.casefold() "ß" is not "ss"."""
    return len(a) == len(b) and all(_simple_upper(x) == _simple_upper(y) for x, y in zip(a, b))


def _signed(whole: int, like: float) -> float:
    return math.copysign(0.0, like) if whole == 0 else float(whole)


def round_half_away(v: float) -> float:
    """Math.Round(v, 2, MidpointRounding.AwayFromZero).

    .NET scales, nudges and truncates in double arithmetic instead of rounding the decimal text, so a value such as 1.005 lands where
    .NET puts it (1.0), not where Decimal(repr(v)) would (1.01). Copying the steps keeps both ports on the same side of every half-way case."""
    if not abs(v) < _ROUND_LIMIT:
        return v
    scaled = v * 100.0
    return _signed(math.trunc(scaled + math.copysign(_JUST_BELOW_HALF, scaled)), scaled) / 100.0


def round_half_even(v: float) -> float:
    """Math.Round(v, 2): ties go to the even digit, as in the hardware probe."""
    if not abs(v) < _ROUND_LIMIT:
        return v
    scaled = v * 100.0
    return _signed(round(scaled), scaled) / 100.0


def fmt(v: float, decimals: int = 0, *, optional: bool = False) -> str:
    """The .NET custom formats "0" (decimals=0), "0.0" (decimals=1) and "0.#" (decimals=1, optional=True).

    .NET keeps 15 significant digits and then rounds those half away from zero, so 0.25 prints as "0.3" where Python's own format gives "0.2"."""
    if math.isnan(v):
        return "NaN"
    if math.isinf(v):
        return "Infinity" if v > 0 else "-Infinity"
    digits = Decimal(format(v, ".14e")).quantize(Decimal(1).scaleb(-decimals), rounding=ROUND_HALF_UP, context=_WIDE)
    text = format(digits, "f")
    if optional and "." in text:
        text = text.rstrip("0").rstrip(".")
    return text


def format_double(v: float) -> str:
    """A double as System.Text.Json writes it: the shortest text that round-trips, without a ".0" on whole numbers and with E notation
    only outside 1E-4 up to 1E+17."""
    if math.isnan(v) or math.isinf(v):
        raise ValueError("NaN and Infinity cannot be written as JSON numbers.")
    if v == 0:
        return "-0" if math.copysign(1.0, v) < 0 else "0"
    sign = "-" if v < 0 else ""
    mantissa, _, exponent = repr(abs(v)).partition("e")
    whole, _, fraction = mantissa.partition(".")
    digits = whole + fraction
    scale = len(whole) + int(exponent or 0)
    trimmed = digits.lstrip("0")
    scale -= len(digits) - len(trimmed)
    digits = trimmed.rstrip("0")
    if scale > max(len(digits), 17) or scale < -3:
        power = scale - 1
        body = digits[0] + ("." + digits[1:] if len(digits) > 1 else "")
        return f"{sign}{body}E{'-' if power < 0 else '+'}{abs(power):02d}"
    if scale <= 0:
        return f"{sign}0.{'0' * -scale}{digits}"
    if scale >= len(digits):
        return f"{sign}{digits}{'0' * (scale - len(digits))}"
    return f"{sign}{digits[:scale]}.{digits[scale:]}"


def escape_string(text: str) -> str:
    """A JSON string literal as the default System.Text.Json encoder writes it: everything outside printable ASCII is \\uXXXX (upper-case hex,
    astral characters as surrogate pairs), and so are the HTML-sensitive characters " & ' + < > and the backtick."""
    out = ['"']
    for ch in text:
        code = ord(ch)
        if ch in _SHORT_ESCAPES:
            out.append(_SHORT_ESCAPES[ch])
        elif code < 0x20 or code > 0x7E or ch in _ESCAPED_ASCII:
            if code > 0xFFFF:
                code -= 0x10000
                out.append(f"\\u{0xD800 + (code >> 10):04X}\\u{0xDC00 + (code & 0x3FF):04X}")
            else:
                out.append(f"\\u{code:04X}")
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)
