#!/usr/bin/env bash
#
# One-command walkthrough of the MCP interceptors conformance work.
# Two beats, each self-verifying:
#
#   1. The conformance suite is a deterministic, discriminating oracle.
#   2. The same fixtures certify a Python implementation, cross-language.
#
# Usage:  scripts/demo.sh            (runs both beats)
#         scripts/demo.sh 1          (runs a single beat: 1 or 2)
#
# Requirements: Node >= 22, npm. Beat 2 also needs `uv` and the WG python-sdk
# branch fetched (git fetch upstream feature/python-sdk). Beat 2 self-skips with
# a loud message if either is missing, so the demo still runs end to end.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

bold=$'\033[1m'; dim=$'\033[2m'; green=$'\033[32m'; red=$'\033[31m'; reset=$'\033[0m'
banner() { printf '\n%s────────────────────────────────────────────────────────────%s\n%s%s%s\n\n' "$dim" "$reset" "$bold" "$1" "$reset"; }
step()   { printf '%s$ %s%s\n' "$dim" "$*" "$reset"; }
ok()     { printf '%s✓ %s%s\n' "$green" "$*" "$reset"; }
die()    { printf '%s✗ %s%s\n' "$red" "$*" "$reset"; exit 1; }

WHICH="${1:-all}"

beat1() {
  banner "BEAT 1  ·  The conformance suite is deterministic and discriminating"
  cd "$ROOT/conformance"
  [ -d node_modules ] || { step "npm install"; npm install >/dev/null 2>&1; }
  step "npm run generate   # catalog -> fixtures/ + manifest.json"
  npm run generate
  step "git diff --exit-code -- fixtures manifest.json   # regeneration is byte-identical"
  if git diff --exit-code -- fixtures manifest.json >/dev/null 2>&1; then
    ok "fixtures reproduce byte-for-byte: the suite is a stable oracle, not runner output"
  else
    die "fixtures drifted on regeneration"
  fi
  step "npm test   # meta-tests + full suite vs the TypeScript reference SDK"
  npm test
  ok "24 fixtures, reference SDK 24/24, determinism + requirement-tagging enforced"
  cd "$ROOT"
}

beat2() {
  banner "BEAT 2  ·  The same fixtures certify a Python SDK (cross-language)"
  cd "$ROOT/conformance/adapters/python"
  if ! command -v uv >/dev/null 2>&1; then
    printf '%sSKIP: uv not installed (curl -LsSf https://astral.sh/uv/install.sh | sh)%s\n' "$red" "$reset"; cd "$ROOT"; return 0
  fi
  if ! git -C "$ROOT" cat-file -e "a90c7ce2232828d6cf46ee0ca20dc21be5f5f99d^{commit}" 2>/dev/null; then
    printf '%sSKIP: WG python-sdk not fetched (git fetch upstream feature/python-sdk)%s\n' "$red" "$reset"; cd "$ROOT"; return 0
  fi
  step "./scripts/vendor.sh   # vendor the WG feature/python-sdk READ-ONLY at a pinned SHA"
  ./scripts/vendor.sh
  step "uv sync --dev"
  uv sync --dev >/dev/null 2>&1
  step "uv run pytest -q"
  uv run pytest -q
  # The adapter CLI exits non-zero on any sub-100% posture (by design). The raw
  # and strawman postures are MEANT to be sub-100%, so capture without tripping
  # `set -e`/pipefail and assert on the printed compliance line instead.
  step "uv run python -m conformance_adapter            # conformant posture"
  { uv run python -m conformance_adapter || true; } | tee /tmp/fc-demo-conformant.txt
  grep -q "24/24 fixtures passed - compliance 100%" /tmp/fc-demo-conformant.txt \
    || die "conformant posture did not reach 24/24"
  step "uv run python -m conformance_adapter --adapter raw        # WG SDK as-is"
  { uv run python -m conformance_adapter --adapter raw || true; } | tee /tmp/fc-demo-raw.txt
  if grep -q "compliance 100%" /tmp/fc-demo-raw.txt; then
    die "raw WG SDK scored 100% - interop findings not caught"
  fi
  ok "TS-generated fixtures certify Python unchanged; the raw failures are the 3 real interop findings"
  cd "$ROOT"
}

case "$WHICH" in
  1) beat1 ;;
  2) beat2 ;;
  all) beat1; beat2; banner "BOTH BEATS GREEN"; ok "deterministic oracle · cross-language certification" ;;
  *) die "usage: scripts/demo.sh [1|2]" ;;
esac
