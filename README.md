# MCP Interceptors Extension

This repository contains SDK implementations for **SEP-1763: Interceptors for Model Context Protocol** - a standardized framework for intercepting, validating, and transforming MCP messages.

**Specification Reference:** https://github.com/modelcontextprotocol/modelcontextprotocol/issues/1763

## SDK Implementations

| Language | Directory | Status |
|----------|-----------|--------|
| C# | [csharp-sdk/](./csharp-sdk/) | Available |
| Go | `go-sdk/` | Planned |
| TypeScript | `typescript-sdk/` | Planned |

## Overview

MCP Interceptors provide three types of message interception:

- **Validation** - Validates requests/responses, returns pass/fail with severity levels
- **Mutation** - Transforms payloads before they continue through the pipeline
- **Observability** - Fire-and-forget logging/metrics collection, never blocks

Interceptors can operate in different phases:
- **Request** - Intercept incoming requests
- **Response** - Intercept outgoing responses
- **Both** - Intercept in both directions

## License

MIT
