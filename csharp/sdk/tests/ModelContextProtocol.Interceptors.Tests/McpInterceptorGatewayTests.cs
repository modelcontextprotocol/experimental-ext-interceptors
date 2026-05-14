using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

public class McpInterceptorGatewayTests
{
    [Fact]
    public async Task ConfigureServerOptions_MirrorsToolsCapability()
    {
        // Create an in-memory backend with tools capability
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new() { ListChanged = true };
                options.Handlers.ListToolsHandler = (request, ct) =>
                {
                    return new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "test-tool", Description = "A test tool" }],
                    });
                };
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    return new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"Called: {request.Params!.Name}" }],
                    });
                };
            });

        // Verify the proxy client can list tools from the backend
        var tools = await fixture.ProxyClient.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("test-tool", tools[0].Name);
    }

    [Fact]
    public async Task CallToolAsync_PassesThroughInterceptors()
    {
        var interceptorInvoked = false;

        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                {
                    return new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                };
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    return new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"echo: {request.Params!.Arguments?["message"]}" }],
                    });
                };
            },
            interceptors: [CreateMutationInterceptor("test-mutator", (req, _, _, _) =>
            {
                interceptorInvoked = true;
                // Pass through without mutation
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                {
                    Modified = false,
                    Payload = req.Payload,
                });
            })]);

        var result = await fixture.ProxyClient.CallToolAsync("echo",
            new Dictionary<string, object?> { ["message"] = "hello" });

        Assert.True(interceptorInvoked);
        Assert.Contains("echo: hello", result.Content[0].ToString());
    }

    [Fact]
    public async Task CallToolAsync_ValidationAbortBlocksRequest()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                {
                    return new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                };
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    Assert.Fail("Backend should not be called when validation aborts.");
                    return new ValueTask<CallToolResult>(new CallToolResult());
                };
            },
            interceptors: [CreateValidationInterceptor("blocker", (req, _, _, _) =>
            {
                return new ValueTask<InterceptorResult>(
                    ValidationInterceptorResult.Failure(new ValidationMessage
                    {
                        Message = "Blocked!",
                        Severity = ValidationSeverity.Error,
                    }));
            })]);

        // The SDK wraps handler exceptions as CallToolResult { IsError = true }
        // The backend handler's Assert.Fail proves it was never called.
        var result = await fixture.ProxyClient.CallToolAsync("echo",
            new Dictionary<string, object?> { ["message"] = "should be blocked" });
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task CallToolAsync_MutationModifiesPayload()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                {
                    return new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                };
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    // Verify the mutation was applied before reaching the backend
                    var msg = request.Params!.Arguments?["message"];
                    return new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"echo: {msg}" }],
                    });
                };
            },
            interceptors: [CreateMutationInterceptor("email-redactor", (req, _, _, _) =>
            {
                // Simulate redacting emails from the payload
                var payloadStr = req.Payload!.ToJsonString();
                payloadStr = payloadStr.Replace("user@example.com", "[REDACTED]");
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                {
                    Modified = true,
                    Payload = JsonNode.Parse(payloadStr),
                });
            })]);

        var result = await fixture.ProxyClient.CallToolAsync("echo",
            new Dictionary<string, object?> { ["message"] = "Contact user@example.com" });

        Assert.Contains("[REDACTED]", result.Content[0].ToString());
        Assert.DoesNotContain("user@example.com", result.Content[0].ToString());
    }

    [Fact]
    public async Task ConfigureServerOptions_OnlyRegistersAdvertisedCapabilities()
    {
        // Backend only has tools, no prompts/resources
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                {
                    return new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                };
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    return new ValueTask<CallToolResult>(new CallToolResult());
                };
            });

        // Proxy should advertise tools but not prompts/resources
        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.NotNull(caps?.Tools);
        Assert.Null(caps?.Prompts);
        Assert.Null(caps?.Resources);
    }

    [Fact]
    public async Task ConfigureServerOptions_MirrorsBackendExperimentalCapabilities()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Capabilities.Experimental = new Dictionary<string, object>
                {
                    ["com.example/feature"] = JsonSerializer.SerializeToElement(new { enabled = true }),
                };
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            });

        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.NotNull(caps.Experimental);
        Assert.True(caps.Experimental!.ContainsKey("com.example/feature"));
    }

    [Fact]
    public async Task ConfigureServerOptions_DoesNotAdvertiseBackendTasksCapability()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
#pragma warning disable MCPEXP001
                options.Capabilities.Tasks = new McpTasksCapability
                {
                    List = new ListMcpTasksCapability(),
                    Cancel = new CancelMcpTasksCapability(),
                };
#pragma warning restore MCPEXP001
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            });

        var caps = fixture.ProxyClient.ServerCapabilities;
#pragma warning disable MCPEXP001
        Assert.Null(caps.Tasks);
#pragma warning restore MCPEXP001
    }

    [Fact]
    public async Task ConfigureServerOptions_MirrorsBackendExtensionsInTransparentMode()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
