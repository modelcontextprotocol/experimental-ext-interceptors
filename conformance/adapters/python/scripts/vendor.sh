#!/usr/bin/env bash
# Vendor the WG Python SDK (upstream feature/python-sdk) READ-ONLY into
# _vendor/wg-python-sdk for the conformance run. The checkout is pinned to an
# exact commit, never modified, and never committed (_vendor/ is gitignored):
# the conformance suite certifies upstream's implementation as-is.
set -euo pipefail

# The certified upstream commit (tip of feature/python-sdk when this adapter
# was built). Bump deliberately; the findings in adapter.py are relative to it.
PINNED_SHA="a90c7ce2232828d6cf46ee0ca20dc21be5f5f99d"

ADAPTER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(git -C "$ADAPTER_DIR" rev-parse --show-toplevel)"

if ! git -C "$REPO_ROOT" cat-file -e "${PINNED_SHA}^{commit}" 2>/dev/null; then
  echo "error: pinned commit $PINNED_SHA not present locally." >&2
  echo "fetch it first: git fetch upstream feature/python-sdk" >&2
  exit 1
fi

rm -rf "$ADAPTER_DIR/_vendor/wg-python-sdk"
mkdir -p "$ADAPTER_DIR/_vendor"
git -C "$REPO_ROOT" archive --prefix=wg-python-sdk/ "$PINNED_SHA:python/sdk" \
  | tar -x -C "$ADAPTER_DIR/_vendor"

echo "vendored python/sdk @ $PINNED_SHA -> $ADAPTER_DIR/_vendor/wg-python-sdk"
