// Copyright 2025 The MCP Interceptors Authors. All rights reserved.
// Use of this source code is governed by a Apache-2.0
// license that can be found in the LICENSE file.

/**
 * JSON Canonicalization Scheme (RFC 8785).
 *
 * RFC 8785 defines number and string serialization in terms of ECMAScript
 * JSON.stringify, and property order as UTF-16 code unit order, which is
 * what Array.prototype.sort compares by default.
 */

const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;

/**
 * Returns the canonical JSON text of `value`. Throws a TypeError for values
 * that have no I-JSON representation (non-finite numbers, bigints, lone
 * surrogates, functions, undefined outside an object property, array holes,
 * non-plain objects).
 */
export function canonicalize(value: unknown): string {
  if (value === null) {
    return 'null';
  }
  switch (typeof value) {
    case 'boolean':
      return value ? 'true' : 'false';
    case 'number':
      if (!Number.isFinite(value)) {
        throw new TypeError(`JCS: cannot serialize number ${value}`);
      }
      return JSON.stringify(value);
    case 'string':
      if (LONE_SURROGATE.test(value)) {
        throw new TypeError('JCS: string contains a lone surrogate');
      }
      return JSON.stringify(value);
    case 'object': {
      if (typeof (value as { toJSON?: unknown }).toJSON === 'function') {
        throw new TypeError('JCS: cannot serialize an object with a custom toJSON method');
      }
      if (Array.isArray(value)) {
        // Visit every index: map() skips holes, which JSON sends as null.
        return `[${Array.from(value, canonicalize).join(',')}]`;
      }
      // Class instances such as Date have a wire form that differs from
      // their own enumerable properties, so only plain objects are accepted.
      const proto: unknown = Object.getPrototypeOf(value);
      if (proto !== Object.prototype && proto !== null) {
        throw new TypeError('JCS: cannot serialize a non-plain object');
      }
      const obj = value as Record<string, unknown>;
      const keys = Object.keys(obj)
        .filter((k) => obj[k] !== undefined)
        .sort();
      return `{${keys.map((k) => `${canonicalize(k)}:${canonicalize(obj[k])}`).join(',')}}`;
    }
    default:
      throw new TypeError(`JCS: cannot serialize ${typeof value}`);
  }
}
