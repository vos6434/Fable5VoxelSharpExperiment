using System.Net.WebSockets;
using System.Threading.Channels;
using Voxel.Shared;

namespace Voxel.Server;

/// <summary>
/// Authoritative game server, protocol-compatible with the web version: owns
/// the persisted world (all chunk data flows from here), applies and
/// rebroadcasts block edits, and relays player positions at 10 Hz. Movement
/// is client-reported for now — same trust model as the web server.
///
/// Concurrency model: each socket gets a receive loop and a send loop; sends
/// go through a per-client channel so a slow client can't interleave frames
/// or block the broadcaster. World-store access is serialized with a lock.
/// </summary>
public sealed class GameServer
{
    private const int MaxChunksPerRequest = 64;

    private sealed class Client
    {
        public required int Id { get; init; }
        public required string Name { get; set; }
        public required Channel<byte[]> Outbox { get; init; }
        public double X, Y, Z;
        public float Yaw, Pitch;
    }

    private readonly WorldStore _store;
    private readonly BlockRegistry _blocks;
    private readonly WorldGen _generator;
    private readonly WorldClock _clock;
    private readonly Lock _worldLock = new();
    private readonly Lock _clientsLock = new();
    private readonly Dictionary<int, Client> _clients = new();
    private int _nextPlayerId = 1;

    public GameServer(WorldStore store, BlockRegistry blocks, WorldGen generator, WorldClock clock)
    {
        _store = store;
        _blocks = blocks;
        _generator = generator;
        _clock = clock;
        _clock.OnTick += HandleTick;
        _clock.OnTimescaleChanged += BroadcastTimeSync;
    }

    /// <summary>Runs on the clock thread — the server's simulation heartbeat.</summary>
    private void HandleTick(long worldTick)
    {
        if (worldTick % 2 == 0) BroadcastMoves();           // 10 Hz position relay
        if (worldTick % 100 == 0)
        {
            BroadcastTimeSync();                             // drift guard
            lock (_worldLock) _store.SetMeta("worldTime", worldTick.ToString());
        }
    }

    private void BroadcastTimeSync()
    {
        Broadcast(Protocol.EncodeTimeSync(_clock.WorldTick, _clock.Timescale, Constants.DayLengthTicks));
    }

