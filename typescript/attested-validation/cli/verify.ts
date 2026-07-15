/**
 * Offline verifier - the "check it yourself" surface.
 *
 * An auditor runs this on a laptop months later, against a key they hold, with
 * no access to the issuer's systems and no network. The attested validation
 * result either verifies or it does not.
 *
 *   node --experimental-strip-types cli/verify.ts <result.json> <pubkey-b64 | @pubkey.txt>
 */
import { readFileSync } from "node:fs";
import process from "node:process";
import { verifyAttestedValidationResult } from "../src/attest.ts";
import type { ValidationResult } from "../src/types.ts";

function resolvePinnedKey(arg: string): string {
  return arg.startsWith("@") ? readFileSync(arg.slice(1), "utf8").trim() : arg;
}

export async function main(argv: readonly string[]): Promise<number> {
  const [resultPath, keyArg] = argv;
  if (!resultPath || !keyArg) {
    console.error(
      "usage: verify <result.json> <trusted-pubkey-b64 | @pubkey-file>",
    );
    return 2;
  }
  const result = JSON.parse(readFileSync(resultPath, "utf8")) as ValidationResult;
  const v = await verifyAttestedValidationResult(result, resolvePinnedKey(keyArg));
  if (v.ok) {
    console.log("PASS - attestation verifies against the pinned issuer key");
    return 0;
  }
  console.log(`FAIL - ${v.reason}`);
  return 1;
}

if (process.argv[1]?.endsWith("verify.ts")) {
  main(process.argv.slice(2)).then((code) => process.exit(code));
}
