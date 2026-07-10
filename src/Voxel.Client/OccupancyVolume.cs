using Silk.NET.OpenGL;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// GPU occupancy of the world around the camera — one byte per voxel in an
/// R8 3D texture used by shadow rays (plan 02 M3+):
///   0   = passable
///   128 = emissive solid (blocks skylight; sun/moon and block-light rays pass through)
///   255 = opaque solid
///
/// Addressing is toroidal (per plan 02): world chunk c occupies texture slab
/// c mod RegionChunks, so recentering on a chunk crossing keeps every chunk
/// that is still in range in place — only the newly-entered rim is zeroed and
/// re-uploaded, instead of wiping the whole volume. The shader mirrors this
/// by fetching at (world voxel mod Size).
///
/// Texture axes map directly to world XYZ. GL 3D data order is x-fastest then
/// y then z, which differs from ChunkData's (x, z, y) order, so each chunk is
/// transposed on upload.
/// </summary>
public sealed class OccupancyVolume : IDisposable
{
    private const int S = Constants.ChunkSize;

    private readonly int RegionRadiusChunks;
    private readonly int RegionChunks;
    /// <summary>Edge length in voxels (region size is a quality setting, plan 02 M8).</summary>
    public int Size { get; }

    private readonly GL _gl;
    private const byte OccEmissive = 128;
    private const byte OccOpaque = 255;

    private readonly byte[] _opaque;
    private readonly byte[] _emissive;
    private readonly uint _texture;
    private readonly byte[] _scratch = new byte[S * S * S];
    private readonly byte[] _zeroSlab = new byte[S * S * S];
    private readonly byte[] _zero;
    private readonly (int Dx, int Dy, int Dz)[] _localChunks;
    private readonly HashSet<(int, int, int)> _uploaded = new();
    private readonly HashSet<(int, int, int)> _dirty = new();

    /// <summary>World y of the highest solid voxel per uploaded chunk — feeds ContentTopWorldY.</summary>
    private readonly Dictionary<(int, int, int), int> _chunkTopY = new();
    private int _contentTopY = int.MinValue;
    private bool _contentTopDirty;

    private (int X, int Y, int Z) _originChunk = (int.MinValue, 0, 0);
    private int _cursor;

