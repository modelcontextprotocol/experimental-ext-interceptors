/**
 * Write the generated fixtures + manifest to disk. Deterministic; running it
 * twice produces identical bytes. Usage:
 *
 *   node --experimental-strip-types scripts/generate.ts
 */
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { generateFiles } from "../src/generate.ts";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");

const files = generateFiles();
for (const file of files) {
  const target = join(ROOT, file.path);
  await mkdir(dirname(target), { recursive: true });
  await writeFile(target, file.contents, "utf8");
}
console.log(`wrote ${files.length} files (${files.length - 1} fixtures + manifest.json)`);