#pragma warning disable MCPEXP001
                options.Capabilities.Extensions ??= new Dictionary<string, object>();
                options.Capabilities.Extensions["com.example/backend"] = JsonSerializer.SerializeToElement(new { version = 1 });
#pragma warning restore MCPEXP001
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            });

#pragma warning disable MCPEXP001
        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.NotNull(caps.Extensions);
        Assert.True(caps.Extensions!.ContainsKey("com.example/backend"));
        Assert.False(caps.Extensions.ContainsKey("interceptors"));
#pragma warning restore MCPEXP001
    }

    [Fact]
    public async Task ConfigureServerOptions_DoesNotAdvertiseInterceptorsCapabilityByDefault()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            });

#pragma warning disable MCPEXP001
        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.False(caps?.Extensions?.ContainsKey("interceptors") ?? false);
#pragma warning restore MCPEXP001
    }

    [Fact]
    public async Task ConfigureServerOptions_AdvertisesInterceptorsCapabilityWhenEnabled()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
#pragma warning disable MCPEXP001
                options.Capabilities.Extensions ??= new Dictionary<string, object>();
                options.Capabilities.Extensions["com.example/backend"] = JsonSerializer.SerializeToElement(new { version = 1 });
#pragma warning restore MCPEXP001
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            },
            exposeInterceptorProtocol: true);

#pragma warning disable MCPEXP001
        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.NotNull(caps?.Extensions);
        Assert.True(caps!.Extensions!.ContainsKey("interceptors"));
        Assert.True(caps.Extensions.ContainsKey("com.example/backend"));
