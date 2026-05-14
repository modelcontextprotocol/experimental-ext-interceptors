using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors.Server;

/// <summary>
/// An <see cref="McpServerInterceptor"/> implementation that invokes a method via reflection,
/// binding parameters from <see cref="InvokeInterceptorRequestParams"/>.
/// </summary>
internal sealed class ReflectionMcpServerInterceptor : McpServerInterceptor
{
    private readonly Interceptor _protocolInterceptor;
    private readonly IReadOnlyList<object> _metadata;
    private readonly MethodInfo _method;
    private readonly object? _target;
    private readonly Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, object?[]> _parameterBinder;
    private readonly Func<object?, ValueTask<InterceptorResult>> _resultConverter;

    private ReflectionMcpServerInterceptor(
        Interceptor protocolInterceptor,
        IReadOnlyList<object> metadata,
        MethodInfo method,
        object? target,
        Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, object?[]> parameterBinder,
        Func<object?, ValueTask<InterceptorResult>> resultConverter)
    {
        _protocolInterceptor = protocolInterceptor;
        _metadata = metadata;
        _method = method;
        _target = target;
        _parameterBinder = parameterBinder;
        _resultConverter = resultConverter;
    }

    public override Interceptor ProtocolInterceptor => _protocolInterceptor;
    public override IReadOnlyList<object> Metadata => _metadata;

    public override async ValueTask<InterceptorResult> InvokeAsync(
        InvokeInterceptorRequestParams request,
        McpServer server,
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
    {
        var args = _parameterBinder(request, server, services, cancellationToken);
        var result = _method.Invoke(_target, args);

        // Handle async methods
        if (result is Task task)
        {
            await task.ConfigureAwait(false);

            // If it's Task<T>, get the result
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                result = taskType.GetProperty("Result")!.GetValue(task);
            }
            else
            {
                result = null;
            }
        }
        else if (result is ValueTask<InterceptorResult> vtResult)
        {
            return await vtResult.ConfigureAwait(false);
        }
        else if (result is ValueTask<ValidationInterceptorResult> vtValidation)
        {
            return await vtValidation.ConfigureAwait(false);
        }
        else if (result is ValueTask<MutationInterceptorResult> vtMutation)
        {
            return await vtMutation.ConfigureAwait(false);
        }
        else if (result is ValueTask<SinkInterceptorResult> vtSink)
        {
            return await vtSink.ConfigureAwait(false);
        }

        return await _resultConverter(result).ConfigureAwait(false);
    }

    internal static new McpServerInterceptor Create(Delegate method, McpServerInterceptorCreateOptions? options = null)
    {
        var methodInfo = method.Method;
        var target = method.Target;
        return Create(methodInfo, target, options);
    }

    internal static McpServerInterceptor Create(MethodInfo method, object? target, McpServerInterceptorCreateOptions? options = null)
    {
        var attr = method.GetCustomAttribute<McpServerInterceptorAttribute>()
            ?? throw new InvalidOperationException($"Method '{method.Name}' does not have [{nameof(McpServerInterceptorAttribute)}].");

        var events = attr.Events?.ToList() ?? [InterceptionEvents.All];
        var hooks = attr.Phase switch
        {
            InterceptorPhase.Both =>
            [
                new InterceptorHook { Events = events, Phase = InterceptorPhase.Request },
                new InterceptorHook { Events = events.ToList(), Phase = InterceptorPhase.Response },
            ],
            _ => new List<InterceptorHook> { new() { Events = events, Phase = attr.Phase } },
        };

        var interceptor = new Interceptor
        {
            Name = attr.Name ?? method.Name,
            Description = attr.Description,
            Type = attr.Type,
            Hooks = hooks,
            Mode = attr.Mode == InterceptorMode.Active ? null : attr.Mode,
            FailOpen = attr.FailOpen ? true : null,
            PriorityHint = attr.PriorityHint,
        };

        // Collect metadata from declaring type and method
        var metadata = new List<object>();
        if (method.DeclaringType is { } declaringType)
        {
            metadata.AddRange(declaringType.GetCustomAttributes(inherit: true));
        }
        metadata.AddRange(method.GetCustomAttributes(inherit: true));

        var parameterBinder = BuildParameterBinder(method);
        var resultConverter = BuildResultConverter(method, attr.Type);

        return new ReflectionMcpServerInterceptor(interceptor, metadata, method, target, parameterBinder, resultConverter);
    }

    private static Func<InvokeInterceptorRequestParams, McpServer, IServiceProvider?, CancellationToken, object?[]> BuildParameterBinder(MethodInfo method)
    {
        var parameters = method.GetParameters();

        return (request, server, services, ct) =>
        {
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramType = param.ParameterType;
                var paramName = param.Name?.ToLowerInvariant();

                if (paramType == typeof(CancellationToken))
                {
                    args[i] = ct;
                }
                else if (paramType == typeof(McpServer))
                {
                    args[i] = server;
                }
                else if (paramType == typeof(IServiceProvider))
                {
                    args[i] = services;
                }
                else if (paramType == typeof(InvokeInterceptorRequestParams))
                {
                    args[i] = request;
                }
                else if (paramType == typeof(InvokeInterceptorContext))
                {
                    args[i] = request.Context;
                }
                else if (paramType == typeof(JsonNode) && paramName == "payload")
                {
                    args[i] = request.Payload;
                }
                else if (paramType == typeof(JsonNode) && paramName == "config")
                {
                    args[i] = request.Config;
                }
                else if (paramType == typeof(string) && (paramName == "event" || paramName == "eventname"))
                {
                    args[i] = request.Event;
                }
                else if (paramType == typeof(InterceptorPhase))
                {
                    args[i] = request.Phase;
                }
                else
                {
                    args[i] = param.HasDefaultValue ? param.DefaultValue : null;
                }
            }
            return args;
        };
    }

    private static Func<object?, ValueTask<InterceptorResult>> BuildResultConverter(MethodInfo method, InterceptorType interceptorType)
    {
        var returnType = method.ReturnType;

        // Unwrap Task<T> or ValueTask<T>
        if (returnType.IsGenericType)
        {
            var genericDef = returnType.GetGenericTypeDefinition();
            if (genericDef == typeof(Task<>) || genericDef == typeof(ValueTask<>))
            {
                returnType = returnType.GetGenericArguments()[0];
            }
        }

        // If return type is already an InterceptorResult subclass, pass through
        if (typeof(InterceptorResult).IsAssignableFrom(returnType))
        {
            return result => new ValueTask<InterceptorResult>((InterceptorResult)result!);
        }

        // If return type is bool, convert to ValidationInterceptorResult
        if (returnType == typeof(bool))
        {
            return result =>
            {
                var valid = (bool)result!;
                return new ValueTask<InterceptorResult>(new ValidationInterceptorResult { Valid = valid });
            };
        }

        // Default: wrap as the appropriate result type based on interceptor type
        return result =>
        {
            InterceptorResult interceptorResult = interceptorType switch
            {
                InterceptorType.Validation => new ValidationInterceptorResult { Valid = true },
                InterceptorType.Sink => new SinkInterceptorResult { Recorded = true },
                _ => throw new InvalidOperationException($"Cannot auto-convert return type '{result?.GetType().Name ?? "null"}' for interceptor type '{interceptorType}'."),
            };
            return new ValueTask<InterceptorResult>(interceptorResult);
        };
    }
}
