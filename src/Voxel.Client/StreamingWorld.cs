using Silk.NET.OpenGL;
using Voxel.Client.Gl;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Server-fed chunk streaming, ported from the web client: chunks within
/// DataRadius are requested over the wire (nearest-first, batched, bounded
/// in-flight); chunks within RenderRadius mesh once all six neighbors have
/// data so cross-chunk culling is always correct; everything beyond
/// UnloadRadius unloads (hysteresis). Block edits bump the chunk's mesh
/// version and re-queue meshing; border edits also remesh the touching
/// neighbor; stale mesh results are dropped by version comparison.
/// </summary>
public sealed class StreamingWorld : IDisposable
{
    public const int RenderRadius = 6;
    private const int DataRadius = RenderRadius + 1;
    private const int UnloadRadius = DataRadius + 2;
    private const int MaxOutstandingRequests = 96;
    private const int RequestBatch = 32;
    private const int MaxPendingMeshJobs = 24;
    private const int MaxUploadsPerFrame = 8;

    private enum MeshState { None, Pending, Done }

    private sealed class Entry
    {
        public required int Cx, Cy, Cz;
        public required ChunkData Data;
        public bool Empty;
        public MeshState State;
        public long MeshVersion;
        public ChunkMesh? Solid;
        public ChunkMesh? LiquidSurface;
        public ChunkMesh? Translucent;
    }

