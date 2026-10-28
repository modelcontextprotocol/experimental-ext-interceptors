# MCP Interceptors TypeScript SDK

TypeScript implementation of the Model Context Protocol (MCP) interceptor framework.

## Installation

```bash
npm install @ext-modelcontextprotocol/interceptors
```

## Usage

```typescript
import { Interceptor } from '@ext-modelcontextprotocol/interceptors';

// The MCP SDK is available as a peer dependency
import { Client, Server } from '@modelcontextprotocol/sdk';

// Example usage will be added as the implementation progresses
```

### Requiring confirmation

A validator can block a call unless a human approves it. The request is
bound to the exact `{ event, phase, target, payload }`, so an approval
cannot be replayed for a different call:

```typescript
import {
  isConfirmedCall,
  MemoryUsedConfirmations,
  requireConfirmation,
  runValidationPhase,
} from '@ext-modelcontextprotocol/interceptors';

// Validator: passes only with a valid confirmation for this exact call, once.
const used = new MemoryUsedConfirmations();
const guard = {
  name: 'public-write-guard',
  invoke: async (params) => requireConfirmation(params, { reason: 'Post to a public repo?', secret, used }),
};

// Invoker: asks the person (e.g. via elicitation) and re-validates.
// Without `confirm`, the request simply blocks.
const outcome = await runValidationPhase({
  event: 'tools/call',
  phase: 'request',
  payload,
  target: 'github',
  validators: [guard],
  confirm: async (prompt, signal) => ({ approved: await askUser(prompt, signal) }),
});

// Denials, timeouts, failures and plain blocks all come back as not allowed.
if (!outcome.allowed) {
  // block with outcome.reason
}

// After any later mutations, the payload must still match what was approved.
if (outcome.confirmedCallHash && !isConfirmedCall(finalCall, outcome.confirmedCallHash)) {
  // block
}
```

A confirmation is single use. Invokers must not attach it to a second
request; with `used`, the validator enforces that too, so a retry asks for a
new confirmation instead of reusing the old one. `MemoryUsedConfirmations`
keeps accepted ids until they expire; validators in several processes need a
shared `UsedConfirmations` store.

## Development

```bash
# Install dependencies
npm install

# Build
npm run build

# Run tests
npm test

# Lint
npm run lint
```

## License

Apache License 2.0 - See LICENSE file in the root directory for details.
