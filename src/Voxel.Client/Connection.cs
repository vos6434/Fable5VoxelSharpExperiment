using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Threading.Channels;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>One decoded server message, queued for the main thread.</summary>
public abstract record ServerEvent
{
    public sealed record Chunk(int Cx, int Cy, int Cz, ushort[]? Blocks) : ServerEvent;
    public sealed record BlockUpdated(int X, int Y, int Z, ushort BlockId) : ServerEvent;
    public sealed record PlayerJoined(int Id, string Name) : ServerEvent;
    public sealed record PlayerLeft(int Id) : ServerEvent;
    public sealed record PlayerMoved(PlayerMove[] Moves) : ServerEvent;
    public sealed record TimeSynced(long WorldTick, float Timescale, int DayLengthTicks) : ServerEvent;
    public sealed record Disconnected(string Reason) : ServerEvent;
}

/// <summary>
/// Client side of the game protocol over ClientWebSocket. Receiving and
/// payload inflation happen on a background task; decoded events are queued
/// for the main/render thread to drain each frame. Outgoing sends are
/// serialized through a channel.
/// </summary>
public sealed class Connection : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly Channel<byte[]> _outbox =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    public readonly ConcurrentQueue<ServerEvent> Events = new();

    public int PlayerId { get; }
    public int Seed { get; }
    public (double X, double Y, double Z) Spawn { get; }
    public string[] Palette { get; }

    private Connection(ClientWebSocket ws, WelcomePayload welcome)
    {
        _ws = ws;
        PlayerId = welcome.PlayerId;
        Seed = welcome.Seed;
        Spawn = (welcome.Spawn.X, welcome.Spawn.Y, welcome.Spawn.Z);
        Palette = welcome.Palette;
        _ = Task.Run(() => SendLoop(_cts.Token));
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    public static async Task<Connection> ConnectAsync(Uri server, string playerName, TimeSpan timeout)
    {
        var ws = new ClientWebSocket();
        using var connectCts = new CancellationTokenSource(timeout);
        await ws.ConnectAsync(server, connectCts.Token);
        await ws.SendAsync(
            Protocol.EncodeJson(Msg.Hello, new HelloPayload { Name = playerName, ProtocolVersion = Protocol.Version }),
            WebSocketMessageType.Binary, endOfMessage: true, connectCts.Token);

        byte[]? first = await ReceiveMessage(ws, connectCts.Token);
        if (first is null)
        {
            // Version-mismatch rejections arrive as a close reason.
            string reason = string.IsNullOrEmpty(ws.CloseStatusDescription)
                ? "server closed during handshake"
                : ws.CloseStatusDescription;
            throw new InvalidOperationException(reason);
        }
        if (Protocol.TypeOf(first) != Msg.Welcome)
        {
            throw new InvalidOperationException("protocol error: expected Welcome");
        }
        return new Connection(ws, Protocol.DecodeJson<WelcomePayload>(first));
    }

    public void RequestChunks(IReadOnlyList<(int, int, int)> coords) =>
        _outbox.Writer.TryWrite(Protocol.EncodeChunkRequest(coords));

    public void SendMove(double x, double y, double z, float yaw, float pitch) =>
        _outbox.Writer.TryWrite(Protocol.EncodeMove(x, y, z, yaw, pitch));

    public void SendSetBlock(int x, int y, int z, ushort blockId) =>
        _outbox.Writer.TryWrite(Protocol.EncodeBlockChange(Msg.SetBlock, x, y, z, blockId));

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            await foreach (byte[] message in _outbox.Reader.ReadAllAsync(ct))
            {
                await _ws.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? message = await ReceiveMessage(_ws, ct);
                if (message is null)
                {
                    Events.Enqueue(new ServerEvent.Disconnected("server closed the connection"));
                    return;
                }
                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Events.Enqueue(new ServerEvent.Disconnected(ex.Message));
        }
    }

    private void Dispatch(byte[] message)
    {
        switch (Protocol.TypeOf(message))
        {
            case Msg.ChunkData:
            {
                var header = Protocol.DecodeChunkData(message);
                ushort[]? blocks = null;
                if (!header.Empty)
                {
                    byte[] raw = InflateRaw(header.Payload.Span);
                    if (raw.Length != Constants.ChunkVolume * 2)
                    {
                        throw new InvalidDataException($"chunk {header.Cx},{header.Cy},{header.Cz}: bad payload size {raw.Length}");
                    }
                    blocks = new ushort[Constants.ChunkVolume];
                    Buffer.BlockCopy(raw, 0, blocks, 0, raw.Length);
                }
                Events.Enqueue(new ServerEvent.Chunk(header.Cx, header.Cy, header.Cz, blocks));
                break;
            }
            case Msg.BlockUpdate:
            {
                var change = Protocol.DecodeBlockChange(message);
                Events.Enqueue(new ServerEvent.BlockUpdated(change.X, change.Y, change.Z, change.BlockId));
                break;
            }
            case Msg.PlayerJoin:
            {
                var p = Protocol.DecodeJson<PlayerJoinPayload>(message);
                Events.Enqueue(new ServerEvent.PlayerJoined(p.Id, p.Name));
                break;
            }
            case Msg.PlayerLeave:
                Events.Enqueue(new ServerEvent.PlayerLeft(Protocol.DecodeJson<PlayerLeavePayload>(message).Id));
                break;
            case Msg.PlayerMoves:
                Events.Enqueue(new ServerEvent.PlayerMoved(Protocol.DecodePlayerMoves(message)));
                break;
            case Msg.TimeSync:
            {
                var (worldTick, timescale, dayLength) = Protocol.DecodeTimeSync(message);
                Events.Enqueue(new ServerEvent.TimeSynced(worldTick, timescale, dayLength));
                break;
            }
            default:
                break;
        }
    }

    private static async Task<byte[]?> ReceiveMessage(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    private static byte[] InflateRaw(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _ws.Dispose();
    }
}
