import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

// One config for both `vite-node` (the demo) and `vitest` (the tests). The
// reference SDK and the attestation package are consumed as TypeScript source
// via these aliases, mirroring tsconfig.json `paths`, so no build step is
// needed to run the demo or the tests.
export default defineConfig({
  resolve: {
    alias: {
      "@formalcore/mcp-interceptors-sdk": fileURLToPath(
        new URL("../../sdk/src/index.ts", import.meta.url),
      ),
      "@formalcore/mcp-attested-validation": fileURLToPath(
        new URL("../../attested-validation/src/index.ts", import.meta.url),
      ),
    },
  },
  test: {
    // Pin the project root and test glob to THIS package, so the suite never
    // discovers unrelated packages elsewhere in the monorepo.
    root: fileURLToPath(new URL(".", import.meta.url)),
    include: ["tests/**/*.test.ts"],
  },
});
