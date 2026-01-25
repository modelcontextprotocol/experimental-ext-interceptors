# MCP Interceptors

Multi-language implementations of the Model Context Protocol (MCP) interceptor framework.

For the full specification and design details, see: [SEP-1763: Interceptor Framework for Model Context Protocol](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763)

## Implementations

| Language | Directory | Package | Status |
|----------|-----------|---------|--------|
| Go | `go/sdk/` | `github.com/modelcontextprotocol/ext-interceptors/go/sdk` | Planned |
| Python | `python/sdk/` | `mcp-ext-interceptors` | Planned |
| TypeScript | `typescript/sdk/` | `@ext-modelcontextprotocol/interceptors` | Planned |


## CI/CD

This monorepo uses **path-based CI workflows** to efficiently test only what changes:

### How It Works

1. **Language-specific workflows** (`python.yml`, `go.yml`, `typescript.yml`)
   - Only trigger when their language directory or workflow file changes
   - Run all tests, linting, and checks for that language

2. **Status check workflow** (`status-check.yml`)
   - Runs on every PR to verify required checks passed
   - Determines what needs to pass based on which files changed
   - This is the only required check in branch protection

### Examples

- Change `python/sdk/file.py` → Only Python CI runs → PR requires Python checks to pass
- Change both Go and TypeScript files → Both CIs run → PR requires both to pass
- Change only `README.md` → No language CIs run → PR can merge immediately

### Forcing All Checks

To run all language checks regardless of changed files:
- **In a PR**: Comment `/test all` (only works for repo owners/members/collaborators)
- **Manually**: Use GitHub Actions UI or CLI to trigger individual workflows

### Adding New Required Checks

1. **Add your check** to the appropriate language workflow (e.g., `python.yml`):
   ```yaml
   python-security-scan:
     name: "Security Scan"
     runs-on: ubuntu-latest
     steps:
       - name: Run security checks
         run: # your commands here
   ```

2. **Update the status check** in `.github/workflows/status-check.yml`:
   ```javascript
   const requiredChecks = {
     python: [
       'Python CI / Linting',
       'Python CI / Unit Tests (3.10)',
       // ... existing checks ...
       'Python CI / Security Scan'  // ← Add your new check
     ],
   ```

3. **Submit PR** - Your new check is now required for all relevant changes!

## License

Apache License 2.0 - See LICENSE file for details

## Resources

- [Interceptor Framework Specification (SEP-1763)](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763) - Full specification and design details
- [Model Context Protocol](https://modelcontextprotocol.io/specification)
