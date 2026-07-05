namespace Voxel.Shared;

public static class Coords
{
    /// <summary>World block coordinate → chunk coordinate (floor division, negatives correct).</summary>
    public static int WorldToChunk(int v) => v >= 0 ? v / Constants.ChunkSize : (v - Constants.ChunkSize + 1) / Constants.ChunkSize;

    /// <summary>World position (e.g. camera) → chunk coordinate.</summary>
    public static int WorldToChunk(double v) => (int)Math.Floor(v / Constants.ChunkSize);

    /// <summary>World block coordinate → local 0..15 coordinate within its chunk.</summary>
    public static int WorldToLocal(int v)
    {
        int m = v % Constants.ChunkSize;
        return m < 0 ? m + Constants.ChunkSize : m;
    }

    /// <summary>Dictionary key for a chunk position.</summary>
    public static (int Cx, int Cy, int Cz) ChunkKey(int cx, int cy, int cz) => (cx, cy, cz);
}
