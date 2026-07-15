// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

import type { InterceptorPhase } from "./constants.js";
import type { PriorityHint } from "./types.js";

/**
 * Resolve a mutation's ordering priority for a phase (SEP-2624 priority
 * resolution). Lower executes first; the default is 0. Validation interceptors
 * ignore priority — only call this when ordering mutations.
 */
export function resolvePriority(
  hint: PriorityHint | null,
  phase: InterceptorPhase,
): number {
  if (hint === null) return 0;
  if (typeof hint === "number") return hint;
  return hint[phase] ?? 0;
}
