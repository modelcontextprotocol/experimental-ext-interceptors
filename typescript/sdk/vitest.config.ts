import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

// The attested-validation package is consumed AS SOURCE (it is a strip-types
// package whose exports point at .ts files); alias the package name so vitest
// resolves it the same way tsconfig.json `paths` does for the typechecker.
export default defineConfig({
  resolve: {
    alias: {
      "@formalcore/mcp-attested-validation": fileURLToPath(
        new URL("../attested-validation/src/index.ts", import.meta.url),
      ),
    },
  },
});
