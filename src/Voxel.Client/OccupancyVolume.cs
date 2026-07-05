using Silk.NET.OpenGL;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// GPU occupancy of the world around the camera — one byte per voxel (255 =
/// solid/opaque, 0 = passable) in an R8 3D texture. Shadow and light rays DDA
/// through it (plan 02 M3+). Chunks upload a few per frame; edits re-upload
/// just the touched chunk.
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
    private const int RegionRadiusChunks = 5;
    private const int RegionChunks = RegionRadiusChunks * 2 + 1; // 11
    private const int S = Constants.ChunkSize;
    public const int Size = RegionChunks * S; // 176 voxels per edge

    private readonly GL _gl;
    private readonly byte[] _opaque;
    private readonly uint _texture;
    private readonly byte[] _scratch = new byte[S * S * S];
    private readonly byte[] _zeroSlab = new byte[S * S * S];
    private readonly byte[] _zero = new byte[Size * Size * Size];
    private readonly (int Dx, int Dy, int Dz)[] _localChunks;
    private readonly HashSet<(int, int, int)> _uploaded = new();
    private readonly HashSet<(int, int, int)> _dirty = new();

    private (int X, int Y, int Z) _originChunk = (int.MinValue, 0, 0);
    private int _cursor;

    public OccupancyVolume(GL gl, byte[] opaqueTable)
    {
        _gl = gl;
        _opaque = opaqueTable;

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
                Size, Size, Size, 0, PixelFormat.Red, PixelType.UnsignedByte, null);
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
            return;
        }

        // Toroidal scroll: chunks still in range keep their slab. Drop the ones
        // that scrolled out and zero the slabs of newly-entered coords (they
        // alias the departed chunks' slabs) so stale occupancy can't shadow.
        _uploaded.RemoveWhere(c =>
            c.Item1 < origin.X || c.Item1 >= origin.X + RegionChunks ||
            c.Item2 < origin.Y || c.Item2 >= origin.Y + RegionChunks ||
            c.Item3 < origin.Z || c.Item3 >= origin.Z + RegionChunks);
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
        // (x fastest, then y, then z), writing occupancy per voxel.
        for (int y = 0; y < S; y++)
            for (int z = 0; z < S; z++)
                for (int x = 0; x < S; x++)
                    _scratch[(z * S + y) * S + x] = _opaque[blocks[(y * S + z) * S + x]] != 0 ? (byte)255 : (byte)0;

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
            _gl.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, Size, Size, Size,
                PixelFormat.Red, PixelType.UnsignedByte, p);
        }
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
