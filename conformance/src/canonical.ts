/**
 * RFC 8785 (JSON Canonicalization Scheme) canonicalization for the conformance
 * suite's "canonicalize, then compare one string" model.
 *
 * This routine was previously imported from `@formalcore/mcp-attested-validation`
 * (the same canonicalization that package signed over). That package has been
 * removed from the tree, so the conformance suite vendors its own copy here to
 * stay self-contained: the golden `canonical` / `finalPayloadCanonical` strings
 * baked into every fixture are produced by THIS function, and the suite's
 * meta-test regenerates in memory and diffs against disk — so any byte-level
 * divergence from the fixtures fails loudly rather than passing silently.
 *
 * RFC 8785 pins the serialization to ECMAScript's: string escaping and number
 * formatting are exactly what `JSON.stringify` emits (shortest round-tripping
 * numbers; minimal string escapes; `/` NOT escaped). The one thing the RFC adds
 * over `JSON.stringify` is that object members are ordered by their keys' UTF-16
 * code units — which is precisely `Array.prototype.sort()`'s default order. So
 * the canonical form is `JSON.stringify` over a structurally identical value
 * whose object keys have been recursively sorted.
 */

/** Recursively rebuild `value` with every object's keys in UTF-16 code-unit order. */
function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (typeof value === "object" && value !== null) {
    const source = value as Record<string, unknown>;
    const out: Record<string, unknown> = {};
    for (const key of Object.keys(source).sort()) {
      out[key] = sortKeys(source[key]);
    }
    return out;
  }
  return value;
}

/** RFC 8785 (JCS) canonical string form of a JSON value. */
export function canonicalize(value: unknown): string {
  return JSON.stringify(sortKeys(value));
}
