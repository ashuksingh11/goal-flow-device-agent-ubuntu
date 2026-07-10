using System.Net.WebSockets;
using GoalFlow.Device.Contracts;
using Microsoft.Extensions.Logging;

namespace GoalFlow.Device.Transport;

/// <summary>
/// The device's single outbound WebSocket to the cloud hub — BCL
/// <see cref="ClientWebSocket"/> only (Tizen-lean; no transport packages).
/// Registers with <c>hello(role: device)</c>, advertises <c>capabilities</c>,
/// then pumps frames both ways: inbound dispatch/approval/control are routed
/// to the GoalAgent; outbound plan_ready/proposal/status AND the high-rate
/// STREAM of agent_event frames go out through <see cref="SendAsync{T}"/>
/// (serialized sends — one writer at a time). Dedupes on correlation_id and
/// reconnects with backoff, per contract.
/// </summary>
public sealed class WsClient : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly ILogger<WsClient> _logger;
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WsClient(Uri endpoint, ILogger<WsClient> logger)
    {
        _endpoint = endpoint;
        _logger = logger;
    }

    /// <summary>Raised for each inbound frame, keyed by its <c>type</c> discriminator.</summary>
    public event Func<string /*type*/, string /*rawJson*/, Task>? FrameReceived;

    /// <summary>
    /// Connects, sends <c>hello</c>, awaits <c>hello_ack</c>, then sends the
    /// capabilities advertisement. Retries with exponential backoff.
    /// </summary>
    public Task ConnectAsync(CapabilitiesMessage capabilities, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>
    /// Receive pump: reassembles text frames, peeks <c>type</c> via
    /// ContractJson.PeekType, dedupes repeated correlation_ids, raises
    /// <see cref="FrameReceived"/>. Runs until cancellation or socket close;
    /// reconnects on drop.
    /// </summary>
    public Task RunReceiveLoopAsync(CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    /// <summary>
    /// Sends one contract message (ContractJson snake_case). Safe to call
    /// concurrently — agent_event streaming interleaves with plan_ready/status
    /// sends, so writes are serialized on <see cref="_sendLock"/>.
    /// </summary>
    public Task SendAsync<T>(T message, CancellationToken ct = default)
        => throw new NotImplementedException("v2-M0 design skeleton");

    public ValueTask DisposeAsync()
        => throw new NotImplementedException("v2-M0 design skeleton");
}
