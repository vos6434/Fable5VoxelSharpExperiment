using Silk.NET.OpenGL;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Clustered colored block lights (plan 02 M4/M5). Loaded chunks are scanned
/// for light-emitting blocks (only blocks exposed to a non-opaque neighbor
/// count, so buried lava doesn't flood the registry); emitters distribute
/// into a world-space cluster grid (8^3 blocks per cluster) covering the same
/// region as the occupancy volume. Each cluster keeps its 8 strongest lights;
/// lights beyond the cap fold into an unshadowed per-cluster "overflow" color
/// so torch spam degrades gracefully instead of popping.
///
/// GPU layout (GL 3.3, no SSBOs): one RGBA32F 3D texture sized
/// (clusters, clusters, clusters * 9). Depth slice cz*9+s holds slot s of the
/// cluster column: texel = (position relative to the volume origin the build
/// used, packed color5:5:5 + intensity4 as an exact-integer float). Slice
/// cz*9+8 is the overflow RGB, clamped hue-preserving so emitter-dense areas
/// (hell lava) can't blow out to white.
///
/// Rebuilds run on a background task (hell regions hold thousands of
/// emitters; distributing them stalls a frame if done inline) over a snapshot
/// of the emitter lists; the finished texel buffer uploads on the GL thread.
/// The shader addresses the texture via <see cref="BuiltOriginWorld"/> — the
/// origin the *current texture contents* were built with — so an in-flight
/// rebuild after a recenter never shifts existing lights.
/// </summary>
public sealed class LightVolume : IDisposable
{
    private const int ClusterBlocks = 8;
    private const int Slots = 8;
    private const int Rows = Slots + 1; // + overflow row
    private const double RebuildDebounce = 0.2;
    /// <summary>Max per-channel overflow light — beyond this the cluster is "saturated bright".</summary>
    private const float OverflowCap = 1.25f;

    public readonly record struct Emitter(float X, float Y, float Z, float R, float G, float B, int Intensity);

    private readonly GL _gl;
    private readonly uint _texture;
    private readonly int _regionChunks;
    private readonly int _radiusChunks;
    private readonly int _clusters; // per edge

    // Per-block-id emission tables derived from the registry.
    private readonly byte[] _emission;
    private readonly float[] _colR, _colG, _colB;
    private readonly byte[] _opaque;

    private readonly float[] _texData;
    private readonly float[] _slotKey;
    private readonly Emitter[] _slotData;
    private readonly byte[] _slotCount;

    private readonly Dictionary<(int, int, int), List<Emitter>> _emitters = new();
    private readonly HashSet<(int, int, int)> _scanned = new();
    private readonly HashSet<(int, int, int)> _dirty = new();

    private (int X, int Y, int Z) _originChunk = (int.MinValue, 0, 0);
    private bool _rebuildNeeded;
    private double _cooldown;

    // Background rebuild state (single-flight): the task fills _texData and
    // sets _buildDone; the GL thread uploads and publishes the built origin.
    private Task? _buildTask;
    private (int X, int Y, int Z) _buildOrigin;
    private (int X, int Y, int Z) _builtOrigin;
    private volatile bool _buildDone;

    public int Clusters => _clusters;

    /// <summary>World min-corner the current texture contents are relative to (shader's uLightsOrigin).</summary>
    public (float X, float Y, float Z) BuiltOriginWorld => (
        _builtOrigin.X * Constants.ChunkSize,
        _builtOrigin.Y * Constants.ChunkSize,
        _builtOrigin.Z * Constants.ChunkSize);