#pragma warning restore MCPEXP001
    }

    [Fact]
    public async Task ListInterceptorsAsync_IsNotExposedByDefault()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            },
            interceptors:
            [
                CreateValidationInterceptor("validator-1", (_, _, _, _) =>
                    new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success())),
            ]);

        await Assert.ThrowsAsync<McpProtocolException>(async () =>
        {
            await fixture.ProxyClient.ListInterceptorsAsync();
        });
    }

    [Fact]
    public async Task ListInterceptorsAsync_AggregatesFromInterceptorServersWhenEnabled()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            },
            interceptors:
            [
                CreateValidationInterceptor("validator-1", (_, _, _, _) =>
                    new ValueTask<InterceptorResult>(ValidationInterceptorResult.Success())),
                CreateMutationInterceptor("mutator-1", (req, _, _, _) =>
                    new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false, Payload = req.Payload })),
            ],
            exposeInterceptorProtocol: true);

        // List interceptors through the proxy
        var result = await fixture.ProxyClient.ListInterceptorsAsync();
        Assert.Equal(2, result.Interceptors.Count);
        Assert.Contains(result.Interceptors, i => i.Name == "validator-1");
        Assert.Contains(result.Interceptors, i => i.Name == "mutator-1");
    }

    [Fact]
    public async Task CallToolAsync_ChainsAllInterceptorClients()
    {
        // Set up two interceptor servers, each with one mutation interceptor
        // The first prepends "A:", the second prepends "B:"
        await using var fixture = await GatewayTestFixture.CreateWithMultipleInterceptorServersAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    var msg = request.Params!.Arguments?["message"];
                    return new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"echo: {msg}" }],
                    });
                };
            },
            interceptorConfigs:
            [
                [CreateMutationInterceptor("prepend-a", (req, _, _, _) =>
                {
                    // Prepend "A:" to the message argument
                    var obj = JsonNode.Parse(req.Payload!.ToJsonString())!.AsObject();
                    if (obj["arguments"]?["message"] is JsonNode msgNode)
                    {
                        obj["arguments"]!["message"] = "A:" + msgNode.GetValue<string>();
                    }
                    return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                    {
                        Modified = true,
                        Payload = obj,
                    });
                })],
                [CreateMutationInterceptor("prepend-b", (req, _, _, _) =>
                {
                    var obj = JsonNode.Parse(req.Payload!.ToJsonString())!.AsObject();
                    if (obj["arguments"]?["message"] is JsonNode msgNode)
                    {
                        obj["arguments"]!["message"] = "B:" + msgNode.GetValue<string>();
                    }
                    return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                    {
                        Modified = true,
                        Payload = obj,
                    });
                })],
            ]);

        // Call tool — both interceptors should run (A first, then B)
        var result = await fixture.ProxyClient.CallToolAsync("echo",
            new Dictionary<string, object?> { ["message"] = "hello" });

        // Both interceptor chains ran in order: A prepended first, then B
        var text = result.Content[0].ToString()!;
        Assert.Contains("B:A:hello", text);
    }

    [Fact]
    public async Task SubscribeMutation_UsesModifiedPayload()
    {
        var subscribedUri = "";

        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Resources ??= new() { Subscribe = true };
                options.Handlers.ListResourcesHandler = (request, ct) =>
                    new ValueTask<ListResourcesResult>(new ListResourcesResult { Resources = [] });
                options.Handlers.ReadResourceHandler = (request, ct) =>
                    new ValueTask<ReadResourceResult>(new ReadResourceResult { Contents = [] });
                options.Handlers.ListResourceTemplatesHandler = (request, ct) =>
                    new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult { ResourceTemplates = [] });
                options.Handlers.SubscribeToResourcesHandler = (request, ct) =>
                {
                    subscribedUri = request.Params!.Uri;
                    return new ValueTask<EmptyResult>(new EmptyResult());
                };
                options.Handlers.UnsubscribeFromResourcesHandler = (request, ct) =>
                    new ValueTask<EmptyResult>(new EmptyResult());
            },
            interceptors: [CreateMutationInterceptor("uri-rewriter", (req, _, _, _) =>
            {
                // Mutate the subscribe payload to rewrite the URI
                var payloadStr = req.Payload!.ToJsonString();
                payloadStr = payloadStr.Replace("resource://original", "resource://rewritten");
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                {
                    Modified = true,
                    Payload = JsonNode.Parse(payloadStr),
                });
            }, events: [InterceptionEvents.ResourcesSubscribe])]);

        await fixture.ProxyClient.SubscribeToResourceAsync("resource://original");

        // The mutation interceptor should have rewritten the URI before it reached the backend
        Assert.Equal("resource://rewritten", subscribedUri);
    }

    [Fact]
    public async Task ExecuteChain_FailsWhenInterceptorProtocolNotExposed()
    {
        await using var fixture = await GatewayTestFixture.CreateWithMultipleInterceptorServersAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            },
            interceptorConfigs:
            [
                [CreateMutationInterceptor("mutator-a", (req, _, _, _) =>
                    new ValueTask<InterceptorResult>(new MutationInterceptorResult { Modified = false, Payload = req.Payload }))],
            ]);

        await Assert.ThrowsAsync<McpProtocolException>(async () =>
        {
            await fixture.ProxyClient.ExecuteChainAsync(
                new ExecuteChainRequestParams
                {
                    Event = InterceptionEvents.ToolsCall,
                    Phase = InterceptorPhase.Request,
                    Payload = JsonNode.Parse("""{"original":true}""")!,
                });
        });
    }

    [Fact]
    public async Task ExecuteChain_AggregatesResultsAcrossInterceptorClientsViaGateway()
    {
        // Two interceptor servers, each with one mutation interceptor
        await using var fixture = await GatewayTestFixture.CreateWithMultipleInterceptorServersAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult());
            },
            interceptorConfigs:
            [
                [CreateMutationInterceptor("mutator-a", (req, _, _, _) =>
                {
                    var obj = JsonNode.Parse(req.Payload!.ToJsonString())!.AsObject();
                    obj["a"] = true;
                    return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                    {
                        Modified = true,
                        Payload = obj,
                    });
                })],
                [CreateMutationInterceptor("mutator-b", (req, _, _, _) =>
                {
                    var obj = JsonNode.Parse(req.Payload!.ToJsonString())!.AsObject();
                    obj["b"] = true;
                    return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                    {
                        Modified = true,
                        Payload = obj,
                    });
                })],
            ],
            exposeInterceptorProtocol: true);

        // Client-side ExecuteChainAsync does list+invoke against the proxy; with
        // ExposeInterceptorProtocol enabled, the proxy aggregates list and routes invoke
        // to the appropriate interceptor client behind it.
        var chainResult = await fixture.ProxyClient.ExecuteChainAsync(
            new ExecuteChainRequestParams
            {
                Event = InterceptionEvents.ToolsCall,
                Phase = InterceptorPhase.Request,
                Payload = JsonNode.Parse("""{"original":true}""")!,
            });

        Assert.Equal(InterceptorChainStatus.Success, chainResult.Status);
        // Results from both interceptor servers should be aggregated
        Assert.Equal(2, chainResult.Results.Count);
        // Final payload should have mutations from both servers
        Assert.True(chainResult.FinalPayload!["a"]!.GetValue<bool>());
        Assert.True(chainResult.FinalPayload!["b"]!.GetValue<bool>());
        Assert.True(chainResult.FinalPayload!["original"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CallToolAsync_MutationFailureBlocksRequest()
    {
        var backendCalled = false;

        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    backendCalled = true;
                    return new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "backend-called" }],
                    });
                };
            },
            interceptors:
            [
                CreateMutationInterceptor("failing-mutator", (_, _, _, _) =>
                {
                    throw new InvalidOperationException("PII redaction failed");
                }),
            ]);

        var result = await fixture.ProxyClient.CallToolAsync("echo");

        Assert.False(backendCalled);
        Assert.True(result.IsError);
        Assert.Contains("An error occurred invoking 'echo'.", result.Content[0].ToString());
    }

    [Fact]
    public async Task CallToolAsync_MutationTimeoutBlocksRequest()
    {
        var backendCalled = false;

        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                options.Handlers.CallToolHandler = (request, ct) =>
                {
                    backendCalled = true;
                    return new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "backend-called" }],
                    });
                };
            },
            interceptors:
            [
                CreateMutationInterceptor("slow-mutator", async (_, _, _, ct) =>
                {
                    await Task.Delay(200, ct);
                    return new MutationInterceptorResult { Modified = false };
                }),
            ],
            timeoutMs: 25);

        var result = await fixture.ProxyClient.CallToolAsync("echo");

        Assert.False(backendCalled);
        Assert.True(result.IsError);
        Assert.Contains("An error occurred invoking 'echo'.", result.Content[0].ToString());
    }

    [Fact]
    public async Task WithInterceptorGateway_RegistersNotificationForwardingOncePerSession()
    {
        var forwardingRegistrations = 0;

        await using var fixture = await GatewayTestFixture.CreateWithBuilderGatewayAsync(
            backendConfigure: options =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new() { ListChanged = true };
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "ok" }],
                    });
            },
            onRegisterNotificationHandler: method =>
            {
                if (method == NotificationMethods.ToolListChangedNotification)
                {
                    Interlocked.Increment(ref forwardingRegistrations);
                }
            });

        await fixture.ProxyClient.ListToolsAsync();
        await fixture.ProxyClient.ListToolsAsync();
        await fixture.ProxyClient.CallToolAsync("echo");

        Assert.Equal(1, forwardingRegistrations);
    }

    [Fact]
    public async Task WithInterceptorGateway_ServiceProviderFactoryOverloadConfiguresGateway()
    {
        await using var fixture = await GatewayTestFixture.CreateWithBuilderGatewayFactoryAsync(
            backendConfigure: options =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Tools ??= new();
                options.Handlers.ListToolsHandler = (request, ct) =>
                    new ValueTask<ListToolsResult>(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "echo", Description = "Echo" }],
                    });
                options.Handlers.CallToolHandler = (request, ct) =>
                    new ValueTask<CallToolResult>(new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = request.Params!.Name }],
                    });
            },
            exposeInterceptorProtocol: true);

