# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Secret-format catalog — the Python port of the OPEN detection tier
(`typescript/sdk/src/samples/security/secret-formats.ts`), clean-room.

Recognizes credentials by their public, well-known *format* (prefix + shape):
exactly the seven formats from MCPHunt's public canary registry (CC-BY 4.0).
Detection is exact/verbatim only — the literal token, which is what the open
cross-boundary guard tracks. Every entry carries an ``origin``.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

_ORIGIN = "MCPHunt canary_registry credential formats (arXiv:2604.27819, CC-BY 4.0)"


@dataclass(frozen=True, kw_only=True)
class SecretFormat:
    id: str
    label: str
    #: Regex source matching the credential's public shape (compiled per scan).
    pattern: str
    #: A benign example of the format, used by calibration tests.
    example: str
    origin: str


SECRET_FORMATS: tuple[SecretFormat, ...] = (
    SecretFormat(id="stripe_secret_live", label="Stripe live secret key", pattern=r"sk_live_[A-Za-z0-9]{16,}", example="sk_live_4eC7aRm9Kx2bNw5pQj8sYd", origin=_ORIGIN),
    SecretFormat(id="stripe_pub_live", label="Stripe live publishable key", pattern=r"pk_live_[A-Za-z0-9]{16,}", example="pk_live_51HGf0KxLPq3NmRs7TvW9y", origin=_ORIGIN),
    SecretFormat(id="github_pat", label="GitHub personal access token", pattern=r"ghp_[A-Za-z0-9]{20,}", example="ghp_Xa2bC3dEf4gH5iJk6Lm7nN8oP", origin=_ORIGIN),
    SecretFormat(id="github_oauth", label="GitHub OAuth token", pattern=r"gho_[A-Za-z0-9]{20,}", example="gho_Bc4dEf5gHi6jKl7mNo8pQr9sT", origin=_ORIGIN),
    SecretFormat(id="aws_access_key", label="AWS access key id", pattern=r"AKIA[0-9A-Z]{16}", example="AKIA5MZXN8QRF3WBY6OE", origin=_ORIGIN),
    SecretFormat(id="slack_bot", label="Slack bot token", pattern=r"xoxb-[0-9A-Za-z-]{10,}", example="xoxb-17345628901-AbCdEfGhIjKlMnOp", origin=_ORIGIN),
    SecretFormat(id="slack_refresh", label="Slack refresh token", pattern=r"xoxr-[0-9A-Za-z-]{10,}", example="xoxr-98127345602-QrStUvWxYzAbCdEf", origin=_ORIGIN),
)


@dataclass(frozen=True, kw_only=True)
class SecretHit:
    format_id: str
    value: str


def find_secrets(text: str) -> tuple[SecretHit, ...]:
    """Every verbatim secret occurrence in ``text``, across all formats, in
    catalog order then position order — the same enumeration order as the TS
    ``findSecrets`` (per-format global scans, concatenated)."""
    return tuple(
        SecretHit(format_id=fmt.id, value=match.group(0))
        for fmt in SECRET_FORMATS
        for match in re.finditer(fmt.pattern, text)
    )
