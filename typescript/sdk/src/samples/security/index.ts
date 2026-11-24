// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by an Apache-2.0
// license that can be found in the LICENSE file.

/**
 * Reference security interceptors (open tier): verbatim cross-boundary
 * secret-flow blocking and outbound secret redaction, built on the public
 * authoring surface (`defineValidator` / `defineMutator`).
 */
export {
  CROSS_BOUNDARY_GUARD_NAME,
  createCrossBoundaryGuard,
} from "./cross-boundary-guard.js";
export {
  SECRETLESS_REDACTOR_NAME,
  createSecretlessRedactor,
  handleFor,
} from "./secretless-redactor.js";
export { SECRET_FORMATS, findSecrets } from "./secret-formats.js";
export type { SecretFormat, SecretHit } from "./secret-formats.js";
export { TOOL_SERVER, serverOf } from "./server-of.js";