    // FACE_DIRS order: px, nx, py, ny, pz, nz.
    private static readonly (int Dx, int Dy, int Dz)[] NeighborOffsets =
        [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    private static readonly (int Dx, int Dy, int Dz, int DistSq)[] DataOffsets = SphereOffsets(DataRadius);
    private static readonly (int Dx, int Dy, int Dz, int DistSq)[] RenderOffsets = SphereOffsets(RenderRadius);

    private readonly GL _gl;
    private readonly Connection _connection;
    private readonly MesherPool _pool;
    private readonly Dictionary<(int, int, int), Entry> _chunks = new();
    private readonly HashSet<(int, int, int)> _requested = new();
    private int _requestCursor;
    private (int X, int Y, int Z) _center = (int.MinValue, 0, 0);

    public string? DisconnectReason { get; private set; }

    public StreamingWorld(GL gl, ClientData data, Connection connection)
    {
        _gl = gl;
        _connection = connection;
        _pool = new MesherPool(data.Blocks.Opaque, data.RenderTable, data.TranslucentMask, data.FlatAoMask, data.EmissiveMask);
    }

    public (int Loaded, int Rendered, int PendingMesh, int AwaitingNet, int Workers) Stats
    {
        get
        {
            int rendered = 0;
            foreach (var e in _chunks.Values)
            {
                if (e.State == MeshState.Done && (e.Solid is not null || e.LiquidSurface is not null || e.Translucent is not null)) rendered++;
            }
            return (_chunks.Count, rendered, _pool.Pending, _requested.Count, _pool.WorkerCount);
        }
    }

    public ushort GetBlock(int wx, int wy, int wz)
    {
        return _chunks.TryGetValue((Coords.WorldToChunk(wx), Coords.WorldToChunk(wy), Coords.WorldToChunk(wz)), out var e)
            ? e.Data.Get(Coords.WorldToLocal(wx), Coords.WorldToLocal(wy), Coords.WorldToLocal(wz))
            : (ushort)0;
    }

    /// <summary>Raw block array for a loaded chunk (for the occupancy volume); null if not loaded.</summary>
    public ushort[]? ChunkBlocks(int cx, int cy, int cz)
        => _chunks.TryGetValue((cx, cy, cz), out var e) ? e.Data.Blocks : null;

    /// <summary>Chunk coords edited since the last drain (occupancy volume re-uploads these).</summary>
    private readonly HashSet<(int, int, int)> _occupancyDirty = new();

    /// <summary>Same edits, drained separately by the light volume (emitter rescan).</summary>
    private readonly HashSet<(int, int, int)> _lightDirty = new();

    public void DrainOccupancyDirty(HashSet<(int, int, int)> into)
    {
        foreach (var key in _occupancyDirty) into.Add(key);
        _occupancyDirty.Clear();
    }

    public void DrainLightDirty(HashSet<(int, int, int)> into)
    {
        foreach (var key in _lightDirty) into.Add(key);
        _lightDirty.Clear();
    }

    public void Update(double camX, double camY, double camZ)
    {
        var center = (Coords.WorldToChunk(camX), Coords.WorldToChunk(camY), Coords.WorldToChunk(camZ));
        if (center != _center)
        {
            _center = center;
            _requestCursor = 0;
            UnloadFar();
        }

        RequestSome();
        ScheduleMeshes();
        _pool.DrainResults(MaxUploadsPerFrame, ApplyMeshResult);
    }

    /// <summary>World-related server events, routed here by the Game's event drain.</summary>
    public void HandleEvent(ServerEvent evt)
    {
        switch (evt)
        {
            case ServerEvent.Chunk chunk:
                OnChunk(chunk);
                break;
            case ServerEvent.BlockUpdated update:
                SetBlock(update.X, update.Y, update.Z, update.BlockId);
                break;
            case ServerEvent.Disconnected d:
                DisconnectReason = d.Reason;
                break;
            default:
                break;
        }
    }

    public IEnumerable<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> SolidMeshes()
    {
        foreach (var e in _chunks.Values)
        {
            if (e.Solid is not null) yield return (e.Cx, e.Cy, e.Cz, e.Solid);
        }
    }

    public IEnumerable<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> LiquidSurfaceMeshes()
    {
        foreach (var e in _chunks.Values)
        {
            if (e.LiquidSurface is not null) yield return (e.Cx, e.Cy, e.Cz, e.LiquidSurface);
        }
    }

    public IEnumerable<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> TranslucentMeshes()
    {
        foreach (var e in _chunks.Values)
        {
            if (e.Translucent is not null) yield return (e.Cx, e.Cy, e.Cz, e.Translucent);
        }
    }

    /// <summary>Applies a block change (server broadcast or local prediction) and re-queues meshing.</summary>
    public void SetBlock(int wx, int wy, int wz, ushort blockId)
    {
        int cx = Coords.WorldToChunk(wx);
        int cy = Coords.WorldToChunk(wy);
        int cz = Coords.WorldToChunk(wz);
        if (!_chunks.TryGetValue((cx, cy, cz), out var entry)) return;

        int lx = Coords.WorldToLocal(wx);
        int ly = Coords.WorldToLocal(wy);
        int lz = Coords.WorldToLocal(wz);
        if (entry.Data.Get(lx, ly, lz) == blockId) return;

        entry.Data.Set(lx, ly, lz, blockId);
        if (blockId != 0) entry.Empty = false;
        MarkDirty(entry);
        _occupancyDirty.Add((cx, cy, cz)); // this chunk's occupancy changed
        _lightDirty.Add((cx, cy, cz));

        void Touch(int ncx, int ncy, int ncz)
        {
            if (_chunks.TryGetValue((ncx, ncy, ncz), out var n)) MarkDirty(n);
        }
        if (lx == 0) Touch(cx - 1, cy, cz);
        if (lx == Constants.ChunkSize - 1) Touch(cx + 1, cy, cz);
        if (ly == 0) Touch(cx, cy - 1, cz);
        if (ly == Constants.ChunkSize - 1) Touch(cx, cy + 1, cz);
        if (lz == 0) Touch(cx, cy, cz - 1);
        if (lz == Constants.ChunkSize - 1) Touch(cx, cy, cz + 1);
    }

    // ---- internals ----------------------------------------------------------

    private void MarkDirty(Entry entry)
    {
        entry.MeshVersion++;
        if (!entry.Empty || entry.Solid is not null) entry.State = MeshState.None;
    }

    private void OnChunk(ServerEvent.Chunk chunk)
    {
        var key = (chunk.Cx, chunk.Cy, chunk.Cz);
        _requested.Remove(key);
        if (_chunks.ContainsKey(key)) return;

        int dx = chunk.Cx - _center.X, dy = chunk.Cy - _center.Y, dz = chunk.Cz - _center.Z;
        if (dx * dx + dy * dy + dz * dz > UnloadRadius * UnloadRadius) return; // moved away meanwhile

        bool empty = chunk.Blocks is null;
        _chunks[key] = new Entry
        {
            Cx = chunk.Cx, Cy = chunk.Cy, Cz = chunk.Cz,
            Data = chunk.Blocks is null ? new ChunkData() : new ChunkData(chunk.Blocks),
            Empty = empty,
            State = empty ? MeshState.Done : MeshState.None,
        };
    }

    private void RequestSome()
    {
        if (_requestCursor >= DataOffsets.Length) return;
        List<(int, int, int)>? batch = null;
        while (_requestCursor < DataOffsets.Length &&
               _requested.Count < MaxOutstandingRequests &&
               (batch?.Count ?? 0) < RequestBatch)
        {
            var (dx, dy, dz, _) = DataOffsets[_requestCursor];
            _requestCursor++;
            var key = (_center.X + dx, _center.Y + dy, _center.Z + dz);
            if (_chunks.ContainsKey(key) || _requested.Contains(key)) continue;
            _requested.Add(key);
            (batch ??= new List<(int, int, int)>()).Add(key);
        }
        if (batch is not null) _connection.RequestChunks(batch);
    }

    private void ScheduleMeshes()
    {
        foreach (var (dx, dy, dz, _) in RenderOffsets)
        {
            if (_pool.Pending >= MaxPendingMeshJobs) break;
            var key = (_center.X + dx, _center.Y + dy, _center.Z + dz);
            if (!_chunks.TryGetValue(key, out var entry) || entry.State != MeshState.None) continue;

            var neighbors = new ushort[]?[6];
            bool ready = true;
            for (int i = 0; i < 6; i++)
            {
                var (nx, ny, nz) = NeighborOffsets[i];
                if (!_chunks.TryGetValue((entry.Cx + nx, entry.Cy + ny, entry.Cz + nz), out var n))
                {
                    ready = false;
                    break;
                }
                neighbors[i] = n.Data.Blocks;
            }
            if (!ready) continue;

            entry.State = MeshState.Pending;
            // Workers get copies; the world keeps the originals editable.
            var blocksCopy = (ushort[])entry.Data.Blocks.Clone();
            for (int i = 0; i < 6; i++) neighbors[i] = (ushort[]?)neighbors[i]?.Clone();
            _pool.Submit(new MesherPool.Job(key, entry.MeshVersion, blocksCopy, neighbors));
        }
    }

    private void ApplyMeshResult(MesherPool.Completed completed)
    {
        if (!_chunks.TryGetValue(completed.Job.Key, out var entry)) return;    // unloaded meanwhile
        if (entry.MeshVersion != completed.Job.Version) return;                // edited meanwhile
        entry.Solid?.Dispose();
        entry.LiquidSurface?.Dispose();
        entry.Translucent?.Dispose();
        entry.Solid = completed.Result.Solid is null ? null : new ChunkMesh(_gl, completed.Result.Solid);
        entry.LiquidSurface = completed.Result.LiquidSurface is null ? null : new ChunkMesh(_gl, completed.Result.LiquidSurface);
        entry.Translucent = completed.Result.Translucent is null ? null : new ChunkMesh(_gl, completed.Result.Translucent);
        entry.State = MeshState.Done;
    }

    private void UnloadFar()
    {
        List<(int, int, int)>? toRemove = null;
        foreach (var (key, entry) in _chunks)
        {
            int dx = entry.Cx - _center.X;
            int dy = entry.Cy - _center.Y;
            int dz = entry.Cz - _center.Z;
            if (dx * dx + dy * dy + dz * dz <= UnloadRadius * UnloadRadius) continue;
            entry.Solid?.Dispose();
            entry.LiquidSurface?.Dispose();
            entry.Translucent?.Dispose();
            entry.MeshVersion++; // invalidates any in-flight mesh job
            (toRemove ??= new List<(int, int, int)>()).Add(key);
        }
        if (toRemove is not null)
        {
            foreach (var key in toRemove) _chunks.Remove(key);
        }
    }

    private static (int, int, int, int)[] SphereOffsets(int radius)
    {
        var offsets = new List<(int, int, int, int)>();
        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int z = -radius; z <= radius; z++)
                {
                    int d2 = x * x + y * y + z * z;
                    if (d2 <= radius * radius) offsets.Add((x, y, z, d2));
                }
            }
        }
        offsets.Sort((a, b) => a.Item4.CompareTo(b.Item4));
        return [.. offsets];
    }

    public void Dispose()
    {
        foreach (var entry in _chunks.Values)
        {
            entry.Solid?.Dispose();
            entry.LiquidSurface?.Dispose();
            entry.Translucent?.Dispose();
        }
        _pool.Dispose();
    }
}