#pragma warning disable MCPEXP001
        Assert.True(fixture.ProxyClient.ServerCapabilities.Extensions?.ContainsKey(InterceptorProtocolConstants.ExtensionCapabilityKey) ?? false);
#pragma warning restore MCPEXP001

        var tools = await fixture.ProxyClient.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("echo", tools[0].Name);
    }

    [Fact]
    public async Task ListPromptsAsync_ProxiesToBackend()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Prompts ??= new();
                options.Handlers.ListPromptsHandler = (request, ct) =>
                    new ValueTask<ListPromptsResult>(new ListPromptsResult
                    {
                        Prompts = [new Prompt { Name = "test-prompt", Description = "A test prompt" }],
                    });
                options.Handlers.GetPromptHandler = (request, ct) =>
                    new ValueTask<GetPromptResult>(new GetPromptResult
                    {
                        Messages = [new PromptMessage
                        {
                            Role = Role.User,
                            Content = new TextContentBlock { Text = $"Prompt: {request.Params!.Name}" },
                        }],
                    });
            });

        var prompts = await fixture.ProxyClient.ListPromptsAsync();
        Assert.Single(prompts);
        Assert.Equal("test-prompt", prompts[0].Name);
    }

    [Fact]
    public async Task GetPromptAsync_ProxiesToBackendWithInterception()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Prompts ??= new();
                options.Handlers.ListPromptsHandler = (request, ct) =>
                    new ValueTask<ListPromptsResult>(new ListPromptsResult { Prompts = [] });
                options.Handlers.GetPromptHandler = (request, ct) =>
                    new ValueTask<GetPromptResult>(new GetPromptResult
                    {
                        Messages = [new PromptMessage
                        {
                            Role = Role.User,
                            Content = new TextContentBlock { Text = $"Hello from {request.Params!.Name}" },
                        }],
                    });
            },
            interceptors: [CreateMutationInterceptor("prompt-mutator", (req, _, _, _) =>
            {
                // Pass through — just verify the interceptor is invoked
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                {
                    Modified = false,
                    Payload = req.Payload,
                });
            }, events: [InterceptionEvents.PromptsGet])]);

        var result = await fixture.ProxyClient.GetPromptAsync("my-prompt");
        Assert.Single(result.Messages);
        Assert.Contains("Hello from my-prompt", result.Messages[0].Content.ToString());
    }

    [Fact]
    public async Task ListResourcesAsync_ProxiesToBackend()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Resources ??= new();
                options.Handlers.ListResourcesHandler = (request, ct) =>
                    new ValueTask<ListResourcesResult>(new ListResourcesResult
                    {
                        Resources = [new Resource { Name = "test-resource", Uri = "resource://test" }],
                    });
                options.Handlers.ReadResourceHandler = (request, ct) =>
                    new ValueTask<ReadResourceResult>(new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Text = "content", Uri = request.Params!.Uri, MimeType = "text/plain" }],
                    });
                options.Handlers.ListResourceTemplatesHandler = (request, ct) =>
                    new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult { ResourceTemplates = [] });
            });

        var resources = await fixture.ProxyClient.ListResourcesAsync();
        Assert.Single(resources);
        Assert.Equal("test-resource", resources[0].Name);
    }

    [Fact]
    public async Task ReadResourceAsync_ProxiesToBackendWithInterception()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Resources ??= new();
                options.Handlers.ListResourcesHandler = (request, ct) =>
                    new ValueTask<ListResourcesResult>(new ListResourcesResult { Resources = [] });
                options.Handlers.ReadResourceHandler = (request, ct) =>
                    new ValueTask<ReadResourceResult>(new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Text = $"Content of {request.Params!.Uri}", Uri = request.Params.Uri, MimeType = "text/plain" }],
                    });
                options.Handlers.ListResourceTemplatesHandler = (request, ct) =>
                    new ValueTask<ListResourceTemplatesResult>(new ListResourceTemplatesResult { ResourceTemplates = [] });
            },
            interceptors: [CreateMutationInterceptor("uri-rewriter", (req, _, _, _) =>
            {
                var payloadStr = req.Payload!.ToJsonString();
                payloadStr = payloadStr.Replace("resource://original", "resource://rewritten");
                return new ValueTask<InterceptorResult>(new MutationInterceptorResult
                {
                    Modified = true,
                    Payload = JsonNode.Parse(payloadStr),
                });
            }, events: [InterceptionEvents.ResourcesRead])]);

        var result = await fixture.ProxyClient.ReadResourceAsync("resource://original");
        Assert.Single(result.Contents);
        Assert.Contains("Content of resource://rewritten", ((TextResourceContents)result.Contents[0]).Text);
    }

    [Fact]
    public async Task CompleteAsync_ProxiesToBackend()
    {
        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Completions ??= new();
                options.Handlers.CompleteHandler = (request, ct) =>
                    new ValueTask<CompleteResult>(new CompleteResult
                    {
                        Completion = new Completion { Values = ["alpha", "beta"], HasMore = false },
                    });
            });

        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.NotNull(caps?.Completions);

        var result = await fixture.ProxyClient.CompleteAsync(new CompleteRequestParams
        {
            Ref = new PromptReference { Name = "test" },
            Argument = new Argument { Name = "arg", Value = "a" },
        });
        Assert.Equal(2, result.Completion.Values.Count);
        Assert.Contains("alpha", result.Completion.Values);
    }

    [Fact]
    public async Task SetLoggingLevelAsync_ProxiesToBackend()
    {
        LoggingLevel? receivedLevel = null;

        await using var fixture = await GatewayTestFixture.CreateAsync(
            backendConfigure: (options) =>
            {
                options.Capabilities ??= new();
                options.Capabilities.Logging ??= new();
                options.Handlers.SetLoggingLevelHandler = (request, ct) =>
                {
                    receivedLevel = request.Params!.Level;
                    return new ValueTask<EmptyResult>(new EmptyResult());
                };
            });

        var caps = fixture.ProxyClient.ServerCapabilities;
        Assert.NotNull(caps?.Logging);

        await fixture.ProxyClient.SetLoggingLevelAsync(LoggingLevel.Warning);
        Assert.Equal(LoggingLevel.Warning, receivedLevel);
    }

    // ── Test helpers ──────────────────────────────────────────────────

    private static McpServerInterceptor CreateMutationInterceptor(
        string name,
        Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> handler,
        string[]? events = null)
    {
        var ev = events ?? [InterceptionEvents.All];
        return new TestInterceptor(
            new Interceptor
            {
                Name = name,
                Type = InterceptorType.Mutation,
                Hooks =
                [
                    new InterceptorHook { Events = ev.ToList(), Phase = InterceptorPhase.Request },
                    new InterceptorHook { Events = ev.ToList(), Phase = InterceptorPhase.Response },
                ],
            },
            handler);
    }

    private static McpServerInterceptor CreateValidationInterceptor(
        string name,
        Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> handler)
    {
        return new TestInterceptor(
            new Interceptor
            {
                Name = name,
                Type = InterceptorType.Validation,
                Hooks =
                [
                    new InterceptorHook { Events = [InterceptionEvents.All], Phase = InterceptorPhase.Request },
                    new InterceptorHook { Events = [InterceptionEvents.All], Phase = InterceptorPhase.Response },
                ],
            },
            handler);
    }

    private sealed class TestInterceptor : McpServerInterceptor
    {
        private readonly Interceptor _interceptor;
        private readonly Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> _handler;

        public TestInterceptor(
            Interceptor interceptor,
            Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, ValueTask<InterceptorResult>> handler)
        {
            _interceptor = interceptor;
            _handler = handler;
        }

        public override Interceptor ProtocolInterceptor => _interceptor;
        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<InterceptorResult> InvokeAsync(
            InvokeInterceptorRequestParams request,
            McpServer server,
            IServiceProvider? services,
            CancellationToken cancellationToken = default) =>
            _handler(request, server, services, cancellationToken);
    }

    /// <summary>
    /// Creates an in-memory test fixture with a backend server, interceptor server, and proxy server,
    /// all connected via anonymous pipes.
    /// </summary>
    internal sealed class GatewayTestFixture : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _disposables;

        public McpClient ProxyClient { get; }

        private GatewayTestFixture(McpClient proxyClient, List<IAsyncDisposable> disposables)
        {
            ProxyClient = proxyClient;
            _disposables = disposables;
        }

        public static async Task<GatewayTestFixture> CreateAsync(
            Action<McpServerOptions> backendConfigure,
            McpServerInterceptor[]? interceptors = null,
            int? timeoutMs = null,
            bool exposeInterceptorProtocol = false)
        {
            var disposables = new List<IAsyncDisposable>();

            try
            {
                // 1. Create backend server + client via pipes
                var (backendServer, backendClient) = await CreateServerClientPair(
                    "test-backend",
                    backendConfigure);
                disposables.Add(backendServer);
                disposables.Add(backendClient);

                // 2. Create interceptor server + client via pipes
                var (interceptorServer, interceptorClient) = await CreateServerClientPair(
                    "test-interceptors",
                    options =>
                    {
                        var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
                        var allEvents = new HashSet<string>();

                        foreach (var interceptor in interceptors ?? [])
                        {
                            collection.Add(interceptor);
                            foreach (var hook in interceptor.ProtocolInterceptor.Hooks)
                            {
                                foreach (var ev in hook.Events)
                                {
                                    allEvents.Add(ev);
                                }
                            }
                        }

                        var filter = new InterceptorMessageFilter(collection);
                        options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);

                        options.Capabilities ??= new();
#pragma warning disable MCPEXP001
                        options.Capabilities.Extensions ??= new Dictionary<string, object>();
                        options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                            new InterceptorsCapability { SupportedEvents = allEvents.ToList() },
                            InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
                    });
                disposables.Add(interceptorServer);
                disposables.Add(interceptorClient);

                // 3. Create the gateway
                var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
                {
                    BackendClient = backendClient,
                    InterceptorClients = [interceptorClient],
                    TimeoutMs = timeoutMs,
                    ExposeInterceptorProtocol = exposeInterceptorProtocol,
                });
                disposables.Add(gateway);

                // 4. Create proxy server + client via pipes
                var (proxyServer, proxyClient) = await CreateServerClientPair(
                    "test-proxy",
                    options =>
                    {
                        gateway.ConfigureServerOptions(options);
                    });
                disposables.Add(proxyServer);
                disposables.Add(proxyClient);

                gateway.RegisterNotificationForwarding(proxyServer);

                return new GatewayTestFixture(proxyClient, disposables);
            }
            catch
            {
                foreach (var d in disposables)
                {
                    await d.DisposeAsync();
                }

                throw;
            }
        }

        public static async Task<GatewayTestFixture> CreateWithMultipleInterceptorServersAsync(
            Action<McpServerOptions> backendConfigure,
            McpServerInterceptor[][] interceptorConfigs,
            bool exposeInterceptorProtocol = false)
        {
            var disposables = new List<IAsyncDisposable>();

            try
            {
                var (backendServer, backendClient) = await CreateServerClientPair(
                    "test-backend", backendConfigure);
                disposables.Add(backendServer);
                disposables.Add(backendClient);

                var interceptorClients = new List<McpClient>();
                for (int i = 0; i < interceptorConfigs.Length; i++)
                {
                    var interceptors = interceptorConfigs[i];
                    var (server, client) = await CreateServerClientPair(
                        $"test-interceptors-{i}",
                        options =>
                        {
                            var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
                            var allEvents = new HashSet<string>();
                            foreach (var interceptor in interceptors)
                            {
                                collection.Add(interceptor);
                                foreach (var hook in interceptor.ProtocolInterceptor.Hooks)
                                    foreach (var ev in hook.Events)
                                        allEvents.Add(ev);
                            }

                            var filter = new InterceptorMessageFilter(collection);
                            options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);
                            options.Capabilities ??= new();
#pragma warning disable MCPEXP001
                            options.Capabilities.Extensions ??= new Dictionary<string, object>();
                            options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                                new InterceptorsCapability { SupportedEvents = allEvents.ToList() },
                                InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
                        });
                    disposables.Add(server);
                    disposables.Add(client);
                    interceptorClients.Add(client);
                }

                var gateway = new McpInterceptorGateway(new McpInterceptorGatewayOptions
                {
                    BackendClient = backendClient,
                    InterceptorClients = interceptorClients,
                    ExposeInterceptorProtocol = exposeInterceptorProtocol,
                });
                disposables.Add(gateway);

                var (proxyServer, proxyClient) = await CreateServerClientPair(
                    "test-proxy",
                    options => gateway.ConfigureServerOptions(options));
                disposables.Add(proxyServer);
                disposables.Add(proxyClient);

                gateway.RegisterNotificationForwarding(proxyServer);

                return new GatewayTestFixture(proxyClient, disposables);
            }
            catch
            {
                foreach (var d in disposables)
                    await d.DisposeAsync();
                throw;
            }
        }

        public static async Task<GatewayTestFixture> CreateWithBuilderGatewayAsync(
            Action<McpServerOptions> backendConfigure,
            Action<string>? onRegisterNotificationHandler = null,
            bool exposeInterceptorProtocol = false)
        {
            var disposables = new List<IAsyncDisposable>();

            try
            {
                var (backendServer, backendClientInner) = await CreateServerClientPair(
                    "test-backend",
                    backendConfigure,
                    onRegisterNotificationHandler);
                var backendClient = onRegisterNotificationHandler is null
                    ? backendClientInner
                    : new NotificationTrackingClient(backendClientInner, onRegisterNotificationHandler);
                disposables.Add(backendServer);
                disposables.Add(backendClient);

                var (interceptorServer, interceptorClient) = await CreateServerClientPair(
                    "test-interceptors",
                    options =>
                    {
                        var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
                        var filter = new InterceptorMessageFilter(collection);
                        options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);

                        options.Capabilities ??= new();
#pragma warning disable MCPEXP001
                        options.Capabilities.Extensions ??= new Dictionary<string, object>();
                        options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                            new InterceptorsCapability { SupportedEvents = [] },
                            InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
                    });
                disposables.Add(interceptorServer);
                disposables.Add(interceptorClient);

                var services = new ServiceCollection();
                services.AddMcpServer()
                    .WithInterceptorGateway(new McpInterceptorGatewayOptions
                    {
                        BackendClient = backendClient,
                        InterceptorClients = [interceptorClient],
                        ExposeInterceptorProtocol = exposeInterceptorProtocol,
                    });

                var optionsDescriptor = services.Single(d => d.ServiceType == typeof(McpInterceptorGatewayOptions));
                var gatewayOptions = optionsDescriptor.ImplementationFactory is not null
                    ? (McpInterceptorGatewayOptions)optionsDescriptor.ImplementationFactory(EmptyServiceProvider.Instance)!
                    : optionsDescriptor.ImplementationInstance as McpInterceptorGatewayOptions
                        ?? throw new InvalidOperationException("Gateway options were not registered.");
                var gateway = new McpInterceptorGateway(gatewayOptions);
                disposables.Add(gateway);

                var serverOptions = new McpServerOptions();
                new TestGatewayServerOptionsSetup(gateway).Configure(serverOptions);
                var (proxyServer, proxyClient) = await CreateServerClientPair(
                    "test-proxy",
                    options =>
                    {
                        options.ServerInfo = serverOptions.ServerInfo;
                        options.Capabilities = serverOptions.Capabilities;
                        options.Handlers = serverOptions.Handlers;
                        options.Filters = serverOptions.Filters;
                    });
                disposables.Add(proxyServer);
                disposables.Add(proxyClient);

                return new GatewayTestFixture(proxyClient, disposables);
            }
            catch
            {
                foreach (var d in disposables)
                {
                    await d.DisposeAsync();
                }

                throw;
            }
        }

        public static async Task<GatewayTestFixture> CreateWithBuilderGatewayFactoryAsync(
            Action<McpServerOptions> backendConfigure,
            bool exposeInterceptorProtocol = false)
        {
            var disposables = new List<IAsyncDisposable>();

            try
            {
                var (backendServer, backendClient) = await CreateServerClientPair(
                    "test-backend",
                    backendConfigure);
                disposables.Add(backendServer);
                disposables.Add(backendClient);

                var (interceptorServer, interceptorClient) = await CreateServerClientPair(
                    "test-interceptors",
                    options =>
                    {
                        var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
                        var filter = new InterceptorMessageFilter(collection);
                        options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);

                        options.Capabilities ??= new();
#pragma warning disable MCPEXP001
                        options.Capabilities.Extensions ??= new Dictionary<string, object>();
                        options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
                            new InterceptorsCapability { SupportedEvents = [] },
                            InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
                    });
                disposables.Add(interceptorServer);
                disposables.Add(interceptorClient);

                var services = new ServiceCollection();
                services.AddSingleton(new NamedClient("backend", backendClient));
                services.AddSingleton(new NamedClient("interceptor", interceptorClient));
                services.AddMcpServer()
                    .WithInterceptorGateway(sp => new McpInterceptorGatewayOptions
                    {
                        BackendClient = sp.GetServices<NamedClient>().Single(c => c.Name == "backend").Client,
                        InterceptorClients = [sp.GetServices<NamedClient>().Single(c => c.Name == "interceptor").Client],
                        ExposeInterceptorProtocol = exposeInterceptorProtocol,
                    });

                var serviceProvider = new TestServiceProvider(
                    new NamedClient("backend", backendClient),
                    new NamedClient("interceptor", interceptorClient));
                var optionsDescriptor = services.Single(d => d.ServiceType == typeof(McpInterceptorGatewayOptions));
                var serverOptions = new McpServerOptions();
                var gatewayOptions = optionsDescriptor.ImplementationFactory is not null
                    ? (McpInterceptorGatewayOptions)optionsDescriptor.ImplementationFactory(serviceProvider)!
                    : optionsDescriptor.ImplementationInstance as McpInterceptorGatewayOptions
                        ?? throw new InvalidOperationException("Gateway options were not registered.");
                var gateway = new McpInterceptorGateway(gatewayOptions);
                disposables.Add(gateway);
                new TestGatewayServerOptionsSetup(gateway).Configure(serverOptions);

                var (proxyServer, proxyClient) = await CreateServerClientPair(
                    "test-proxy",
                    options =>
                    {
                        options.ServerInfo = serverOptions.ServerInfo;
                        options.Capabilities = serverOptions.Capabilities;
                        options.Handlers = serverOptions.Handlers;
                        options.Filters = serverOptions.Filters;
                    });
                disposables.Add(proxyServer);
                disposables.Add(proxyClient);

                return new GatewayTestFixture(proxyClient, disposables);
            }
            catch
            {
                foreach (var d in disposables)
                {
                    await d.DisposeAsync();
                }

                throw;
            }
        }

        internal static async Task<(McpServer server, McpClient client)> CreateServerClientPairForTesting(
            string name,
            Action<McpServerOptions> configure,
            Action<string>? onRegisterNotificationHandler = null) =>
            await CreateServerClientPair(name, configure, onRegisterNotificationHandler);

        private static async Task<(McpServer server, McpClient client)> CreateServerClientPair(
            string name,
            Action<McpServerOptions> configure,
            Action<string>? onRegisterNotificationHandler = null)
        {
            // Create anonymous pipes for communication
            var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
            var serverFromClient = new AnonymousPipeClientStream(PipeDirection.In,
                clientToServer.GetClientHandleAsString());

            var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
            var clientFromServer = new AnonymousPipeClientStream(PipeDirection.In,
                serverToClient.GetClientHandleAsString());

            var serverOptions = new McpServerOptions
            {
                ServerInfo = new() { Name = name, Version = "1.0.0" },
            };
            configure(serverOptions);

            // Server reads from serverFromClient, writes to serverToClient
            var serverTransport = new StreamServerTransport(serverFromClient, serverToClient);
            var server = McpServer.Create(serverTransport, serverOptions);
            _ = server.RunAsync(); // Run in background

            // Client writes to clientToServer, reads from clientFromServer
            var clientTransport = new StreamClientTransport(clientToServer, clientFromServer);
            var client = await McpClient.CreateAsync(clientTransport);

            return (server, client);
        }

        public async ValueTask DisposeAsync()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    await _disposables[i].DisposeAsync();
                }
                catch
                {
                    // Swallow disposal errors in tests
                }
            }
        }

