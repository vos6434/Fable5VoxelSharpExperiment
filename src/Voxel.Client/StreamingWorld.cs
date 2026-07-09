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
/// LOD1 ring (plan 04 M2): beyond the full-detail sphere a second ring of
/// server-downsampled 8³ chunks streams to Lod1RenderRadius, band-limited to
/// ChunkLod's vertical band, requested only on leftover budget (LOD0 keeps
/// absolute priority). A LOD1 mesh renders only while the chunk has no
/// full-detail mesh, so ring transitions swap without holes.
/// </summary>
public sealed class StreamingWorld : IDisposable
{
    public const int RenderRadius = 8;                   // LOD0, full detail (plan 04: up from 6)
    private const int DataRadius = RenderRadius + 1;
    private const int UnloadRadius = DataRadius + 2;
    public const int Lod1RenderRadius = 16;
    private const int Lod1DataRadius = Lod1RenderRadius + 1;
    private const int Lod1UnloadRadius = Lod1DataRadius + 2;
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
        public required int Cx, Cy, Cz;
        public ushort[]? Cells;   // 8³ LOD1 cells; null = all air
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
    /// <summary>
    /// Inside this distance a LOD1 mesh renders even under a full-detail mesh
    /// (depth-biased behind it). At the ring boundary the fine mesh's step
    /// faces were culled against full-detail neighbor data that isn't what
    /// actually renders next to it — the coarse copy underneath seals those
    /// see-through cracks.
    /// </summary>
    private const int Lod1UnderlapRadius = RenderRadius + 2;

    /// <summary>LOD1 shell: one chunk inside the full-detail boundary (underlap) out to its data radius.</summary>
    private static readonly (int Dx, int Dy, int Dz, int DistSq)[] Lod1Offsets =
        [.. SphereOffsets(Lod1DataRadius).Where(o => o.Item4 >= (RenderRadius - 1) * (RenderRadius - 1))];

    private readonly GL _gl;
    private readonly Connection _connection;
    private readonly MesherPool _pool;
    private readonly ushort _waterId;
    private readonly Dictionary<(int, int, int), Entry> _chunks = new();
    private readonly Dictionary<(int, int, int), LodEntry> _lod1 = new();
    private readonly HashSet<(int, int, int)> _requested = new();
    private readonly HashSet<(int, int, int)> _lod1Requested = new();
    private int _requestCursor;
    private int _lod1RequestCursor;
    private (int X, int Y, int Z) _center = (int.MinValue, 0, 0);

    public string? DisconnectReason { get; private set; }

    public StreamingWorld(GL gl, ClientData data, Connection connection)
    {
        _gl = gl;
        _connection = connection;
        _waterId = data.Blocks.Resolve("water");
        _pool = new MesherPool(data.Blocks.Opaque, data.RenderTable, data.TranslucentMask, data.FlatAoMask, data.EmissiveMask);
    }

