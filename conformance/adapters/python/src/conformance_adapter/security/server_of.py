# Copyright 2025 The MCP Interceptors Authors. All rights reserved.
# Use of this source code is governed by an Apache-2.0
# license that can be found in the LICENSE file.

"""Deriving the "server" (trust boundary) an event belongs to - the OPEN
tier's heuristic, ported from
`typescript/sdk/src/samples/security/server-of.ts`.

Tool name → owning server via a small MCPHunt-server catalog; resource URI →
authority (or scheme); otherwise the event's namespace, then ``unknown``.
Real per-request server identity is a gateway/host concern.
"""

from __future__ import annotations

import re
from typing import Any

#: MCPHunt's server roster: tool name → owning server.
TOOL_SERVER: dict[str, str] = {
    "read_file": "filesystem",
    "write_file": "filesystem",
    "move_file": "filesystem",
    "list_directory": "filesystem",
    "search_files": "filesystem",
    "read_query": "sqlite",
    "write_query": "sqlite",
    "list_tables": "sqlite",
    "describe_table": "sqlite",
    "git_log": "git",
    "git_show": "git",
    "git_diff_unstaged": "git",
    "read_graph": "memory",
    "create_entities": "memory",
    "add_observations": "memory",
    "fetch": "fetch",
    "browser_type": "browser",
    "browser_fill_form": "browser",
    "browser_snapshot": "browser",
    "execute_command": "shell",
}

_UNKNOWN_SERVER = "unknown"

_AUTHORITY = re.compile(r"^[a-z][a-z0-9+.-]*://([^/]+)", re.IGNORECASE)
_SCHEME = re.compile(r"^([a-z][a-z0-9+.-]*):", re.IGNORECASE)


def _as_record(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _as_str(value: Any) -> str | None:
    return value if isinstance(value, str) else None


def _tool_name(payload: dict[str, Any]) -> str | None:
    """The tool name inside a tools/call payload, wrapped in ``params`` or flat."""
    params = _as_record(payload.get("params"))
    name = _as_str(params.get("name"))
    return name if name is not None else _as_str(payload.get("name"))


def _resource_uri(payload: dict[str, Any]) -> str | None:
    params = _as_record(payload.get("params"))
    uri = _as_str(params.get("uri"))
    return uri if uri is not None else _as_str(payload.get("uri"))


def server_of(event: str, payload: Any) -> str:
    """The server an event crosses. Tool → server via the catalog; resource →
    URI authority (or scheme); otherwise the event's namespace, then
    ``unknown``."""
    record = _as_record(payload)

    tool = _tool_name(record)
    if tool is not None:
        return TOOL_SERVER.get(tool, tool)

    uri = _resource_uri(record)
    if uri is not None:
        authority = _AUTHORITY.match(uri)
        if authority is not None:
            return authority.group(1)
        scheme = _SCHEME.match(uri)
        if scheme is not None:
            return scheme.group(1)

    namespace = event.split("/")[0]
    return namespace if namespace else _UNKNOWN_SERVER