    public OccupancyVolume(GL gl, byte[] opaqueTable, byte[] emissiveMask, int regionRadiusChunks)
    {
        _gl = gl;
        _opaque = opaqueTable;
        _emissive = emissiveMask;
        RegionRadiusChunks = regionRadiusChunks;
        RegionChunks = regionRadiusChunks * 2 + 1;
        Size = RegionChunks * S;
        _zero = new byte[Size * Size * Size];

        // Region chunk offsets, sorted nearest-center-first so shadows near the
        // player fill in before the edges.
        var list = new List<(int, int, int)>();
        for (int dx = 0; dx < RegionChunks; dx++)
            for (int dy = 0; dy < RegionChunks; dy++)
                for (int dz = 0; dz < RegionChunks; dz++)
                    list.Add((dx, dy, dz));
        int c = RegionRadiusChunks;
        list.Sort((a, b) =>
            ((a.Item1 - c) * (a.Item1 - c) + (a.Item2 - c) * (a.Item2 - c) + (a.Item3 - c) * (a.Item3 - c))
            .CompareTo((b.Item1 - c) * (b.Item1 - c) + (b.Item2 - c) * (b.Item2 - c) + (b.Item3 - c) * (b.Item3 - c)));
        _localChunks = [.. list];

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture3D, _texture);
        unsafe
        {
            gl.TexImage3D(TextureTarget.Texture3D, 0, (int)InternalFormat.R8,
                (uint)Size, (uint)Size, (uint)Size, 0, PixelFormat.Red, PixelType.UnsignedByte, null);
        }
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
    }

    /// <summary>World min-corner of the volume (for the shader's uOccupancyOrigin).</summary>
    public (float X, float Y, float Z) OriginWorld =>
        (_originChunk.X * S, _originChunk.Y * S, _originChunk.Z * S);

    /// <summary>Min-corner chunk coordinate (the light volume follows this).</summary>
    public (int X, int Y, int Z) OriginChunk => _originChunk;

    /// <summary>
    /// World y of the highest solid voxel currently in the volume (int.MinValue
    /// when empty). Shadow/skylight marches exit once a ray rises above it —
    /// over open terrain that cuts ~90-step marches to a handful (perf fix,
    /// found at 14 fps over the ocean where every water + seabed fragment paid
    /// two near-full marches).
    /// </summary>
    public int ContentTopWorldY
    {
        get
        {
            if (_contentTopDirty)
            {
                _contentTopDirty = false;
                _contentTopY = int.MinValue;
                foreach (int top in _chunkTopY.Values)
                {
                    if (top > _contentTopY) _contentTopY = top;
                }
            }
            return _contentTopY;
        }
    }

    public void Bind(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture3D, _texture);
    }

    public void Update(StreamingWorld world, double camX, double camY, double camZ, int uploadBudget)
    {
        var desired = (
            Coords.WorldToChunk(camX) - RegionRadiusChunks,
            Coords.WorldToChunk(camY) - RegionRadiusChunks,
            Coords.WorldToChunk(camZ) - RegionRadiusChunks);

        if (desired != _originChunk) Recenter(desired);

        // Edits: re-upload touched chunks that fall inside the region.
        world.DrainOccupancyDirty(_dirty);
        foreach (var key in _dirty) _uploaded.Remove(key);
        _dirty.Clear();

        // Upload a budget of not-yet-present in-region chunks (nearest-first via cursor order).
        int uploaded = 0;
        for (int scanned = 0; scanned < _localChunks.Length && uploaded < uploadBudget; scanned++)
        {
            var (dx, dy, dz) = _localChunks[_cursor];
            _cursor = (_cursor + 1) % _localChunks.Length;
            var world_ = (_originChunk.X + dx, _originChunk.Y + dy, _originChunk.Z + dz);
            if (_uploaded.Contains(world_)) continue;
            var blocks = world.ChunkBlocks(world_.Item1, world_.Item2, world_.Item3);
            if (blocks is null) continue;
            UploadChunk(world_.Item1, world_.Item2, world_.Item3, blocks);
            _uploaded.Add(world_);
            uploaded++;
        }
    }

    private void Recenter((int X, int Y, int Z) origin)
    {
        var old = _originChunk;
        _originChunk = origin;
        _cursor = 0; // restart the nearest-first scan around the new center

        bool overlaps = old.X != int.MinValue
            && Math.Abs(origin.X - old.X) < RegionChunks
            && Math.Abs(origin.Y - old.Y) < RegionChunks
            && Math.Abs(origin.Z - old.Z) < RegionChunks;
        if (!overlaps)
        {
            // Teleport / first update: nothing carries over.
            ClearTexture();
            _uploaded.Clear();
            _chunkTopY.Clear();
            _contentTopDirty = true;
            return;
        }

        // Toroidal scroll: chunks still in range keep their slab. Drop the ones
        // that scrolled out and zero the slabs of newly-entered coords (they
        // alias the departed chunks' slabs) so stale occupancy can't shadow.
        bool OutOfRegion((int, int, int) c) =>
            c.Item1 < origin.X || c.Item1 >= origin.X + RegionChunks ||
            c.Item2 < origin.Y || c.Item2 >= origin.Y + RegionChunks ||
            c.Item3 < origin.Z || c.Item3 >= origin.Z + RegionChunks;
        _uploaded.RemoveWhere(OutOfRegion);
        List<(int, int, int)>? evict = null;
        foreach (var key in _chunkTopY.Keys)
        {
            if (OutOfRegion(key)) (evict ??= new List<(int, int, int)>()).Add(key);
        }
        if (evict is not null)
        {
            foreach (var key in evict) _chunkTopY.Remove(key);
            _contentTopDirty = true;
        }
        for (int dx = 0; dx < RegionChunks; dx++)
            for (int dy = 0; dy < RegionChunks; dy++)
                for (int dz = 0; dz < RegionChunks; dz++)
                {
                    int cx = origin.X + dx, cy = origin.Y + dy, cz = origin.Z + dz;
                    bool wasInside =
                        cx >= old.X && cx < old.X + RegionChunks &&
                        cy >= old.Y && cy < old.Y + RegionChunks &&
                        cz >= old.Z && cz < old.Z + RegionChunks;
                    if (!wasInside) ZeroSlab(cx, cy, cz);
                }
    }

    private static int Mod(int v, int m) => (v % m + m) % m;

    private unsafe void ZeroSlab(int cx, int cy, int cz)
    {
        _gl.BindTexture(TextureTarget.Texture3D, _texture);
        fixed (byte* p = _zeroSlab)
        {
            _gl.TexSubImage3D(TextureTarget.Texture3D, 0,
                Mod(cx, RegionChunks) * S, Mod(cy, RegionChunks) * S, Mod(cz, RegionChunks) * S,
                S, S, S, PixelFormat.Red, PixelType.UnsignedByte, p);
        }
    }

    private unsafe void UploadChunk(int cx, int cy, int cz, ushort[] blocks)
    {
        // Transpose ChunkData order (x fastest, then z, then y) into GL 3D order
        // (x fastest, then y, then z), writing occupancy per voxel and tracking
        // the chunk's highest solid for ContentTopWorldY.
        int topLocalY = -1;
        for (int y = 0; y < S; y++)
            for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                {
                    byte occ = OccVoxel(blocks[(y * S + z) * S + x]);
                    _scratch[(z * S + y) * S + x] = occ;
                    if (occ != 0) topLocalY = y;
                }
        if (topLocalY >= 0) _chunkTopY[(cx, cy, cz)] = cy * S + topLocalY;
        else _chunkTopY.Remove((cx, cy, cz));
        _contentTopDirty = true;

        _gl.BindTexture(TextureTarget.Texture3D, _texture);
        fixed (byte* p = _scratch)
        {
            _gl.TexSubImage3D(TextureTarget.Texture3D, 0,
                Mod(cx, RegionChunks) * S, Mod(cy, RegionChunks) * S, Mod(cz, RegionChunks) * S,
                S, S, S, PixelFormat.Red, PixelType.UnsignedByte, p);
        }
    }

    private unsafe void ClearTexture()
    {
        _gl.BindTexture(TextureTarget.Texture3D, _texture);
        fixed (byte* p = _zero)
        {
            _gl.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, (uint)Size, (uint)Size, (uint)Size,
                PixelFormat.Red, PixelType.UnsignedByte, p);
        }
    }

    private byte OccVoxel(ushort id)
    {
        if (_opaque[id] == 0) return 0;
        return _emissive[id] != 0 ? OccEmissive : OccOpaque;
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
