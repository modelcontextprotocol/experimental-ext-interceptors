using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Interceptors.Protocol;
using ModelContextProtocol.Interceptors.Server;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Interceptors;

/// <summary>
/// Extension methods for <see cref="IMcpServerBuilder"/> to register interceptors.
/// </summary>
public static class McpServerInterceptorBuilderExtensions
{
    /// <summary>
    /// Registers interceptors from all methods on <typeparamref name="T"/> that are decorated
    /// with <see cref="McpServerInterceptorAttribute"/>.
    /// </summary>
    public static IMcpServerBuilder WithInterceptors<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] T>(
        this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EnsureSetupRegistered(builder.Services);

        foreach (var method in typeof(T).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<McpServerInterceptorAttribute>() is not null)
            {
                McpServerInterceptor interceptor;
                if (method.IsStatic)
                {
                    interceptor = ReflectionMcpServerInterceptor.Create(method, target: null);
                }
                else
                {
                    // For instance methods, we need a factory. Register the type in DI if not already.
                    builder.Services.TryAddTransient(typeof(T));
                    interceptor = ReflectionMcpServerInterceptor.Create(
                        method,
                        target: null,
                        new McpServerInterceptorCreateOptions { Services = null });

                    // Wrap with instance resolution
                    var instanceInterceptor = new DeferredInstanceInterceptor<T>(interceptor, method);
                    builder.Services.AddSingleton<McpServerInterceptor>(instanceInterceptor);
                    continue;
                }

                builder.Services.AddSingleton<McpServerInterceptor>(interceptor);
            }
        }

        return builder;
    }

    /// <summary>
    /// Registers interceptors from all methods on <typeparamref name="T"/> on the given instance.
    /// </summary>
    public static IMcpServerBuilder WithInterceptors<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] T>(
        this IMcpServerBuilder builder,
        T target) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        EnsureSetupRegistered(builder.Services);

        foreach (var method in typeof(T).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<McpServerInterceptorAttribute>() is not null)
            {
                var interceptor = ReflectionMcpServerInterceptor.Create(method, method.IsStatic ? null : target);
                builder.Services.AddSingleton<McpServerInterceptor>(interceptor);
            }
        }

        return builder;
    }

    /// <summary>
    /// Registers the specified interceptors directly.
    /// </summary>
    public static IMcpServerBuilder WithInterceptors(
        this IMcpServerBuilder builder,
        params McpServerInterceptor[] interceptors)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EnsureSetupRegistered(builder.Services);

        foreach (var interceptor in interceptors)
        {
            builder.Services.AddSingleton(interceptor);
        }

        return builder;
    }

    private static void EnsureSetupRegistered(IServiceCollection services)
    {
        // Only register once
        if (services.Any(d => d.ImplementationType == typeof(InterceptorServerOptionsSetup)))
        {
            return;
        }

        services.AddSingleton<IConfigureOptions<McpServerOptions>, InterceptorServerOptionsSetup>();
    }

    /// <summary>
    /// Wraps an interceptor to resolve target instances from DI for instance methods.
    /// </summary>
    private sealed class DeferredInstanceInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] TTarget> : McpServerInterceptor
    {
        private readonly McpServerInterceptor _inner;
        private readonly MethodInfo _method;

        internal DeferredInstanceInterceptor(McpServerInterceptor inner, MethodInfo method)
        {
            _inner = inner;
            _method = method;
        }

        public override Interceptor ProtocolInterceptor => _inner.ProtocolInterceptor;
        public override IReadOnlyList<object> Metadata => _inner.Metadata;

        public override async ValueTask<InterceptorResult> InvokeAsync(
            InvokeInterceptorRequestParams request,
            McpServer server,
            IServiceProvider? services,
            CancellationToken cancellationToken = default)
        {
            if (services is null)
            {
                throw new InvalidOperationException($"Cannot resolve instance of type '{typeof(TTarget).Name}' without a service provider.");
            }

            var target = (TTarget)services.GetRequiredService(typeof(TTarget));

            // Create a new interceptor bound to this instance and invoke it
            var boundInterceptor = ReflectionMcpServerInterceptor.Create(_method, target);
            return await boundInterceptor.InvokeAsync(request, server, services, cancellationToken);
        }
    }
}

/// <summary>
/// Configures <see cref="McpServerOptions"/> to register interceptor support.
/// </summary>
internal sealed class InterceptorServerOptionsSetup : IConfigureOptions<McpServerOptions>
{
    private readonly IEnumerable<McpServerInterceptor> _interceptors;

    public InterceptorServerOptionsSetup(IEnumerable<McpServerInterceptor> interceptors)
    {
        _interceptors = interceptors;
    }

    public void Configure(McpServerOptions options)
    {
        // Collect all interceptors into a primitive collection
        var collection = new McpServerPrimitiveCollection<McpServerInterceptor>();
        var allEvents = new HashSet<string>();

        foreach (var interceptor in _interceptors)
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

        // Register the message filter
        var filter = new InterceptorMessageFilter(collection);
        options.Filters.Message.IncomingFilters.Add(filter.CreateFilter);

        // Advertise interceptor capability via Extensions
        options.Capabilities ??= new();

#pragma warning disable MCPEXP001 // We intentionally use the experimental Extensions API for protocol extensions
        options.Capabilities.Extensions ??= new Dictionary<string, object>();
        // Serialize to JsonElement so the SDK's own serializer (which doesn't know about
        // InterceptorsCapability) can write it without type-info issues.
        var capability = new InterceptorsCapability
        {
            SupportedEvents = allEvents.ToList(),
        };
        options.Capabilities.Extensions[InterceptorProtocolConstants.ExtensionCapabilityKey] = JsonSerializer.SerializeToElement(
            capability, InterceptorJsonUtilities.DefaultOptions);
#pragma warning restore MCPEXP001
    }
}
