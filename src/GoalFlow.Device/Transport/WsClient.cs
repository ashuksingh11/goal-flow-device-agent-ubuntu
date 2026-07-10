using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    private readonly HashSet<string> _seenCorrelations = [];
    private CapabilitiesMessage? _capabilities;

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
    {
        _capabilities = capabilities;
        return ConnectWithRetryAsync(ct);
    }

    /// <summary>Connects, retrying with exponential backoff (1s→10s) until it
    /// succeeds or is cancelled — so the device can start before the cloud, and
    /// survive the cloud restarting.</summary>
    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(10);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ws_connect_failed {Endpoint}: {Message}; retrying in {Seconds}s",
                    _endpoint, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, max.Ticks));
            }
        }
    }

    /// <summary>
    /// Receive pump: reassembles text frames, peeks <c>type</c> via
    /// ContractJson.PeekType, dedupes repeated correlation_ids, raises
    /// <see cref="FrameReceived"/>. Runs until cancellation or socket close;
    /// reconnects on drop.
    /// </summary>
    public Task RunReceiveLoopAsync(CancellationToken ct = default)
        => ReceiveLoopAsync(ct);

    /// <summary>
    /// Sends one contract message (ContractJson snake_case). Safe to call
    /// concurrently — agent_event streaming interleaves with plan_ready/status
    /// sends, so writes are serialized on <see cref="_sendLock"/>.
    /// </summary>
    public Task SendAsync<T>(T message, CancellationToken ct = default)
        => SendCoreAsync(message, ct);

    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(_endpoint, ct);
        _logger.LogInformation("ws_connected {Endpoint}", _endpoint);
        await SendAsync(new Hello(), ct);
        await SendAsync(_capabilities!, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
          try
          {
            if (_socket is null || _socket.State != WebSocketState.Open)
            {
                await ConnectWithRetryAsync(ct);
            }

            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket!.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var raw = Encoding.UTF8.GetString(ms.ToArray());
            var type = ContractJson.PeekType(raw);
            if (type is null)
            {
                continue;
            }

            // Dedupe only replay-prone frames. approval/control are USER COMMANDS
            // that legitimately repeat with the same goal correlation_id but carry
            // distinct content (approving different proposals, advancing days) — the
            // app layer (ApprovalCoordinator) handles their idempotency. Deduping
            // them here silently dropped every approval after the first.
            var dedupeable = type is not (MessageTypes.Approval or MessageTypes.Control);
            var correlation = TryGetCorrelation(raw);
            if (dedupeable && correlation is not null && !_seenCorrelations.Add($"{type}:{correlation}"))
            {
                _logger.LogInformation("ws_duplicate_ignored type={Type} correlation_id={CorrelationId}", type, correlation);
                continue;
            }

            if (FrameReceived is not null)
            {
                await FrameReceived.Invoke(type, raw);
            }
          }
          catch (OperationCanceledException)
          {
              break;
          }
          catch (Exception ex)
          {
              // Any transport error (cloud down / restarted / dropped) → drop the
              // socket and let the next iteration reconnect with backoff. Never crash.
              _logger.LogWarning("ws_receive_error: {Message}; reconnecting", ex.Message);
              _socket?.Dispose();
              _socket = null;
              try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
              catch (OperationCanceledException) { break; }
          }
        }
    }

    private async Task SendCoreAsync<T>(T message, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }

        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(message));
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string? TryGetCorrelation(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("correlation_id", out var c) ? c.GetString() : null;
    }
}
