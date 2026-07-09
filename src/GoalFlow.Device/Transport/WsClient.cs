using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Transport;

/// <summary>
/// LATER MILESTONE — thin WebSocket transport shell (DESIGN STUB).
/// <para>
/// The cloud is the hub; the device opens ONE outbound
/// <see cref="ClientWebSocket"/> to it (works on both Linux dev and Tizen)
/// and registers with a <c>hello</c> frame. The UI never talks to the device
/// directly — everything routes through the cloud.
/// </para>
/// <para>
/// This class is deliberately dumb plumbing around the transport-agnostic
/// <see cref="Pipeline"/>: deserialize <c>dispatch</c> → Pipeline.RunAsync →
/// serialize <c>plan_ready</c>; deserialize <c>approval</c> →
/// Pipeline.OnApprovalAsync → serialize <c>status</c>. No policy, no state
/// beyond the socket + outbox.
/// </para>
/// Resilience contract: reconnect (with backoff) on drop; re-send
/// unacknowledged outbound messages; receiver dedupes on
/// <c>correlation_id</c> (as does ours, via ApprovalBroker/EffectExecutor).
/// </summary>
public sealed class WsClient : IAsyncDisposable
{
    private readonly Uri _cloudUri;
    private readonly Pipeline _pipeline;
    private readonly Harnesses.ITrace _trace;

    private ClientWebSocket? _socket;

    /// <param name="cloudUri">Cloud hub WS endpoint, e.g. wss://cloud.example/ws — Tizen port is a one-line endpoint swap.</param>
    public WsClient(Uri cloudUri, Pipeline pipeline, Harnesses.ITrace trace)
    {
        _cloudUri = cloudUri;
        _pipeline = pipeline;
        _trace = trace;
    }

    /// <summary>
    /// Connects, performs the hello/hello_ack handshake, then runs the
    /// receive loop until cancelled. Reconnects with backoff on drop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                _socket = socket;

                Console.Error.WriteLine($"connecting to {_cloudUri}");
                await socket.ConnectAsync(_cloudUri, cancellationToken);
                await SendAsync(new Hello(), cancellationToken);
                Console.Error.WriteLine("sent hello frame");

                await ReceiveLoopAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"websocket disconnected: {ex.Message}");
            }
            finally
            {
                _socket = null;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>Serializes any contract message and sends it as one text frame.</summary>
    public async Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        var json = JsonSerializer.Serialize(message, ContractJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { State: WebSocketState.Open } socket)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "device shutting down",
                CancellationToken.None);
        }

        _socket?.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextFrameAsync(socket, cancellationToken);
            if (json is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                Console.Error.WriteLine("ignoring websocket frame without type");
                continue;
            }

            switch (typeElement.GetString())
            {
                case MessageTypes.Dispatch:
                    var dispatch = JsonSerializer.Deserialize<Dispatch>(json, ContractJson.Options)
                        ?? throw new InvalidOperationException("Unable to deserialize dispatch frame.");
                    var planReady = await _pipeline.RunAsync(dispatch, cancellationToken);
                    await SendAsync(planReady, cancellationToken);
                    Console.Error.WriteLine($"sent plan_ready for {planReady.GoalId}/{planReady.CorrelationId}");
                    break;

                case MessageTypes.HelloAck:
                    Console.Error.WriteLine("received hello_ack");
                    break;

                case MessageTypes.Approval:
                    var approval = JsonSerializer.Deserialize<Approval>(json, ContractJson.Options)
                        ?? throw new InvalidOperationException("Unable to deserialize approval frame.");
                    var statuses = await _pipeline.OnApprovalAsync(approval, cancellationToken);
                    foreach (var status in statuses)
                    {
                        await SendAsync(status, cancellationToken);
                        Console.Error.WriteLine($"sent status {status.TaskStatus} for {status.GoalId}/{status.CorrelationId}");
                    }

                    break;

                default:
                    Console.Error.WriteLine($"ignoring websocket frame type '{typeElement.GetString()}'");
                    break;
            }
        }
    }

    private static async Task<string?> ReceiveTextFrameAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
