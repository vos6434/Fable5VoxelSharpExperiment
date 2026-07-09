namespace Voxel.Shared;

/// <summary>
/// Chunk downsampling for distant terrain (plan 04). An LOD chunk shares its
/// source chunk's coordinates but stores (16 &gt;&gt; level)³ cells of
/// (1 &lt;&lt; level)-block size: LOD1 = 8³ cells of 2 blocks, LOD2 = 4³ of 4.
/// Cells vote: a cell becomes the most common solid block when solids fill at
/// least half of it, else water when water fills at least half (keeps oceans
/// intact, drops shoreline slivers), else air — so sub-half features erase,
/// which is the accepted v1 trade-off.
/// </summary>
public static class ChunkLod
{
    public const int MaxLevel = 2;

    /// <summary>
    /// Vertical band LOD rings cover, in chunk coords (world y −64…+80).
    /// Distant hell/deep stone is invisible anyway; keeps memory and DB cache
    /// linear in radius². Outside the band LOD requests serve empty.
    /// </summary>
    public const int BandMinCy = -4;
    public const int BandMaxCy = 4;

    public static bool InBand(int cy) => cy is >= BandMinCy and <= BandMaxCy;

    /// <summary>Cells per axis of an LOD chunk (level 0 = full 16).</summary>
    public static int CellsPerAxis(int level) => Constants.ChunkSize >> level;

    public static int CellCount(int level)
    {
        int n = CellsPerAxis(level);
        return n * n * n;
    }

    /// <summary>
    /// Downsamples one chunk's blocks to LOD cells. Output uses the same
    /// x-fastest, then z, then y index order as <see cref="ChunkData"/> at the
    /// reduced size, so the wire/mesher code paths stay shape-agnostic.
    /// </summary>
    public static ushort[] Downsample(ushort[] source, int level, ushort waterId)
    {
        if (source.Length != Constants.ChunkVolume)
        {
            throw new ArgumentException($"expected {Constants.ChunkVolume} blocks, got {source.Length}", nameof(source));
        }
        if (level is < 1 or > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "LOD level must be 1 or 2");
        }

        int factor = 1 << level;
        int n = CellsPerAxis(level);
        int cellVolume = factor * factor * factor;
        var cells = new ushort[n * n * n];

        Span<ushort> ids = stackalloc ushort[cellVolume];
        Span<int> counts = stackalloc int[cellVolume];

        for (int cy = 0; cy < n; cy++)
        for (int cz = 0; cz < n; cz++)
        for (int cx = 0; cx < n; cx++)
        {
            int distinct = 0;
            int waterCount = 0;
            int solidCount = 0;
            for (int dy = 0; dy < factor; dy++)
            for (int dz = 0; dz < factor; dz++)
            for (int dx = 0; dx < factor; dx++)
            {
                ushort id = source[ChunkData.Index(cx * factor + dx, cy * factor + dy, cz * factor + dz)];
                if (id == 0) continue;
                if (id == waterId) { waterCount++; continue; }
                solidCount++;
                int slot = 0;
                while (slot < distinct && ids[slot] != id) slot++;
                if (slot == distinct) { ids[distinct] = id; counts[distinct] = 0; distinct++; }
                counts[slot]++;
            }

            ushort cell = 0;
            if (solidCount * 2 >= cellVolume)
            {
                // Most common solid; ties break to the lower numeric id so the
                // result is deterministic across runs and implementations.
                int best = 0;
                for (int i = 1; i < distinct; i++)
                {
                    if (counts[i] > counts[best] || (counts[i] == counts[best] && ids[i] < ids[best])) best = i;
                }
                cell = ids[best];
            }
            else if (waterCount * 2 >= cellVolume)
            {
                cell = waterId;
            }
            cells[(cy * n + cz) * n + cx] = cell;
        }
        return cells;
    }
}
