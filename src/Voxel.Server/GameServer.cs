using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Numerics;
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
        /// <summary>WorldEdit-style box selection corners for the next contraption (plan 03 M3).</summary>
        public (int X, int Y, int Z)? GlueCorner1;
        public (int X, int Y, int Z)? GlueCorner2;
        /// <summary>Contraption held by the physics gun (0 = none).</summary>
        public uint GunHoldEntityId;
    }

    private readonly WorldStore _store;
    private readonly BlockRegistry _blocks;
    private readonly WorldGen _generator;
    private readonly WorldClock _clock;
    private readonly PhysicsWorld _physics = new();
    private readonly ConcurrentQueue<Action> _tickActions = new();
    private readonly ConcurrentDictionary<uint, byte[]> _spawnPayloads = new();
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

        // Physics terrain source: solid (collision) blocks collide; the chunk
        // fetch clones under the world lock so the tick thread reads a stable
        // copy while network SetBlocks may mutate the live chunk.
        var collides = new byte[blocks.Count];
        var water = new byte[blocks.Count];
        foreach (var def in blocks.Defs)
        {
            collides[def.NumericId] = (byte)(def.Collision == Collision.Solid ? 1 : 0);
            water[def.NumericId] = (byte)(def.StringId == "water" ? 1 : 0);
        }
        _physics.SetTerrainSource(
            (cx, cy, cz) => { lock (_worldLock) return (ushort[])_store.Load(cx, cy, cz).Blocks.Clone(); },
            collides);
        _physics.SetWaterTable(water);
        _physics.OnGrabReleased = clientId =>
        {
            lock (_clientsLock)
            {
                if (_clients.TryGetValue(clientId, out var c))
                {
                    c.GunHoldEntityId = 0;
                    c.Outbox.Writer.TryWrite(Protocol.EncodeGunHold(0));
                }
            }
        };
        LoadPersistedEntities();
    }

    /// <summary>Writes every live entity to disk (shutdown checkpoint).</summary>
    public void PersistAllEntities()
    {
        lock (_worldLock)
        {
            foreach (var e in _physics.Entities.Values)
                _store.SaveEntity(_physics.ToSavedEntity(e));
        }
    }

    private void LoadPersistedEntities()
    {
        List<SavedEntity> saved;
        lock (_worldLock) saved = _store.LoadEntities();
        if (saved.Count == 0) return;

        foreach (var snap in saved)
        {
            var entity = _physics.LoadEntity(snap);
            var (pos, rot, _) = _physics.GetState(entity);
            _spawnPayloads[entity.Id] = EncodeSpawn(entity, pos, rot);
        }
        Console.WriteLine($"[server] loaded {saved.Count} persisted entit{(saved.Count == 1 ? "y" : "ies")}");
    }

    /// <summary>Runs on the clock thread — the server's simulation heartbeat.</summary>
    private void HandleTick(long worldTick)
    {
        // Physics + entity mutations happen only here (Bepu isn't thread-safe);
        // console/network requests enqueue actions the tick thread drains.
        while (_tickActions.TryDequeue(out var action)) action();
        UpdateGunGrabs();
        _physics.Step((float)ClockCore.TickSeconds);
        PersistSleepTransitions();

        if (worldTick % 2 == 0)                              // 10 Hz relay
        {
            BroadcastMoves();
            BroadcastEntityStates();
        }
        if (worldTick % 100 == 0)
        {
            BroadcastTimeSync();                             // drift guard
            lock (_worldLock) _store.SetMeta("worldTime", worldTick.ToString());
        }
    }

    // ---- Physics entities (plan 03) -------------------------------------------

    /// <summary>Thread-safe: queues a debug-box spawn onto the tick thread.</summary>
    public void RequestSpawnDebugBox(Vector3 position, ushort blockId)
    {
        _tickActions.Enqueue(() =>
        {
            var entity = _physics.SpawnDebugBox(position, blockId);
            var (pos, rot, _) = _physics.GetState(entity);
            byte[] payload = EncodeSpawn(entity, pos, rot);
            _spawnPayloads[entity.Id] = payload; // cached for players who join later
            Broadcast(payload);
            Console.WriteLine($"[server] spawned debug body #{entity.Id} at {position}");
        });
    }

    private static byte[] EncodeSpawn(PhysicsWorld.Entity e, Vector3 pos, Quaternion rot)
    {
        byte[] raw = new byte[e.Blocks.Length * 2];
        Buffer.BlockCopy(e.Blocks, 0, raw, 0, raw.Length);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        return Protocol.EncodeEntitySpawn(
            e.Id, e.Kind, e.DimX, e.DimY, e.DimZ,
            pos.X, pos.Y, pos.Z, rot.X, rot.Y, rot.Z, rot.W,
            e.Pivot.X, e.Pivot.Y, e.Pivot.Z, output.ToArray());
    }

    // ---- Glue → contraption (plan 03 M3) --------------------------------------

    private const int MaxContraptionBlocks = 1000;

    private ushort GetWorldBlock(int x, int y, int z)
    {
        lock (_worldLock)
        {
            return _store.Load(Coords.WorldToChunk(x), Coords.WorldToChunk(y), Coords.WorldToChunk(z))
                .Get(Coords.WorldToLocal(x), Coords.WorldToLocal(y), Coords.WorldToLocal(z));
        }
    }

    /// <summary>A block can be glued if it's a breakable solid (excludes air, liquids, bedrock).</summary>
    private bool IsGlueable(ushort id)
    {
        if (id == 0 || id >= _blocks.Count) return false;
        var def = _blocks.Get(id);
        return def.Collision == Collision.Solid && def.Hardness >= 0;
    }

    private void SyncGlueSelection(Client client) =>
        client.Outbox.Writer.TryWrite(Protocol.EncodeGlueSelection(client.GlueCorner1, client.GlueCorner2));

    private List<(int X, int Y, int Z)> CollectGlueBox((int X, int Y, int Z) c1, (int X, int Y, int Z) c2)
    {
        int minX = Math.Min(c1.X, c2.X), maxX = Math.Max(c1.X, c2.X);
        int minY = Math.Min(c1.Y, c2.Y), maxY = Math.Max(c1.Y, c2.Y);
        int minZ = Math.Min(c1.Z, c2.Z), maxZ = Math.Max(c1.Z, c2.Z);
        var marks = new List<(int X, int Y, int Z)>();
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            if (IsGlueable(GetWorldBlock(x, y, z))) marks.Add((x, y, z));
        }
        return marks;
    }

    private void HandleUseItem(Client client, (Protocol.ItemAction Action, int X, int Y, int Z) use)
    {
        switch (use.Action)
        {
            case Protocol.ItemAction.GlueMark:
            {
                if (!IsGlueable(GetWorldBlock(use.X, use.Y, use.Z))) break;
                var p = (use.X, use.Y, use.Z);
                if (client.GlueCorner1 is null)
                    client.GlueCorner1 = p;
                else if (client.GlueCorner2 is null)
                    client.GlueCorner2 = p;
                else
                {
                    client.GlueCorner1 = p;
                    client.GlueCorner2 = null;
                }
                SyncGlueSelection(client);
                break;
            }
            case Protocol.ItemAction.GlueClear:
                client.GlueCorner1 = null;
                client.GlueCorner2 = null;
                SyncGlueSelection(client);
                break;
            case Protocol.ItemAction.GlueActivate:
            {
                if (client.GlueCorner1 is not { } a || client.GlueCorner2 is not { } b) break;
                var marks = CollectGlueBox(a, b);
                if (marks.Count is 0 or > MaxContraptionBlocks) break;
                client.GlueCorner1 = null;
                client.GlueCorner2 = null;
                SyncGlueSelection(client);
                _tickActions.Enqueue(() => ActivateContraption(marks));
                break;
            }
            case Protocol.ItemAction.GunGrab:
                _tickActions.Enqueue(() => TryGunGrab(client));
                break;
            case Protocol.ItemAction.GunRelease:
                _tickActions.Enqueue(() => GunRelease(client, throwImpulse: false));
                break;
            case Protocol.ItemAction.GunThrow:
                _tickActions.Enqueue(() => GunRelease(client, throwImpulse: true));
                break;
            case Protocol.ItemAction.GunSetDistance:
                _tickActions.Enqueue(() => _physics.SetGrabDistance(client.Id, use.Z / 100f));
                break;
        }
    }

    // ---- Physics gun (plan 03 M4) -----------------------------------------------

    private static void ClientLook(Client client, out Vector3 eye, out Vector3 rayDir)
    {
        eye = new Vector3((float)client.X, (float)client.Y, (float)client.Z);
        float cy = MathF.Cos(client.Yaw), sy = MathF.Sin(client.Yaw);
        float cp = MathF.Cos(client.Pitch), sp = MathF.Sin(client.Pitch);
        rayDir = Vector3.Normalize(new Vector3(-sy * cp, sp, -cy * cp));
    }

    private void TryGunGrab(Client client)
    {
        ClientLook(client, out var eye, out var rayDir);
        if (!_physics.TryGrab(client.Id, eye, rayDir, out uint entityId)) return;
        client.GunHoldEntityId = entityId;
        client.Outbox.Writer.TryWrite(Protocol.EncodeGunHold(entityId));
    }

    private void GunRelease(Client client, bool throwImpulse)
    {
        ClientLook(client, out _, out var rayDir);
        if (!_physics.ReleaseGrab(client.Id, throwImpulse, rayDir)) return;
        client.GunHoldEntityId = 0;
        client.Outbox.Writer.TryWrite(Protocol.EncodeGunHold(0));
    }

    private void UpdateGunGrabs()
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients.Values)
            {
                if (client.GunHoldEntityId == 0) continue;
                ClientLook(client, out var eye, out var rayDir);
                _physics.UpdateGrab(client.Id, eye, rayDir);
            }
        }
    }

    /// <summary>Debug: place a thin stone wall in the air and glue it into a contraption that falls.</summary>
    public void RequestTestContraption(int baseX, int baseY, int baseZ)
    {
        _tickActions.Enqueue(() =>
        {
            ushort stone = _blocks.Resolve("stone");
            var positions = new List<(int, int, int)>();
            for (int yi = 0; yi < 5; yi++)
            for (int xi = 0; xi < 3; xi++)
            {
                var p = (baseX + xi, baseY + yi, baseZ);
                lock (_worldLock) _store.SetBlock(p.Item1, p.Item2, p.Item3, stone);
                Broadcast(Protocol.EncodeBlockChange(Msg.BlockUpdate, p.Item1, p.Item2, p.Item3, stone));
                _physics.InvalidateChunk(Coords.WorldToChunk(p.Item1), Coords.WorldToChunk(p.Item2), Coords.WorldToChunk(p.Item3));
                positions.Add(p);
            }
            var entity = ActivateContraption(positions);
            // Topple it so the "falls over as one object" behavior is obvious.
            if (entity is not null) _physics.Nudge(entity, new Vector3(0, 0, -2.5f));
        });
    }

    private PhysicsWorld.Entity? ActivateContraption(List<(int X, int Y, int Z)> marks)
    {
        if (marks.Count is 0 or > MaxContraptionBlocks) return null;
        int minX = marks.Min(m => m.X), minY = marks.Min(m => m.Y), minZ = marks.Min(m => m.Z);
        int maxX = marks.Max(m => m.X), maxY = marks.Max(m => m.Y), maxZ = marks.Max(m => m.Z);
        int dimX = maxX - minX + 1, dimY = maxY - minY + 1, dimZ = maxZ - minZ + 1;

        var blocks = new ushort[dimX * dimY * dimZ];
        int placed = 0;
        foreach (var (x, y, z) in marks)
        {
            ushort id = GetWorldBlock(x, y, z);
            if (!IsGlueable(id)) continue; // vanished/edited since marking
            blocks[((y - minY) * dimZ + (z - minZ)) * dimX + (x - minX)] = id;
            lock (_worldLock) _store.SetBlock(x, y, z, 0);
            Broadcast(Protocol.EncodeBlockChange(Msg.BlockUpdate, x, y, z, 0));
            _physics.InvalidateChunk(Coords.WorldToChunk(x), Coords.WorldToChunk(y), Coords.WorldToChunk(z));
            placed++;
        }
        if (placed == 0) return null;

        var entity = _physics.SpawnContraption(minX, minY, minZ, dimX, dimY, dimZ, blocks);
        var (pos, rot, _) = _physics.GetState(entity);
        byte[] payload = EncodeSpawn(entity, pos, rot);
        _spawnPayloads[entity.Id] = payload;
        Broadcast(payload);
        Console.WriteLine($"[server] contraption #{entity.Id}: {placed} blocks, dims {dimX}x{dimY}x{dimZ}");
        return entity;
    }

    private void PersistSleepTransitions()
    {
        foreach (var entity in _physics.PollSleepTransitions())
        {
            lock (_worldLock) _store.SaveEntity(_physics.ToSavedEntity(entity));
            var (pos, rot, _) = _physics.GetState(entity);
            _spawnPayloads[entity.Id] = EncodeSpawn(entity, pos, rot);
        }
    }

    private void BroadcastEntityStates()
    {
        var entities = _physics.Entities;
        if (entities.Count == 0) return;
        var states = new List<Protocol.EntityState>(entities.Count);
        foreach (var e in entities.Values)
        {
            if (!_physics.IsAwake(e)) continue;
            var (pos, rot, vel) = _physics.GetState(e);
            states.Add(new Protocol.EntityState(
                e.Id, pos.X, pos.Y, pos.Z, rot.X, rot.Y, rot.Z, rot.W, vel.X, vel.Y, vel.Z));
        }
        if (states.Count == 0) return;
        Broadcast(Protocol.EncodeEntityStates(states));
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

        // Introduce existing physics entities (cached spawn payloads; live
        // poses follow on the next EntityState broadcast).
        foreach (var payload in _spawnPayloads.Values)
        {
            client.Outbox.Writer.TryWrite(payload);
        }

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
        _tickActions.Enqueue(() => _physics.ReleaseAllGrabsForClient(client.Id));
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
                // Rebuild that chunk's physics colliders on the tick thread.
                int ecx = Coords.WorldToChunk(edit.X), ecy = Coords.WorldToChunk(edit.Y), ecz = Coords.WorldToChunk(edit.Z);
                _tickActions.Enqueue(() => _physics.InvalidateChunk(ecx, ecy, ecz));
                break;
            }
            case Msg.UseItem:
                HandleUseItem(client, Protocol.DecodeUseItem(data));
                break;
            case Msg.TimeControl:
            {
                // Debug menu: any client may scrub/pause world time (same trust
                // model as block edits). Sentinels mean "leave unchanged".
                var (setTick, timescale) = Protocol.DecodeTimeControl(data);
                if (setTick >= 0) _clock.SetWorldTick(setTick);
                if (timescale >= 0) _clock.SetTimescale(timescale); // fires its own TimeSync
                else BroadcastTimeSync();
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