    public LightVolume(GL gl, BlockRegistry blocks, int regionRadiusChunks)
    {
        _gl = gl;
        _radiusChunks = regionRadiusChunks;
        _regionChunks = regionRadiusChunks * 2 + 1;
        _clusters = _regionChunks * Constants.ChunkSize / ClusterBlocks;
        _opaque = blocks.Opaque;

        _emission = new byte[blocks.Count];
        _colR = new float[blocks.Count];
        _colG = new float[blocks.Count];
        _colB = new float[blocks.Count];
        foreach (var def in blocks.Defs)
        {
            _emission[def.NumericId] = (byte)def.LightEmission;
            _colR[def.NumericId] = def.LightColor.R;
            _colG[def.NumericId] = def.LightColor.G;
            _colB[def.NumericId] = def.LightColor.B;
        }

        int c3 = _clusters * _clusters * _clusters;
        _texData = new float[c3 * Rows * 4];
        _slotKey = new float[c3 * Slots];
        _slotData = new Emitter[c3 * Slots];
        _slotCount = new byte[c3];

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture3D, _texture);
        unsafe
        {
            gl.TexImage3D(TextureTarget.Texture3D, 0, (int)InternalFormat.Rgba32f,
                (uint)_clusters, (uint)_clusters, (uint)(_clusters * Rows), 0,
                PixelFormat.Rgba, PixelType.Float, null);
        }
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        Upload(); // zero-fill: TexImage3D with null data leaves texels undefined
    }

    public void Bind(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture3D, _texture);
    }

    /// <summary>Follows the occupancy volume's origin so cluster coords line up in the shader.</summary>
    public void Update(StreamingWorld world, (int X, int Y, int Z) originChunk, double dt, int scanBudget = 24)
    {
        if (originChunk != _originChunk)
        {
            _originChunk = originChunk;
            bool InRegion((int, int, int) c) =>
                c.Item1 >= originChunk.X && c.Item1 < originChunk.X + _regionChunks &&
                c.Item2 >= originChunk.Y && c.Item2 < originChunk.Y + _regionChunks &&
                c.Item3 >= originChunk.Z && c.Item3 < originChunk.Z + _regionChunks;
            _scanned.RemoveWhere(c => !InRegion(c));
            var gone = _emitters.Keys.Where(c => !InRegion(c)).ToList();
            foreach (var key in gone) _emitters.Remove(key);
            _rebuildNeeded = true;
            _cooldown = 0; // recenter must not lag behind the occupancy scroll
        }

        // Edited chunks rescan; streamed-in chunks scan on a budget.
        world.DrainLightDirty(_dirty);
        foreach (var key in _dirty) _scanned.Remove(key);
        _dirty.Clear();

        int scanned = 0;
        for (int dx = 0; dx < _regionChunks && scanned < scanBudget; dx++)
            for (int dy = 0; dy < _regionChunks && scanned < scanBudget; dy++)
                for (int dz = 0; dz < _regionChunks && scanned < scanBudget; dz++)
                {
                    var key = (_originChunk.X + dx, _originChunk.Y + dy, _originChunk.Z + dz);
                    if (_scanned.Contains(key)) continue;
                    var blocks = world.ChunkBlocks(key.Item1, key.Item2, key.Item3);
                    if (blocks is null) continue;
                    scanned++;
                    _scanned.Add(key);
                    var found = ScanChunk(key.Item1, key.Item2, key.Item3, blocks);
                    bool had = _emitters.TryGetValue(key, out var old);
                    if (found is null)
                    {
                        if (had) { _emitters.Remove(key); _rebuildNeeded = true; }
                    }
                    else if (!had || !found.SequenceEqual(old!))
                    {
                        _emitters[key] = found;
                        _rebuildNeeded = true;
                    }
                }

        // Publish a finished background build (the GL upload must happen here,
        // on the GL thread).
        if (_buildTask is not null && _buildDone)
        {
            _buildTask = null;
            _buildDone = false;
            Upload();
            _builtOrigin = _buildOrigin;
        }

        _cooldown -= dt;
        if (_rebuildNeeded && _cooldown <= 0 && _buildTask is null)
        {
            // Snapshot the emitters: the build runs off-thread while scans
            // keep mutating the per-chunk lists.
            int total = 0;
            foreach (var list in _emitters.Values) total += list.Count;
            var snapshot = new Emitter[total];
            int n = 0;
            foreach (var list in _emitters.Values)
            {
                list.CopyTo(snapshot, n);
                n += list.Count;
            }

            _buildOrigin = _originChunk;
            _rebuildNeeded = false;
            _cooldown = RebuildDebounce;
            _buildTask = Task.Run(() =>
            {
                Rebuild(snapshot);
                _buildDone = true;
            });
        }
    }

    private List<Emitter>? ScanChunk(int cx, int cy, int cz, ushort[] blocks)
    {
        const int S = Constants.ChunkSize;
        List<Emitter>? found = null;
        for (int i = 0; i < blocks.Length; i++)
        {
            ushort id = blocks[i];
            if (id == 0 || _emission[id] == 0) continue;

            // ChunkData order: index = (y*S + z)*S + x.
            int x = i & (S - 1), z = (i >> 4) & (S - 1), y = i >> 8;

            // Only exposed emitters light anything; buried ones (lava oceans)
            // are skipped. Chunk-border blocks count as exposed.
            bool exposed =
                x == 0 || x == S - 1 || y == 0 || y == S - 1 || z == 0 || z == S - 1 ||
                _opaque[blocks[i + 1]] == 0 || _opaque[blocks[i - 1]] == 0 ||
                _opaque[blocks[i + S * S]] == 0 || _opaque[blocks[i - S * S]] == 0 ||
                _opaque[blocks[i + S]] == 0 || _opaque[blocks[i - S]] == 0;
            if (!exposed) continue;

            // Bulk thinning (hell lava oceans): a block with 3+ same-id
            // emitting neighbors is part of a contiguous glowing field; keep
            // only 1 in 4 (world-parity, so it's stable across rescans).
            // Isolated emitters (torches, single glowstone) are never thinned.
            int wx = cx * S + x, wy = cy * S + y, wz = cz * S + z;
            if ((wx & 1) != 0 || (wz & 1) != 0)
            {
                int same = 0;
                if (x < S - 1 && blocks[i + 1] == id) same++;
                if (x > 0 && blocks[i - 1] == id) same++;
                if (y < S - 1 && blocks[i + S * S] == id) same++;
                if (y > 0 && blocks[i - S * S] == id) same++;
                if (z < S - 1 && blocks[i + S] == id) same++;
                if (z > 0 && blocks[i - S] == id) same++;
                if (same >= 3) continue;
            }

            (found ??= new List<Emitter>()).Add(new Emitter(
                wx + 0.5f, wy + 0.5f, wz + 0.5f,
                _colR[id], _colG[id], _colB[id], _emission[id]));
        }
        return found;
    }

    private void Rebuild(Emitter[] emitters)
    {
        Array.Clear(_texData);
        Array.Clear(_slotKey);
        Array.Clear(_slotCount);

        const int S = Constants.ChunkSize;
        float ox = _buildOrigin.X * S, oy = _buildOrigin.Y * S, oz = _buildOrigin.Z * S;
        // A light reaches a cluster if any point of it is within the radius:
        // center distance <= radius + half cluster diagonal.
        float clusterReach = ClusterBlocks * 0.5f * MathF.Sqrt(3f);

        foreach (var e in emitters)
        {
            float rx = e.X - ox, ry = e.Y - oy, rz = e.Z - oz;
            float radius = e.Intensity;
            int c0x = Math.Max(0, (int)((rx - radius) / ClusterBlocks));
            int c0y = Math.Max(0, (int)((ry - radius) / ClusterBlocks));
            int c0z = Math.Max(0, (int)((rz - radius) / ClusterBlocks));
            int c1x = Math.Min(_clusters - 1, (int)((rx + radius) / ClusterBlocks));
            int c1y = Math.Min(_clusters - 1, (int)((ry + radius) / ClusterBlocks));
            int c1z = Math.Min(_clusters - 1, (int)((rz + radius) / ClusterBlocks));

            for (int cz = c0z; cz <= c1z; cz++)
                for (int cy = c0y; cy <= c1y; cy++)
                    for (int cx = c0x; cx <= c1x; cx++)
                    {
                        float mx = (cx + 0.5f) * ClusterBlocks - rx;
                        float my = (cy + 0.5f) * ClusterBlocks - ry;
                        float mz = (cz + 0.5f) * ClusterBlocks - rz;
                        float dist = MathF.Sqrt(mx * mx + my * my + mz * mz);
                        if (dist > radius + clusterReach) continue;
                        Insert((cz * _clusters + cy) * _clusters + cx, e, e.Intensity / (1f + dist));
                    }
        }

        WriteTexels();
    }

    private void Insert(int cluster, in Emitter e, float key)
    {
        int baseSlot = cluster * Slots;
        int count = _slotCount[cluster];
        if (count < Slots)
        {
            _slotKey[baseSlot + count] = key;
            _slotData[baseSlot + count] = e;
            _slotCount[cluster] = (byte)(count + 1);
            return;
        }
        // Full: evict the weakest (either the incoming light or a slot) into
        // the cluster's unshadowed overflow color.
        int weakest = 0;
        for (int s = 1; s < Slots; s++)
        {
            if (_slotKey[baseSlot + s] < _slotKey[baseSlot + weakest]) weakest = s;
        }
        if (key > _slotKey[baseSlot + weakest])
        {
            AddOverflow(cluster, _slotData[baseSlot + weakest]);
            _slotKey[baseSlot + weakest] = key;
            _slotData[baseSlot + weakest] = e;
        }
        else
        {
            AddOverflow(cluster, e);
        }
    }

    private void AddOverflow(int cluster, in Emitter e)
    {
        int cx = cluster % _clusters;
        int cy = cluster / _clusters % _clusters;
        int cz = cluster / (_clusters * _clusters);
        const int S = Constants.ChunkSize;
        float mx = (cx + 0.5f) * ClusterBlocks - (e.X - _buildOrigin.X * S);
        float my = (cy + 0.5f) * ClusterBlocks - (e.Y - _buildOrigin.Y * S);
        float mz = (cz + 0.5f) * ClusterBlocks - (e.Z - _buildOrigin.Z * S);
        float dist = MathF.Sqrt(mx * mx + my * my + mz * mz);
        float falloff = Math.Max(0f, 1f - dist / e.Intensity) * (e.Intensity / 15f);
        if (falloff <= 0) return;
        int t = TexelIndex(cx, cy, cz, Slots);
        _texData[t + 0] += e.R * falloff;
        _texData[t + 1] += e.G * falloff;
        _texData[t + 2] += e.B * falloff;
    }

    /// <summary>Texel float offset for (cluster x, y, z, row): GL order x fastest, then y, then depth slice cz*Rows+row.</summary>
    private int TexelIndex(int cx, int cy, int cz, int row)
        => (((cz * Rows + row) * _clusters + cy) * _clusters + cx) * 4;

    private void WriteTexels()
    {
        const int S = Constants.ChunkSize;
        float ox = _buildOrigin.X * S, oy = _buildOrigin.Y * S, oz = _buildOrigin.Z * S;
        for (int cz = 0; cz < _clusters; cz++)
            for (int cy = 0; cy < _clusters; cy++)
                for (int cx = 0; cx < _clusters; cx++)
                {
                    int cluster = (cz * _clusters + cy) * _clusters + cx;

                    // Clamp the accumulated overflow hue-preserving: hundreds
                    // of folded lava lights must read "saturated warm", not white.
                    int ot = TexelIndex(cx, cy, cz, Slots);
                    float peak = Math.Max(_texData[ot], Math.Max(_texData[ot + 1], _texData[ot + 2]));
                    if (peak > OverflowCap)
                    {
                        float k = OverflowCap / peak;
                        _texData[ot] *= k;
                        _texData[ot + 1] *= k;
                        _texData[ot + 2] *= k;
                    }

                    int count = _slotCount[cluster];
                    for (int s = 0; s < count; s++)
                    {
                        var e = _slotData[cluster * Slots + s];
                        // Color (5 bits/channel) + intensity (4 bits) packed as an
                        // exact-integer float (< 2^24, so float32 stores it losslessly
                        // and the shader can decode with floor/mod — no bit ops, no
                        // NaN-pattern risk through the RGBA32F texture).
                        int r5 = (int)(Math.Clamp(e.R, 0f, 1f) * 31f + 0.5f);
                        int g5 = (int)(Math.Clamp(e.G, 0f, 1f) * 31f + 0.5f);
                        int b5 = (int)(Math.Clamp(e.B, 0f, 1f) * 31f + 0.5f);
                        float packed = ((r5 * 32 + g5) * 32 + b5) * 16 + e.Intensity;
                        int t = TexelIndex(cx, cy, cz, s);
                        _texData[t + 0] = e.X - ox;
                        _texData[t + 1] = e.Y - oy;
                        _texData[t + 2] = e.Z - oz;
                        _texData[t + 3] = packed;
                    }
                }
    }

    private unsafe void Upload()
    {
        _gl.BindTexture(TextureTarget.Texture3D, _texture);
        fixed (float* p = _texData)
        {
            _gl.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                (uint)_clusters, (uint)_clusters, (uint)(_clusters * Rows),
                PixelFormat.Rgba, PixelType.Float, p);
        }
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