    public int PlayerCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public async Task HandleSocket(WebSocket ws, CancellationToken ct)
    {
        Client? client = null;
        var sendTask = Task.CompletedTask;
        try
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                byte[] data = message.ToArray();
                if (data.Length == 0) continue;

                if (client is null)
                {
                    if (Protocol.TypeOf(data) != Msg.Hello) continue; // must hello first
                    var hello = Protocol.DecodeJson<HelloPayload>(data);
                    if (hello.ProtocolVersion != Protocol.Version)
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.PolicyViolation,
                            $"client outdated: protocol {hello.ProtocolVersion}, server requires {Protocol.Version} - update your client",
                            ct);
                        return;
                    }
                    client = OnJoin(ws, hello.Name, ct, out sendTask);
                }
                else
                {
                    Handle(client, data);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // normal disconnect paths
        }
        finally
        {
            if (client is not null) OnLeave(client);
            try { await sendTask; } catch { /* socket already gone */ }
        }
    }

    private Client OnJoin(WebSocket ws, string name, CancellationToken ct, out Task sendTask)
    {
        var spawn = _generator.FindSpawn();
        var client = new Client
        {
            Id = Interlocked.Increment(ref _nextPlayerId),
            Name = name.Length > 24 ? name[..24] : (name.Length == 0 ? "anonymous" : name),
            Outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true }),
            X = spawn.X, Y = spawn.Y + 24, Z = spawn.Z,
        };
        sendTask = Task.Run(() => SendLoop(ws, client.Outbox.Reader, ct), ct);

        var welcome = new WelcomePayload
        {
            PlayerId = client.Id,
            Seed = _generator.Seed,
            Spawn = new SpawnPoint { X = client.X, Y = client.Y, Z = client.Z },
            Palette = [.. _blocks.Defs.Select(d => d.StringId)],
            ProtocolVersion = Protocol.Version,
        };
        client.Outbox.Writer.TryWrite(Protocol.EncodeJson(Msg.Welcome, welcome));
        client.Outbox.Writer.TryWrite(Protocol.EncodeTimeSync(_clock.WorldTick, _clock.Timescale, Constants.DayLengthTicks));

        lock (_clientsLock)
        {
            // Introduce existing players to the newcomer, then announce the newcomer.
            foreach (var other in _clients.Values)
            {
                client.Outbox.Writer.TryWrite(Protocol.EncodeJson(
                    Msg.PlayerJoin, new PlayerJoinPayload { Id = other.Id, Name = other.Name }));
            }
            _clients[client.Id] = client;
        }
        Broadcast(Protocol.EncodeJson(Msg.PlayerJoin, new PlayerJoinPayload { Id = client.Id, Name = client.Name }), except: client.Id);
        Console.WriteLine($"[server] {client.Name} (#{client.Id}) joined - {PlayerCount} online");
        return client;
    }

    private void OnLeave(Client client)
    {
        lock (_clientsLock)
        {
            if (!_clients.Remove(client.Id)) return;
        }
        client.Outbox.Writer.TryComplete();
        Broadcast(Protocol.EncodeJson(Msg.PlayerLeave, new PlayerLeavePayload { Id = client.Id }));
        Console.WriteLine($"[server] {client.Name} (#{client.Id}) left - {PlayerCount} online");
    }

    private void Handle(Client client, byte[] data)
    {
        switch (Protocol.TypeOf(data))
        {
            case Msg.ChunkRequest:
            {
                var coords = Protocol.DecodeChunkRequest(data);
                int served = 0;
                foreach (var (cx, cy, cz) in coords)
                {
                    if (served++ >= MaxChunksPerRequest) break;
                    ChunkData chunk;
                    lock (_worldLock) chunk = _store.Load(cx, cy, cz);
                    byte[]? payload = chunk.CountSolid() == 0 ? null : WorldStore.DeflateChunk(chunk);
                    client.Outbox.Writer.TryWrite(Protocol.EncodeChunkData(cx, cy, cz, payload));
                }
                break;
            }
            case Msg.Move:
            {
                var m = Protocol.DecodeMove(data);
                client.X = m.X; client.Y = m.Y; client.Z = m.Z;
                client.Yaw = m.Yaw; client.Pitch = m.Pitch;
                break;
            }
            case Msg.SetBlock:
            {
                var edit = Protocol.DecodeBlockChange(data);
                if (edit.BlockId >= _blocks.Count) return; // unknown block
                lock (_worldLock) _store.SetBlock(edit.X, edit.Y, edit.Z, edit.BlockId);
                Broadcast(Protocol.EncodeBlockChange(Msg.BlockUpdate, edit.X, edit.Y, edit.Z, edit.BlockId));
                break;
            }
            default:
                break;
        }
    }

    private static async Task SendLoop(WebSocket ws, ChannelReader<byte[]> outbox, CancellationToken ct)
    {
        await foreach (byte[] message in outbox.ReadAllAsync(ct))
        {
            if (ws.State != WebSocketState.Open) break;
            await ws.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }

    private void Broadcast(byte[] message, int? except = null)
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients.Values)
            {
                if (client.Id != except) client.Outbox.Writer.TryWrite(message);
            }
        }
    }

    private void BroadcastMoves()
    {
        PlayerMove[] moves;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            moves = [.. _clients.Values.Select(c => new PlayerMove(
                (ushort)c.Id, (float)c.X, (float)c.Y, (float)c.Z, c.Yaw, c.Pitch))];
        }
        Broadcast(Protocol.EncodePlayerMoves(moves));
    }
}
