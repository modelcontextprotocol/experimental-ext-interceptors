// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Secret-format catalog - data-as-textbook (FUNCTIONAL_PATTERNS RULE 8).
 *
 * This is the OPEN/donated detection tier: it recognizes credentials by their
 * public, well-known *format* (prefix + shape). It is clean-room - only the
 * credential FORMATS that appear in MCPHunt's public canary registry
 * (CC-BY 4.0), no proprietary values and no proprietary code. The closed tier
 * (full 11-signal catalog, fragment/partial, and the semantic/embedding tier)
 * is deliberately NOT here.
 *
 * Every entry carries an `origin` (RULE 8). Detection is exact/verbatim only:
 * we find the literal token, which is what the open cross-boundary guard tracks.
 */

export interface SecretFormat {
  readonly id: string;
  readonly label: string;
  /** Global regex matching the credential's public shape. */
  readonly pattern: RegExp;
  /** A benign example of the format, used by calibration tests. */
  readonly example: string;
  readonly origin: string;
}

const ORIGIN = "MCPHunt canary_registry credential formats (arXiv:2604.27819, CC-BY 4.0)";

export const SECRET_FORMATS: readonly SecretFormat[] = [
  { id: "stripe_secret_live", label: "Stripe live secret key", pattern: /sk_live_[A-Za-z0-9]{16,}/g, example: "sk_live_4eC7aRm9Kx2bNw5pQj8sYd", origin: ORIGIN },
  { id: "stripe_pub_live", label: "Stripe live publishable key", pattern: /pk_live_[A-Za-z0-9]{16,}/g, example: "pk_live_51HGf0KxLPq3NmRs7TvW9y", origin: ORIGIN },
  { id: "github_pat", label: "GitHub personal access token", pattern: /ghp_[A-Za-z0-9]{20,}/g, example: "ghp_Xa2bC3dEf4gH5iJk6Lm7nN8oP", origin: ORIGIN },
  { id: "github_oauth", label: "GitHub OAuth token", pattern: /gho_[A-Za-z0-9]{20,}/g, example: "gho_Bc4dEf5gHi6jKl7mNo8pQr9sT", origin: ORIGIN },
  { id: "aws_access_key", label: "AWS access key id", pattern: /AKIA[0-9A-Z]{16}/g, example: "AKIA5MZXN8QRF3WBY6OE", origin: ORIGIN },
  { id: "slack_bot", label: "Slack bot token", pattern: /xoxb-[0-9A-Za-z-]{10,}/g, example: "xoxb-17345628901-AbCdEfGhIjKlMnOp", origin: ORIGIN },
  { id: "slack_refresh", label: "Slack refresh token", pattern: /xoxr-[0-9A-Za-z-]{10,}/g, example: "xoxr-98127345602-QrStUvWxYzAbCdEf", origin: ORIGIN },
] as const;

export interface SecretHit {
  readonly formatId: string;
  readonly value: string;
}

/**
 * Every verbatim secret occurrence in `text`, across all formats. A fresh
 * RegExp is used per scan so global-regex `lastIndex` never leaks between calls
 * (a classic stateful-regex bug - we do not trust shared mutable state).
 */
export function findSecrets(text: string): readonly SecretHit[] {
  const hits: SecretHit[] = [];
  for (const format of SECRET_FORMATS) {
    const re = new RegExp(format.pattern.source, "g");
    for (const m of text.matchAll(re)) {
      hits.push({ formatId: format.id, value: m[0] });
    }
  }
  return hits;
}
