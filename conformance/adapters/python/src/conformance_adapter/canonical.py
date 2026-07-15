# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""RFC 8785 (JCS)-aligned canonical JSON, byte-identical to the TypeScript
implementation (`typescript/attested-validation/src/canonicalize.ts`).

The conformance comparison model is "canonicalize, then compare one string",
so this function must produce the SAME BYTES as the TS canonicalizer for every
value a fixture can carry. The two traps for a Python port, both covered by
pinned golden tests (`tests/test_canonical_parity.py`):

- Key order: RFC 8785 sorts object keys by UTF-16 code units. Python's native
  ``sorted(keys)`` sorts by code point, which disagrees for astral-plane keys
  (U+1F600 sorts BEFORE U+FFFF under UTF-16, AFTER it under code points).
- String escapes: JSON.stringify escapes ``"``/``\\``, uses the five short
  escapes, emits ``\\u00xx`` lowercase for other control characters, escapes
  lone surrogates, and leaves everything else (including astral chars) raw.

Numbers implement ECMAScript ``Number::toString(10)`` (ES2023 §6.1.6.1.20)
over Python's shortest round-trip digits — both runtimes produce shortest-
round-trip digit strings, so only the FORMATTING thresholds differ (Python's
``repr`` switches to exponent notation at 1e16 and 1e-5; ECMAScript at 1e21
and 1e-7), and ``_format_double`` applies the ECMAScript rules exactly.
Non-finite floats are rejected (as in the TS canonicalizer). Python integers
beyond +/-(2^53 - 1) are rejected: a JS pipeline would already have rounded
them at JSON.parse, so byte parity is unattainable and silence would hide
corruption.
"""

from __future__ import annotations

import math
from typing import Any

_SAFE_INTEGER = 2**53 - 1

_SHORT_ESCAPES = {
    0x08: "\\b",
    0x09: "\\t",
    0x0A: "\\n",
    0x0C: "\\f",
    0x0D: "\\r",
    0x22: '\\"',
    0x5C: "\\\\",
}


def canonicalize(value: Any) -> str:
    """Serialize ``value`` to the JCS canonical form (sorted keys, no
    whitespace, minimal escapes), byte-identical to the TS canonicalizer."""
    return _serialize(value)


def _utf16_units(text: str) -> tuple[int, ...]:
    """The UTF-16 code units of ``text`` — RFC 8785's key sort order."""
    units: list[int] = []
    for ch in text:
        cp = ord(ch)
        if cp <= 0xFFFF:
            units.append(cp)
        else:
            cp -= 0x10000
            units.append(0xD800 | (cp >> 10))
            units.append(0xDC00 | (cp & 0x3FF))
    return tuple(units)


def _string(text: str) -> str:
    parts = ['"']
    for ch in text:
        cp = ord(ch)
        short = _SHORT_ESCAPES.get(cp)
        if short is not None:
            parts.append(short)
        elif cp < 0x20 or 0xD800 <= cp <= 0xDFFF:
            parts.append(f"\\u{cp:04x}")
        else:
            parts.append(ch)
    parts.append('"')
    return "".join(parts)


def _number(value: int | float) -> str:
    if isinstance(value, int):
        if abs(value) > _SAFE_INTEGER:
            raise ValueError(
                "canonicalize: integer exceeds the IEEE-754 safe range; "
                "byte parity with a JS canonicalizer is unattainable"
            )
        return str(value)
    if not math.isfinite(value):
        raise ValueError("canonicalize: cannot serialize a non-finite number")
    return _format_double(value)


def _shortest_digits(value: float) -> tuple[str, int]:
    """The shortest round-trip significant digits of a positive double and
    its decimal point position ``n`` (``value == 0.digits * 10**n``)."""
    text = repr(value)
    mantissa, _, exponent = text.partition("e")
    int_part, _, frac_part = mantissa.partition(".")
    raw = int_part + frac_part
    digits = raw.strip("0")
    leading_zeros = len(raw) - len(raw.lstrip("0"))
    n = len(int_part) - leading_zeros + (int(exponent) if exponent else 0)
    return digits, n


def _format_double(value: float) -> str:
    """ECMAScript Number::toString(10): full decimal for -6 < n <= 21,
    otherwise ``d.ddd e±x`` with a mandatory exponent sign."""
    if value == 0:
        return "0"  # covers -0.0: JSON.stringify(-0) is "0"
    if value < 0:
        return f"-{_format_double(-value)}"
    digits, n = _shortest_digits(value)
    k = len(digits)
    if k <= n <= 21:
        return digits + "0" * (n - k)
    if 0 < n <= 21:
        return f"{digits[:n]}.{digits[n:]}"
    if -6 < n <= 0:
        return f"0.{'0' * -n}{digits}"
    exponent = n - 1
    head = digits if k == 1 else f"{digits[0]}.{digits[1:]}"
    return f"{head}e{'+' if exponent >= 0 else '-'}{abs(exponent)}"


def _serialize(v: Any) -> str:
    if v is None:
        return "null"
    if v is True:
        return "true"
    if v is False:
        return "false"
    if isinstance(v, str):
        return _string(v)
    if isinstance(v, (int, float)):
        return _number(v)
    if isinstance(v, (list, tuple)):
        return f"[{','.join(_serialize(e) for e in v)}]"
    if isinstance(v, dict):
        for key in v:
            if not isinstance(key, str):
                raise TypeError(f"canonicalize: object key {key!r} is not a string")
        ordered = sorted(v, key=_utf16_units)
        return f"{{{','.join(f'{_string(k)}:{_serialize(v[k])}' for k in ordered)}}}"
    raise TypeError(f"canonicalize: cannot serialize value of type {type(v).__name__}")
