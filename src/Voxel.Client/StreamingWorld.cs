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
///
/// LOD sections (plan 04 v2, Voxy/DH model): level ℓ ≥ 1 streams cubic
/// 16³-cell sections covering 2^ℓ chunks per axis. In its own units every
/// level runs the same ring: sections requested for distance 3..9, meshed
/// to 8, so level ℓ reaches 8·2^ℓ chunks and overlaps the finer level by
/// 25%. A section renders while any of its 8 child footprints lacks a
/// finished finer mesh — coarse fills streaming gaps and the boundary
/// beyond fine coverage, and hides automatically once children are ready
/// (hole-free refinement, no stitching).
/// </summary>
public sealed class StreamingWorld : IDisposable
{
    public const int RenderRadius = 8;                   // LOD0, full detail
    private const int DataRadius = RenderRadius + 1;
    private const int UnloadRadius = DataRadius + 2;

    /// <summary>Coarsest level the client streams/renders (plan 04 v2 M2: level 1; M3 raises it).</summary>
    public const int RenderLodLevels = 1;

    /// <summary>Chunk reach of the coarsest LOD ring (drives fog placement).</summary>
    public const int LodReachChunks = RenderRadius << RenderLodLevels;

    // LOD rings in section units, identical for every level: request the
    // shell 3..9, mesh/render to 8. The inner hole overlaps the finer level
    // (25% overdraw margin); +1 data shell feeds neighbor face culling.
    private const int LodInnerRadius = 3;
    private const int LodRenderRadius = 8;
    private const int LodDataRadius = LodRenderRadius + 1;
    private const int LodUnloadOuter = LodDataRadius + 2;
    private const int LodUnloadInner = LodInnerRadius - 1;

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

    private sealed class LodEntry
    {
        public required int Sx, Sy, Sz;
        public ushort[]? Cells;   // 16³ cells; null = all air
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
    /// <summary>LOD shell offsets (any level): the 3..9 section ring, nearest-first.</summary>
    private static readonly (int Dx, int Dy, int Dz, int DistSq)[] LodOffsets =
        [.. SphereOffsets(LodDataRadius).Where(o => o.Item4 >= LodInnerRadius * LodInnerRadius)];

    private readonly GL _gl;
    private readonly Connection _connection;
    private readonly MesherPool _pool;
    private readonly Dictionary<(int, int, int), Entry> _chunks = new();
    private readonly HashSet<(int, int, int)> _requested = new();
    // Index 1..RenderLodLevels (slot 0 unused) — one section map per level.
    private readonly Dictionary<(int, int, int), LodEntry>[] _lod;
    private readonly HashSet<(int, int, int)>[] _lodRequested;
    private readonly int[] _lodCursor;
    private int _requestCursor;
    private (int X, int Y, int Z) _center = (int.MinValue, 0, 0);

    public string? DisconnectReason { get; private set; }

    public StreamingWorld(GL gl, ClientData data, Connection connection)
    {
        _gl = gl;
        _connection = connection;
        _pool = new MesherPool(data.Blocks.Opaque, data.RenderTable, data.TranslucentMask, data.FlatAoMask, data.EmissiveMask);
        _lod = new Dictionary<(int, int, int), LodEntry>[RenderLodLevels + 1];
        _lodRequested = new HashSet<(int, int, int)>[RenderLodLevels + 1];
        _lodCursor = new int[RenderLodLevels + 1];
        for (int level = 1; level <= RenderLodLevels; level++)
        {
            _lod[level] = new Dictionary<(int, int, int), LodEntry>();
            _lodRequested[level] = new HashSet<(int, int, int)>();
        }
    }

    private (int X, int Y, int Z) LodCenter(int level) => (_center.X >> level, _center.Y >> level, _center.Z >> level);

