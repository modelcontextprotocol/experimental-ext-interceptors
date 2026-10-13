// Copyright 2025 The MCP Interceptors Authors. All rights reserved.

import { describe, it, expect } from 'vitest';
import { InterceptionEvents } from '../protocol/constants.js';
import { matchesEvent } from './chain-orchestrator.js';

describe('matchesEvent', () => {
  it('matches exact event names', () => {
    expect(matchesEvent([InterceptionEvents.ToolsCall], InterceptionEvents.ToolsCall)).toBe(true);
    expect(matchesEvent([InterceptionEvents.ToolsCall], InterceptionEvents.PromptsGet)).toBe(false);
  });

  it('matches wildcard *', () => {
    expect(matchesEvent([InterceptionEvents.All], InterceptionEvents.ResourcesRead)).toBe(true);
  });
});
