namespace Voxel.Shared;

public static class Constants
{
    /// <summary>Edge length of a cubic chunk, in blocks. The world is chunked on all three axes.</summary>
    public const int ChunkSize = 16;

    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;
}