    public (int Loaded, int Rendered, int Lod1Loaded, int Lod1Rendered, int PendingMesh, int AwaitingNet, int Workers) Stats
    {
        get
        {
            int rendered = 0;
            foreach (var e in _chunks.Values)
            {
                if (e.State == MeshState.Done && (e.Solid is not null || e.LiquidSurface is not null || e.Translucent is not null)) rendered++;
            }
            int lodRendered = 0;
            foreach (var e in _lod1.Values)
            {
                if (e.Solid is not null || e.LiquidSurface is not null || e.Translucent is not null) lodRendered++;
            }
            return (_chunks.Count, rendered, _lod1.Count, lodRendered, _pool.Pending,
                _requested.Count + _lod1Requested.Count, _pool.WorkerCount);
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
            _lod1RequestCursor = 0;
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

    /// <summary>A LOD1 mesh yields while its chunk shows no full-detail mesh (transition without holes).</summary>
    private bool Lod1Visible(LodEntry e) =>
        !(_chunks.TryGetValue((e.Cx, e.Cy, e.Cz), out var full) && full.State == MeshState.Done);

    /// <summary>
    /// Depth-writing passes (solid, liquid surface) additionally render inside
    /// the underlap shell even under a full-detail mesh — the coarse copy is
    /// depth-biased behind, so it only shows through boundary cracks and seals
    /// them. The translucent pass must not: it has no depth writes, so a
    /// second layer would double-blend into a visibly lighter band.
    /// </summary>
    private bool Lod1UnderlapVisible(LodEntry e)
    {
        int dx = e.Cx - _center.X, dy = e.Cy - _center.Y, dz = e.Cz - _center.Z;
        int distSq = dx * dx + dy * dy + dz * dz;
        if (distSq >= (RenderRadius - 1) * (RenderRadius - 1) &&
            distSq <= Lod1UnderlapRadius * Lod1UnderlapRadius)
        {
            return true;
        }
        return Lod1Visible(e);
    }

    public IEnumerable<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> Lod1SolidMeshes()
    {
        foreach (var e in _lod1.Values)
        {
            if (e.Solid is not null && Lod1UnderlapVisible(e)) yield return (e.Cx, e.Cy, e.Cz, e.Solid);
        }
    }

    public IEnumerable<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> Lod1LiquidSurfaceMeshes()
    {
        foreach (var e in _lod1.Values)
        {
            if (e.LiquidSurface is not null && Lod1UnderlapVisible(e)) yield return (e.Cx, e.Cy, e.Cz, e.LiquidSurface);
        }
    }

    public IEnumerable<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> Lod1TranslucentMeshes()
    {
        foreach (var e in _lod1.Values)
        {
            if (e.Translucent is not null && Lod1Visible(e)) yield return (e.Cx, e.Cy, e.Cz, e.Translucent);
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
        int dx = chunk.Cx - _center.X, dy = chunk.Cy - _center.Y, dz = chunk.Cz - _center.Z;
        int distSq = dx * dx + dy * dy + dz * dz;

        if (chunk.LodLevel == 1)
        {
            _lod1Requested.Remove(key);
            if (_lod1.ContainsKey(key)) return;
            if (distSq > Lod1UnloadRadius * Lod1UnloadRadius) return; // moved away meanwhile
            _lod1[key] = new LodEntry
            {
                Cx = chunk.Cx, Cy = chunk.Cy, Cz = chunk.Cz,
                Cells = chunk.Blocks,
                State = chunk.Blocks is null ? MeshState.Done : MeshState.None,
            };
            return;
        }
        if (chunk.LodLevel != 0) return;

        _requested.Remove(key);
        if (_chunks.ContainsKey(key)) return;
        if (distSq > UnloadRadius * UnloadRadius) return; // moved away meanwhile

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
        List<(int, int, int)>? batch = null;
        while (_requestCursor < DataOffsets.Length &&
               _requested.Count + _lod1Requested.Count < MaxOutstandingRequests &&
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

        // LOD1 streams only on leftover budget: never before the full-detail
        // sphere has been fully requested, never past the shared in-flight cap.
        if (_requestCursor < DataOffsets.Length || batch is not null) return;
        List<(int, int, int)>? lodBatch = null;
        while (_lod1RequestCursor < Lod1Offsets.Length &&
               _requested.Count + _lod1Requested.Count < MaxOutstandingRequests &&
               (lodBatch?.Count ?? 0) < RequestBatch)
        {
            var (dx, dy, dz, _) = Lod1Offsets[_lod1RequestCursor];
            _lod1RequestCursor++;
            int cy = _center.Y + dy;
            if (!ChunkLod.InBand(cy)) continue; // distant hell/deep stone is invisible anyway
            var key = (_center.X + dx, cy, _center.Z + dz);
            if (_lod1.ContainsKey(key) || _lod1Requested.Contains(key)) continue;
            _lod1Requested.Add(key);
            (lodBatch ??= new List<(int, int, int)>()).Add(key);
        }
        if (lodBatch is not null) _connection.RequestChunks(1, lodBatch);
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
        ScheduleLod1Meshes();
    }

    private void ScheduleLod1Meshes()
    {
        foreach (var (dx, dy, dz, distSq) in Lod1Offsets)
        {
            if (distSq > Lod1RenderRadius * Lod1RenderRadius) break; // sorted; the rest is data-only shell
            if (_pool.Pending >= MaxPendingMeshJobs) break;
            var key = (_center.X + dx, _center.Y + dy, _center.Z + dz);
            if (!_lod1.TryGetValue(key, out var entry) || entry.State != MeshState.None || entry.Cells is null) continue;

            var neighbors = new ushort[]?[6];
            bool ready = true;
            for (int i = 0; i < 6; i++)
            {
                var (nx, ny, nz) = NeighborOffsets[i];
                var nkey = (entry.Cx + nx, entry.Cy + ny, entry.Cz + nz);
                if (!ChunkLod.InBand(nkey.Item2)) continue; // outside the band = air, never requested
                if (_lod1.TryGetValue(nkey, out var ln))
                {
                    neighbors[i] = ln.Cells is null ? null : (ushort[])ln.Cells.Clone();
                }
                else if (_chunks.TryGetValue(nkey, out var full))
                {
                    // Inner boundary: derive the coarse neighbor from full-detail
                    // data with the same downsampler the server uses, so
                    // cross-ring face culling is consistent.
                    neighbors[i] = full.Empty ? null : ChunkLod.Downsample(full.Data.Blocks, 1, _waterId);
                }
                else
                {
                    ready = false;
                    break;
                }
            }
            if (!ready) continue;

            entry.State = MeshState.Pending;
            _pool.Submit(new MesherPool.Job(key, entry.MeshVersion, (ushort[])entry.Cells.Clone(), neighbors, Level: 1));
        }
    }

    private void ApplyMeshResult(MesherPool.Completed completed)
    {
        if (completed.Job.Level == 1)
        {
            if (!_lod1.TryGetValue(completed.Job.Key, out var lodEntry)) return; // unloaded meanwhile
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

        // LOD1 sheds both ways: past the outer hysteresis ring, and deep inside
        // the full-detail sphere where the player has moved (entries are only
        // requested from RenderRadius−1 outward; 2 chunks of inner hysteresis).
        int lodInnerUnload = (RenderRadius - 3) * (RenderRadius - 3);
        List<(int, int, int)>? lodRemove = null;
        foreach (var (key, entry) in _lod1)
        {
            int dx = entry.Cx - _center.X;
            int dy = entry.Cy - _center.Y;
            int dz = entry.Cz - _center.Z;
            int distSq = dx * dx + dy * dy + dz * dz;
            if (distSq <= Lod1UnloadRadius * Lod1UnloadRadius && distSq >= lodInnerUnload) continue;
            entry.Solid?.Dispose();
            entry.LiquidSurface?.Dispose();
            entry.Translucent?.Dispose();
            entry.MeshVersion++;
            (lodRemove ??= new List<(int, int, int)>()).Add(key);
        }
        if (lodRemove is not null)
        {
            foreach (var key in lodRemove) _lod1.Remove(key);
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
        foreach (var entry in _lod1.Values)
        {
            entry.Solid?.Dispose();
            entry.LiquidSurface?.Dispose();
            entry.Translucent?.Dispose();
        }
        _pool.Dispose();
    }
}
