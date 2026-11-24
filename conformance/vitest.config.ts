import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

// Source-consumption aliases, mirroring tsconfig.json `paths`.
export default defineConfig({
  resolve: {
    alias: {
      "@formalcore/mcp-interceptors-sdk": fileURLToPath(
        new URL("../typescript/sdk/src/index.ts", import.meta.url),
      ),
    },
  },
});
