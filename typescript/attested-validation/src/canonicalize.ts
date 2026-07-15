/**
 * Deterministic canonical JSON so a signature verifies identically regardless of
 * object key order or whitespace. Aligned with RFC 8785 (JCS) for the value types
 * an interceptor result actually contains - strings, booleans, integers, arrays,
 * and objects. Non-finite numbers are rejected rather than silently coerced.
 *
 * Full RFC 8785 float serialization (ECMAScript `Number` shortest round-trip) is
 * out of scope because interceptor results carry no floats; if that changes,
 * swap this for a JCS library without touching the signing code.
 */
export function canonicalize(value: unknown): string {
  return serialize(value);
}

function serialize(v: unknown): string {
  if (v === null) return "null";

  const t = typeof v;
  if (t === "string" || t === "boolean") return JSON.stringify(v);
  if (t === "number") {
    if (!Number.isFinite(v as number)) {
      throw new Error("canonicalize: cannot serialize a non-finite number");
    }
    return JSON.stringify(v);
  }

  if (Array.isArray(v)) {
    return `[${v.map((e) => (e === undefined ? "null" : serialize(e))).join(",")}]`;
  }

  if (t === "object") {
    const obj = v as Record<string, unknown>;
    const keys = Object.keys(obj)
      .filter((k) => obj[k] !== undefined)
      .sort();
    return `{${keys
      .map((k) => `${JSON.stringify(k)}:${serialize(obj[k])}`)
      .join(",")}}`;
  }

  throw new Error(`canonicalize: cannot serialize value of type ${t}`);
}
