using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Interceptors;
using ModelContextProtocol.Interceptors.Client;
using ModelContextProtocol.Interceptors.Gateway;
using ModelContextProtocol.Interceptors.Protocol;

namespace InterceptorProxy;

/// <summary>
/// The proxy's configuration file. The format belongs to this sample, not to the library; its
/// <c>policy</c> section mirrors SEP-2624's <c>InterceptorOverrides</c> plus per-interceptor <c>config</c>.
/// </summary>
internal sealed partial class ProxyConfig
{
    /// <summary>Name advertised to clients. Defaults to the backend's own server name.</summary>
    public string? Name { get; set; }

    /// <summary>The MCP server being protected.</summary>
    public required ServerConnection Backend { get; set; }

    /// <summary>Interceptor servers, keyed by a name used in logs.</summary>
    public Dictionary<string, ServerConnection> InterceptorServers { get; set; } = [];

    /// <summary>Which side of the trust boundary the proxy protects: "client" (default) or "server".</summary>
    public InterceptorInvokerRole InvokerRole { get; set; } = InterceptorInvokerRole.Client;

    /// <summary>Events to intercept; omit to intercept everything.</summary>
    public List<string>? Events { get; set; }

    /// <summary>Aggregate timeout for all interceptors in one phase of one operation.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>"errorResult" (default) or "protocolError".</summary>
    public ToolCallRejectionMode ToolCallRejection { get; set; } = ToolCallRejectionMode.ErrorResult;

    /// <summary>JSON-lines file recording every interceptor run (no payloads).</summary>
    public string? AuditLog { get; set; }

    public PolicyConfig Policy { get; set; } = new();

    internal sealed class PolicyConfig
    {
        /// <summary>Interceptors that must be present; missing ones fail every operation (fail-closed).</summary>
        public List<string>? Include { get; set; }

        public int? DefaultTimeoutMs { get; set; }

        public Dictionary<string, InterceptorSettings> Interceptors { get; set; } = [];
    }

    internal sealed class InterceptorSettings
    {
        public InterceptorMode? Mode { get; set; }
        public bool? FailOpen { get; set; }
        public int? TimeoutMs { get; set; }
        public PriorityHint? PriorityHint { get; set; }
        public List<InterceptorHook>? Hooks { get; set; }
        public JsonNode? Config { get; set; }
    }

    internal sealed class ServerConnection
    {
        public string? Command { get; set; }
        public List<string> Args { get; set; } = [];
        public Dictionary<string, string?>? Env { get; set; }
        public string? Cwd { get; set; }
        public Uri? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }

        public IClientTransport CreateTransport(string name, string baseDirectory, Action<string> onStderr)
        {
            if (Url is not null)
            {
                return new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = name,
                    Endpoint = Url,
                    AdditionalHeaders = Headers,
                });
            }

            if (string.IsNullOrWhiteSpace(Command))
            {
                throw new InvalidOperationException($"Server '{name}' needs either \"command\" or \"url\".");
            }

            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = Command,
                Arguments = Args,
                EnvironmentVariables = Env,
                WorkingDirectory = Path.GetFullPath(Cwd ?? ".", baseDirectory),
                StandardErrorLines = line => onStderr($"[{name}] {line}"),
            });
        }
    }

    /// <summary>Builds the invoker policy the gateway applies.</summary>
    public InterceptorPolicy ToPolicy()
    {
        var policy = new InterceptorPolicy
        {
            Include = Policy.Include is { } include ? new HashSet<string>(include, StringComparer.Ordinal) : null,
            DefaultTimeoutMs = Policy.DefaultTimeoutMs,
        };

        foreach (var (name, settings) in Policy.Interceptors)
        {
            policy.Overrides[name] = new InterceptorOverrides
            {
                Mode = settings.Mode,
                FailOpen = settings.FailOpen,
                TimeoutMs = settings.TimeoutMs,
                PriorityHint = settings.PriorityHint,
                Hooks = settings.Hooks,
            };

            if (settings.Config is { } config)
            {
                policy.Config[name] = config;
            }
        }

        return policy;
    }

    /// <summary>Reads a config file, expanding <c>${VAR}</c> from the environment.</summary>
    public static ProxyConfig Load(string path)
    {
        var text = EnvironmentReference().Replace(File.ReadAllText(path), match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value)
            ?? throw new InvalidOperationException($"Environment variable '{match.Groups[1].Value}' referenced in {path} is not set."));

        return JsonSerializer.Deserialize<ProxyConfig>(text, SerializerOptions)
            ?? throw new InvalidOperationException($"{path} is empty.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Interceptor types come from the library's source-generated context; the config classes
        // above are read by reflection.
        TypeInfoResolver = JsonTypeInfoResolver.Combine(InterceptorJsonUtilities.DefaultOptions.TypeInfoResolver, new DefaultJsonTypeInfoResolver()),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvironmentReference();
}
