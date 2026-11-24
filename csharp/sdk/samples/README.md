# Samples

## Start here

1. **[WhyInterceptorsDemo](WhyInterceptorsDemo)**: a short scripted demo. One agent and one MCP
   server, first connected directly and then through a proxy that applies a destination allowlist,
   a redactor and an audit log from an interceptor server. Best for people new to interceptors.

   ```bash
   dotnet run --project samples/WhyInterceptorsDemo
   ```

2. **[InterceptorProxy](InterceptorProxy)**: put interceptors in front of any MCP server and add it
   to Claude Code, Claude Desktop or VS Code like any other server. Configured with a JSON file
   whose `policy` section mirrors the SEP's invoker overrides.

3. **[InterceptorServerSample](InterceptorServerSample)**: the smallest interceptor server, to copy
   when writing your own.

## Library API samples

| Sample | Shows |
| --- | --- |
| [InterceptorClientSample](InterceptorClientSample) | Calling `interceptors/list` and `interceptor/invoke` directly, and running several interceptors with `ExecuteInterceptorsAsync`. |
| [GatewaySample](GatewaySample) | `InterceptingMcpClient`: an agent-side wrapper that runs interceptors around its own calls. |
| [TransparentProxySample](TransparentProxySample) | The minimal `McpInterceptorGateway` setup that InterceptorProxy is built on. |
| [GatewayChainSample](GatewayChainSample) | Gateways stacked in layers, each with its own interceptor server. |
| [ConfigDrivenGatewaySample](ConfigDrivenGatewaySample) | Choosing interceptor servers per request from the caller's identity (`InterceptorServerConnectionResolver`), over Streamable HTTP. |
| [AvatarMoodInterceptorSample](AvatarMoodInterceptorSample) | A **sink** interceptor (proposed in experimental-ext-interceptors#28) that reacts to the conversation without affecting it: a background model classifies mood and animates a console avatar. Needs `ANTHROPIC_API_KEY`. |

Most samples launch their servers with `dotnet run --project` or `npx`, so the first run builds or
downloads them.
