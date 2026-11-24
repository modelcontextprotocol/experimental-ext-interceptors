using System.Collections.Concurrent;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WhyInterceptorsDemo;

/// <summary>
/// Wraps a client transport and keeps every JSON-RPC message that crosses it, so the demo can
/// show the exact <c>interceptor/invoke</c> exchange rather than a reconstruction.
/// </summary>
internal sealed class RecordingClientTransport(IClientTransport inner) : IClientTransport
{
    public ConcurrentQueue<(bool Outgoing, JsonRpcMessage Message)> Messages { get; } = new();

    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new RecordingTransport(await inner.ConnectAsync(cancellationToken), Messages);

    private sealed class RecordingTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly ConcurrentQueue<(bool, JsonRpcMessage)> _messages;
        private readonly Channel<JsonRpcMessage> _incoming = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly Task _pump;

        public RecordingTransport(ITransport inner, ConcurrentQueue<(bool, JsonRpcMessage)> messages)
        {
            _inner = inner;
            _messages = messages;
            _pump = PumpAsync();
        }

        public string? SessionId => _inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => _incoming.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            _messages.Enqueue((true, message));
            return _inner.SendMessageAsync(message, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            try { await _pump; } catch { }
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var message in _inner.MessageReader.ReadAllAsync())
                {
                    _messages.Enqueue((false, message));
                    await _incoming.Writer.WriteAsync(message);
                }
            }
            finally
            {
                _incoming.Writer.TryComplete();
            }
        }
    }
}
