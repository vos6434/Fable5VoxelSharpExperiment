namespace Voxel.Shared;

/// <summary>
/// Section-based LOD pyramid (plan 04 v2, Voxy/Distant Horizons model). A
/// level-ℓ *section* covers 2^ℓ chunks per axis (section coords = chunk
/// coords &gt;&gt; ℓ) and stores the same 16³ u16 grid as a chunk, with cells
/// of 2^ℓ blocks — so the wire format, DB rows, and mesher are
/// level-invariant. Level ℓ mips from the 8 level ℓ−1 children (level 1
/// from the 8 source chunks).
///
/// Mip rule (Voxy): each cell takes the *most opaque* of its 8 child cells,
/// ties broken toward the topmost corner (then z, then x). Surfaces keep
/// their block identity — grass stays grass — and coarse terrain always
/// covers at least what the fine terrain does, so overdraw blending never
/// shows sunken seams. Air only where all 8 children are air.
/// </summary>
public static class ChunkLod
{
    public const int MaxLevel = 3;

    /// <summary>Bump when the mip rules change: stored LOD blobs are stale and get regenerated.</summary>
    public const int AlgoVersion = 3;

    /// <summary>World blocks per cell at a level (level 0 = 1).</summary>
    public static int CellSize(int level) => 1 << level;

    /// <summary>Child index inside a parent section, matching the mip input array order.</summary>
    public static int ChildIndex(int dx, int dy, int dz) => (dy << 2) | (dz << 1) | dx;

    /// <summary>
    /// Mips 8 child grids (16³ each, <see cref="ChildIndex"/> order, null =
    /// all air) into one parent 16³ grid. <paramref name="opaque"/> is the
    /// registry's opacity table; ranking is opaque &gt; non-air &gt; air.
    /// Returns null when every child is null (all air).
    /// </summary>
    public static ushort[]? MipSections(ushort[]?[] children, byte[] opaque)
    {
        if (children.Length != 8)
        {
            throw new ArgumentException($"expected 8 children, got {children.Length}", nameof(children));
        }

        ushort[]? cells = null;
        const int S = Constants.ChunkSize;
        const int Half = S / 2;

        for (int ci = 0; ci < 8; ci++)
        {
            var child = children[ci];
            if (child is null) continue;
            if (child.Length != Constants.ChunkVolume)
            {
                throw new ArgumentException($"child {ci}: expected {Constants.ChunkVolume} cells, got {child.Length}", nameof(children));
            }

            // Child ci occupies one octant of the parent grid.
            int ox = (ci & 1) * Half;
            int oy = ((ci >> 2) & 1) * Half;
            int oz = ((ci >> 1) & 1) * Half;

            for (int y = 0; y < Half; y++)
            for (int z = 0; z < Half; z++)
            for (int x = 0; x < Half; x++)
            {
                // Rank each of the 8 source cells: opacity first, then the
                // topmost corner (y, then z, then x) breaks ties — a grass
                // top beats the dirt under it.
                int best = -1;
                ushort bestId = 0;
                for (int corner = 7; corner >= 0; corner--)
                {
                    int dx = corner & 1, dy = (corner >> 2) & 1, dz = (corner >> 1) & 1;
                    ushort id = child[ChunkData.Index(x * 2 + dx, y * 2 + dy, z * 2 + dz)];
                    if (id == 0) continue;
                    int rank = ((opaque[id] + 1) << 3) | corner;
                    if (rank > best) { best = rank; bestId = id; }
                }
                if (best < 0) continue;
                cells ??= new ushort[Constants.ChunkVolume];
                cells[ChunkData.Index(ox + x, oy + y, oz + z)] = bestId;
            }
        }
        return cells;
    }
}