    public (int Loaded, int Rendered, int LodLoaded, int LodRendered, int PendingMesh, int AwaitingNet, int Workers) Stats
    {
        get
        {
            int rendered = 0;
            foreach (var e in _chunks.Values)
            {
                if (e.State == MeshState.Done && (e.Solid is not null || e.LiquidSurface is not null || e.Translucent is not null)) rendered++;
            }
            int lodLoaded = 0, lodRendered = 0, awaiting = _requested.Count;
            for (int level = 1; level <= RenderLodLevels; level++)
            {
                lodLoaded += _lod[level].Count;
                awaiting += _lodRequested[level].Count;
                foreach (var e in _lod[level].Values)
                {
                    if (e.Solid is not null || e.LiquidSurface is not null || e.Translucent is not null) lodRendered++;
                }
            }
            return (_chunks.Count, rendered, lodLoaded, lodRendered, _pool.Pending, awaiting, _pool.WorkerCount);
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
            for (int level = 1; level <= RenderLodLevels; level++) _lodCursor[level] = 0;
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

    /// <summary>
    /// How many of a section's 8 child footprints have a finished finer
    /// stage (meshed or known-empty): level-1 children are chunks, higher
    /// levels' children are the sections one level below. Coverage is
    /// transitive — a Done child that is itself hidden by its own children
    /// still covers its footprint.
    /// </summary>
    private int CoveredChildren(int level, int sx, int sy, int sz)
    {
        int done = 0;
        for (int dy = 0; dy < 2; dy++)
        for (int dz = 0; dz < 2; dz++)
        for (int dx = 0; dx < 2; dx++)
        {
            var childKey = (sx * 2 + dx, sy * 2 + dy, sz * 2 + dz);
            if (level == 1)
            {
                if (_chunks.TryGetValue(childKey, out var chunk) && chunk.State == MeshState.Done) done++;
            }
            else if (_lod[level - 1].TryGetValue(childKey, out var child) && child.State == MeshState.Done)
            {
                done++;
            }
        }
        return done;
    }

    /// <summary>Depth-writing passes: a section draws while any child footprint is uncovered.</summary>
    public IEnumerable<(int Sx, int Sy, int Sz, ChunkMesh Mesh)> LodSolidMeshes(int level)
    {
        foreach (var e in _lod[level].Values)
        {
            if (e.Solid is not null && CoveredChildren(level, e.Sx, e.Sy, e.Sz) < 8) yield return (e.Sx, e.Sy, e.Sz, e.Solid);
        }
    }

    public IEnumerable<(int Sx, int Sy, int Sz, ChunkMesh Mesh)> LodLiquidSurfaceMeshes(int level)
    {
        foreach (var e in _lod[level].Values)
        {
            if (e.LiquidSurface is not null && CoveredChildren(level, e.Sx, e.Sy, e.Sz) < 8) yield return (e.Sx, e.Sy, e.Sz, e.LiquidSurface);
        }
    }

    /// <summary>
    /// The translucent pass has no depth writes, so overlapping levels would
    /// double-blend: a section's translucency draws only where nothing finer
    /// is finished at all.
    /// </summary>
    public IEnumerable<(int Sx, int Sy, int Sz, ChunkMesh Mesh)> LodTranslucentMeshes(int level)
    {
        foreach (var e in _lod[level].Values)
        {
            if (e.Translucent is not null && CoveredChildren(level, e.Sx, e.Sy, e.Sz) == 0) yield return (e.Sx, e.Sy, e.Sz, e.Translucent);
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

        if (chunk.LodLevel >= 1)
        {
            if (chunk.LodLevel > RenderLodLevels) return;
            var lod = _lod[chunk.LodLevel];
            _lodRequested[chunk.LodLevel].Remove(key);
            if (lod.ContainsKey(key)) return;
            var (scx, scy, scz) = LodCenter(chunk.LodLevel);
            int sdx = chunk.Cx - scx, sdy = chunk.Cy - scy, sdz = chunk.Cz - scz;
            if (sdx * sdx + sdy * sdy + sdz * sdz > LodUnloadOuter * LodUnloadOuter) return; // moved away meanwhile
            lod[key] = new LodEntry
            {
                Sx = chunk.Cx, Sy = chunk.Cy, Sz = chunk.Cz,
                Cells = chunk.Blocks,
                State = chunk.Blocks is null ? MeshState.Done : MeshState.None,
            };
            return;
        }

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
        int inFlight()
        {
            int n = _requested.Count;
            for (int level = 1; level <= RenderLodLevels; level++) n += _lodRequested[level].Count;
            return n;
        }

        List<(int, int, int)>? batch = null;
        while (_requestCursor < DataOffsets.Length &&
               inFlight() < MaxOutstandingRequests &&
               (batch?.Count ?? 0) < RequestBatch)
        {
            var (dx, dy, dz, _) = DataOffsets[_requestCursor];
            _requestCursor++;
            var key = (_center.X + dx, _center.Y + dy, _center.Z + dz);
            if (_chunks.ContainsKey(key) || _requested.Contains(key)) continue;
            _requested.Add(key);
            (batch ??= new List<(int, int, int)>()).Add(key);
        }
        if (batch is not null) _connection.RequestChunks(0, batch);

        // LOD levels stream fine-to-coarse on leftover budget only: never
        // before every finer cursor is exhausted, never past the shared cap.
        if (_requestCursor < DataOffsets.Length || batch is not null) return;
        for (int level = 1; level <= RenderLodLevels; level++)
        {
            var (scx, scy, scz) = LodCenter(level);
            List<(int, int, int)>? lodBatch = null;
            while (_lodCursor[level] < LodOffsets.Length &&
                   inFlight() < MaxOutstandingRequests &&
                   (lodBatch?.Count ?? 0) < RequestBatch)
            {
                var (dx, dy, dz, _) = LodOffsets[_lodCursor[level]];
                _lodCursor[level]++;
                var key = (scx + dx, scy + dy, scz + dz);
                if (_lod[level].ContainsKey(key) || _lodRequested[level].Contains(key)) continue;
                _lodRequested[level].Add(key);
                (lodBatch ??= new List<(int, int, int)>()).Add(key);
            }
            if (lodBatch is not null)
            {
                _connection.RequestChunks((byte)level, lodBatch);
                return; // this level consumed the budget; coarser levels wait
            }
            if (_lodCursor[level] < LodOffsets.Length) return; // capped out mid-level
        }
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

        for (int level = 1; level <= RenderLodLevels; level++) ScheduleLodMeshes(level);
    }

    private void ScheduleLodMeshes(int level)
    {
        var (scx, scy, scz) = LodCenter(level);
        foreach (var (dx, dy, dz, distSq) in LodOffsets)
        {
            if (distSq > LodRenderRadius * LodRenderRadius) break; // sorted; the rest is data-only shell
            if (_pool.Pending >= MaxPendingMeshJobs) break;
            var key = (scx + dx, scy + dy, scz + dz);
            if (!_lod[level].TryGetValue(key, out var entry) || entry.State != MeshState.None || entry.Cells is null) continue;

            var neighbors = new ushort[]?[6];
            bool ready = true;
            for (int i = 0; i < 6; i++)
            {
                var (nx, ny, nz) = NeighborOffsets[i];
                var nkey = (entry.Sx + nx, entry.Sy + ny, entry.Sz + nz);
                if (_lod[level].TryGetValue(nkey, out var n))
                {
                    neighbors[i] = n.Cells is null ? null : (ushort[])n.Cells.Clone();
                    continue;
                }
                // Inside the ring's inner hole nothing is ever requested:
                // stable air (the walls this bakes sit in the overlap region,
                // depth-covered by finer terrain). Anywhere else: wait.
                int ndx = dx + nx, ndy = dy + ny, ndz = dz + nz;
                if (ndx * ndx + ndy * ndy + ndz * ndz >= LodInnerRadius * LodInnerRadius)
                {
                    ready = false;
                    break;
                }
            }
            if (!ready) continue;

            entry.State = MeshState.Pending;
            _pool.Submit(new MesherPool.Job(key, entry.MeshVersion, (ushort[])entry.Cells.Clone(), neighbors, (byte)level));
        }
    }

    private void ApplyMeshResult(MesherPool.Completed completed)
    {
        if (completed.Job.Level >= 1)
        {
            if (!_lod[completed.Job.Level].TryGetValue(completed.Job.Key, out var lodEntry)) return; // unloaded meanwhile
            if (lodEntry.MeshVersion != completed.Job.Version) return;
            UploadMeshes(completed.Result, ref lodEntry.Solid, ref lodEntry.LiquidSurface, ref lodEntry.Translucent);
            lodEntry.State = MeshState.Done;
            return;
        }
        if (!_chunks.TryGetValue(completed.Job.Key, out var entry)) return;    // unloaded meanwhile
        if (entry.MeshVersion != completed.Job.Version) return;                // edited meanwhile
        UploadMeshes(completed.Result, ref entry.Solid, ref entry.LiquidSurface, ref entry.Translucent);
        entry.State = MeshState.Done;
    }

    private void UploadMeshes(MeshResult result, ref ChunkMesh? solid, ref ChunkMesh? liquidSurface, ref ChunkMesh? translucent)
    {
        solid?.Dispose();
        liquidSurface?.Dispose();
        translucent?.Dispose();
        solid = result.Solid is null ? null : new ChunkMesh(_gl, result.Solid);
        liquidSurface = result.LiquidSurface is null ? null : new ChunkMesh(_gl, result.LiquidSurface);
        translucent = result.Translucent is null ? null : new ChunkMesh(_gl, result.Translucent);
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

        // LOD sections shed both outward and inward (the ring follows the
        // player; stale sections deep inside the finer region are waste).
        for (int level = 1; level <= RenderLodLevels; level++)
        {
            var (scx, scy, scz) = LodCenter(level);
            List<(int, int, int)>? lodRemove = null;
            foreach (var (key, entry) in _lod[level])
            {
                int dx = entry.Sx - scx;
                int dy = entry.Sy - scy;
                int dz = entry.Sz - scz;
                int distSq = dx * dx + dy * dy + dz * dz;
                if (distSq <= LodUnloadOuter * LodUnloadOuter && distSq >= LodUnloadInner * LodUnloadInner) continue;
                entry.Solid?.Dispose();
                entry.LiquidSurface?.Dispose();
                entry.Translucent?.Dispose();
                entry.MeshVersion++;
                (lodRemove ??= new List<(int, int, int)>()).Add(key);
            }
            if (lodRemove is not null)
            {
                foreach (var key in lodRemove) _lod[level].Remove(key);
            }
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
        for (int level = 1; level <= RenderLodLevels; level++)
        {
            foreach (var entry in _lod[level].Values)
            {
                entry.Solid?.Dispose();
                entry.LiquidSurface?.Dispose();
                entry.Translucent?.Dispose();
            }
        }
        _pool.Dispose();
    }
}
