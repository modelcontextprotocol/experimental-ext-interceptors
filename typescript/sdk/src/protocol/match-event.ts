// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/** Hook-event matching shared by chain selection and server-side listing. */
import { INTERCEPTION_EVENT } from "./constants.js";
import type { InterceptionEvent } from "./constants.js";
import type { Interceptor } from "./types.js";

/** `*` matches every event; otherwise exact string equality (SEP-2624). */
export function matchesEvent(
  hookEvents: readonly InterceptionEvent[],
  event: InterceptionEvent,
): boolean {
  return hookEvents.some((e) => e === INTERCEPTION_EVENT.All || e === event);
}

/** Does any hook of this interceptor fire for `event` (any phase)? */
export function interceptsEvent(
  descriptor: Interceptor,
  event: InterceptionEvent,
): boolean {
  return descriptor.hooks.some((h) => matchesEvent(h.events, event));
}
