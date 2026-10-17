# MCP Interceptors (Experimental Extension)

> #### **Status:** Experimental. This work is for prototyping and feedback only, and is not an accepted or official MCP extension.

This repository provides a multi-language reference implementation of the proposed interceptor extension for the Model Context Protocol (MCP), as described in [SEP-2624](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624).

**Charter:** [modelcontextprotocol.io/community/working-groups/interceptors](https://modelcontextprotocol.io/community/working-groups/interceptors) - the Interceptors Working Group's mission, scope, leadership, and active work items.

**Meeting notes:** [Meeting Notes - Interceptors WG](https://github.com/modelcontextprotocol/modelcontextprotocol/discussions/categories/meeting-notes-interceptors-wg) in the spec repo. The April 30, 2026 meeting predates that category and is in [this repository's discussions](https://github.com/modelcontextprotocol/experimental-ext-interceptors/discussions), along with copies of the April 23 and May 14 notes.


## Implementations

| Language | Directory | Package | Status |
|----------|-----------|---------|--------|
| C# | `csharp/sdk/` | `ModelContextProtocol.Interceptors` | In Progress |
| Go | `go/sdk/` | `github.com/modelcontextprotocol/ext-interceptors/go/sdk` | Planned |
| Python | `python/sdk/` | `mcp-ext-interceptors` | Planned |
| TypeScript | `typescript/sdk/` | `@ext-modelcontextprotocol/interceptors` | Planned |

## Conformance and reference components

Additional components in this tree address cross-SDK conformance (issue #20).
They are self-contained and independently tested.

| Component | Directory | What it is |
|-----------|-----------|------------|
| Conformance suite | `conformance/` | Language-neutral golden fixtures (generated deterministically from one typed catalog) plus a four-function adapter contract. Any SDK certifies by implementing the adapter and replaying the fixtures. See [`conformance/README.md`](conformance/README.md) and [`conformance/ADAPTER.md`](conformance/ADAPTER.md). |
| Python adapter | `conformance/adapters/python/` | Certifies the `feature/python-sdk` implementation against the shared fixtures, proving the suite is language-neutral in fact. |

### Walkthrough

`scripts/demo.sh` runs two self-verifying beats end to end: the conformance
suite is deterministic and discriminating, and the same fixtures certify the
Python SDK cross-language. Run a single beat with `scripts/demo.sh 1|2`.


## CI/CD

This monorepo uses **path-based CI workflows** to efficiently test only what changes:

### How It Works

1. **Language-specific workflows** (`csharp.yml`, `python.yml`, `go.yml`, `typescript.yml`)
   - Only trigger when their language directory or workflow file changes
   - Run all tests, linting, and checks for that language
   - `conformance.yml` and `integrations.yml` cover the components above on the same path-based model (the conformance job also asserts fixture determinism and that the suite discriminates a permissive implementation)

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

- [Interceptor Framework Specification (SEP-2624)](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/2624) - Full specification and design details
- [Model Context Protocol](https://modelcontextprotocol.io/specification)
- [Interceptors for MCP: A Production-Tested Standard for Agentic Middleware](https://www.youtube.com/watch?v=YOv1d7PVi8U) - Kurt Degiorgio & Cannis Chan (Bloomberg), MCP Dev Summit North America
- [Context Middleware for MCP: From Enterprise Needs To Protocol Extension](https://www.youtube.com/watch?v=-X5lx3IOu7M) - Peder Holdgaard Pedersen, MCP Dev Summit North America
