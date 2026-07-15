# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Byte-parity pins: Python ``canonicalize`` == TypeScript ``canonicalize``.

Every ``expected`` literal in ``GOLDEN`` was produced by EXECUTING the
TypeScript canonicalizer (`typescript/attested-validation/src/canonicalize.ts`
under Node 24); every literal in ``NUMBER_PINS`` by executing Node's
``JSON.stringify`` (the TS canonicalizer delegates numbers to it). These pins
are the cross-language contract: if either side changes bytes, a pin fails.

An optional differential fuzz against a live Node process (skipped when
``node`` is absent) re-derives the pins from the source of truth.
"""

from __future__ import annotations

import json
import math
import random
import shutil
import struct
import subprocess

import pytest

from conformance_adapter.canonical import canonicalize

# ── golden pins (expected strings produced by the TS canonicalizer) ──────────

GOLDEN: tuple[tuple[object, str], ...] = (
    (
        {"b": True, "a": [1, "x", None], "z": {"nested": {"deep": "v"}}},
        '{"a":[1,"x",null],"b":true,"z":{"nested":{"deep":"v"}}}',
    ),
    (
        {'quote"key': "line\nbreak\ttab", "ctrl": "\x01\x1f", "uni": "héllo→世界"},
        '{"ctrl":"\\u0001\\u001f","quote\\"key":"line\\nbreak\\ttab","uni":"héllo→世界"}',
    ),
    (
        {"empty": {}, "arr": [], "s": "back\\slash", "zero": 0, "neg": -42, "big": 9007199254740991},
        '{"arr":[],"big":9007199254740991,"empty":{},"neg":-42,"s":"back\\\\slash","zero":0}',
    ),
    ({"B": 1, "a": 2, "Z": 3, "_": 4, "0": 5}, '{"0":5,"B":1,"Z":3,"_":4,"a":2}'),
    # RFC 8785 key order is UTF-16 code units: the astral key U+1F600
    # (surrogates d83d de00) sorts BEFORE U+FFFF. Python's code-point sort
    # would invert this - the exact trap this pin guards.
    ({"\uffff": "bmp-max", "\U0001f600": "astral"}, '{"\U0001f600":"astral","\uffff":"bmp-max"}'),
    (["plain", {"deep": [True, False, None]}], '["plain",{"deep":[true,false,null]}]'),
    ("top-level string with \x07 bell", '"top-level string with \\u0007 bell"'),
    (42, "42"),
    (None, "null"),
)


@pytest.mark.parametrize(("value", "expected"), GOLDEN, ids=[e[:32] for _, e in GOLDEN])
def test_golden_pins(value: object, expected: str) -> None:
    assert canonicalize(value) == expected


# ── number formatting pins (Node `JSON.stringify` outputs) ───────────────────

NUMBER_PINS: tuple[tuple[float, str], ...] = (
    (0.0, "0"),
    (-0.0, "0"),
    (0.1, "0.1"),
    (2 / 3, "0.6666666666666666"),
    (123.456, "123.456"),
    (1e15, "1000000000000000"),
    # Python repr switches to exponent notation at 1e16; ECMAScript at 1e21.
    (1e16, "10000000000000000"),
    (1e20, "100000000000000000000"),
    (123456789012345680000.0, "123456789012345680000"),
    (1e21, "1e+21"),
    (1e22, "1e+22"),
    (1e-5, "0.00001"),
    (1e-6, "0.000001"),
    # Python repr switches at 1e-5; ECMAScript at 1e-7.
    (1e-7, "1e-7"),
    (5e-324, "5e-324"),
    (1.7976931348623157e308, "1.7976931348623157e+308"),
    (9007199254740992.0, "9007199254740992"),
    (0.000001234, "0.000001234"),
)


@pytest.mark.parametrize(("value", "expected"), NUMBER_PINS, ids=[e for _, e in NUMBER_PINS])
def test_number_pins(value: float, expected: str) -> None:
    assert canonicalize(value) == expected


# ── rejections (mirror the TS canonicalizer's error contract) ────────────────


@pytest.mark.parametrize("value", [math.inf, -math.inf, math.nan])
def test_non_finite_rejected(value: float) -> None:
    with pytest.raises(ValueError, match="non-finite"):
        canonicalize(value)


def test_unsafe_integer_rejected() -> None:
    with pytest.raises(ValueError, match="safe range"):
        canonicalize(2**53 + 1)


def test_lone_surrogates_escape_lowercase() -> None:
    assert canonicalize("\ud800\udfff") == '"\\ud800\\udfff"'


# ── differential fuzz against live Node (the pins' source of truth) ──────────

_NODE = shutil.which("node")


@pytest.mark.skipif(_NODE is None, reason="node not available for differential fuzzing")
def test_differential_fuzz_against_node() -> None:
    rng = random.Random(0xF11F593D)
    doubles: list[float] = []
    while len(doubles) < 500:
        value = struct.unpack("<d", struct.pack("<Q", rng.getrandbits(64)))[0]
        if math.isfinite(value):
            doubles.append(value)
    strings = ["".join(chr(rng.randint(0x20, 0xD7FF)) for _ in range(rng.randint(0, 24))) for _ in range(200)]

    # Shortest-round-trip reprs parse to the identical double in both runtimes.
    payload = json.dumps({"doubles": [repr(v) for v in doubles], "strings": strings})
    script = (
        "const { doubles, strings } = JSON.parse(require('fs').readFileSync(0, 'utf8'));"
        "console.log(JSON.stringify({"
        " doubles: doubles.map((r) => JSON.stringify(Number(r))),"
        " strings: strings.map((s) => JSON.stringify(s)),"
        "}));"
    )
    result = subprocess.run(
        [_NODE, "-e", script], input=payload, capture_output=True, text=True, check=True
    )
    expected = json.loads(result.stdout)
    for value, exp in zip(doubles, expected["doubles"], strict=True):
        assert canonicalize(value) == exp
    for value, exp in zip(strings, expected["strings"], strict=True):
        assert canonicalize(value) == exp