#pragma warning disable MCPEXP002
        private sealed class NotificationTrackingClient(McpClient inner, Action<string> onRegister) : McpClient
#pragma warning restore MCPEXP002
        {
            public override string? SessionId => inner.SessionId;
            public override string? NegotiatedProtocolVersion => inner.NegotiatedProtocolVersion;
            public override ServerCapabilities ServerCapabilities => inner.ServerCapabilities;
            public override Implementation ServerInfo => inner.ServerInfo;
            public override string? ServerInstructions => inner.ServerInstructions;
            public override Task<ClientCompletionDetails> Completion => inner.Completion;

            public override ValueTask DisposeAsync() => inner.DisposeAsync();

            public override IAsyncDisposable RegisterNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
            {
                onRegister(method);
                return inner.RegisterNotificationHandler(method, handler);
            }

            public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
                inner.SendMessageAsync(message, cancellationToken);
            public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) =>
                inner.SendRequestAsync(request, cancellationToken);
        }

        private sealed record NamedClient(string Name, McpClient Client);

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            internal static EmptyServiceProvider Instance { get; } = new();

            public object? GetService(Type serviceType) => null;
        }

        private sealed class TestGatewayServerOptionsSetup(McpInterceptorGateway gateway) : IConfigureOptions<McpServerOptions>
        {
            private readonly GatewayConnectionForwardingRegistrar _forwardingRegistrar = new(gateway);

            public void Configure(McpServerOptions options)
            {
                gateway.ConfigureServerOptions(options);
                _forwardingRegistrar.Configure(options);
            }
        }

        private sealed class TestServiceProvider(params NamedClient[] clients) : IServiceProvider
        {
            private readonly NamedClient[] _clients = clients;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IEnumerable<NamedClient>))
                {
                    return _clients;
                }

                if (serviceType == typeof(NamedClient[]))
                {
                    return _clients;
                }

                if (serviceType == typeof(IEnumerable))
                {
                    return _clients;
                }

                return null;
            }
        }

    }
}
