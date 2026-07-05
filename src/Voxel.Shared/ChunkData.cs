namespace Voxel.Shared;

/// <summary>
/// Block storage for one cubic chunk. ushort leaves room for 65k block
/// states. Index order is x-fastest, then z, then y — chosen once here and
/// reused by the mesher, worldgen, and serialization (matches the web
/// version's byte layout so the wire/DB formats interoperate).
/// </summary>
public sealed class ChunkData
{
    public ushort[] Blocks { get; }

    public ChunkData()
    {
        Blocks = new ushort[Constants.ChunkVolume];
    }

    public ChunkData(ushort[] blocks)
    {
        if (blocks.Length != Constants.ChunkVolume)
        {
            throw new ArgumentException($"ChunkData expects {Constants.ChunkVolume} blocks, got {blocks.Length}");
        }
        Blocks = blocks;
    }

    public static int Index(int x, int y, int z) => (y * Constants.ChunkSize + z) * Constants.ChunkSize + x;

    public static bool InBounds(int x, int y, int z) =>
        x >= 0 && x < Constants.ChunkSize &&
        y >= 0 && y < Constants.ChunkSize &&
        z >= 0 && z < Constants.ChunkSize;

    public ushort Get(int x, int y, int z) => Blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, ushort id) => Blocks[Index(x, y, z)] = id;

    /// <summary>Number of non-air blocks; used by tests, empty-chunk detection, and debug overlays.</summary>
    public int CountSolid()
    {
        int n = 0;
        for (int i = 0; i < Constants.ChunkVolume; i++)
        {
            if (Blocks[i] != 0) n++;
        }
        return n;
    }
}
