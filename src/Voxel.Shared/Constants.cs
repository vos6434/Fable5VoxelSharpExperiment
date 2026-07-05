namespace Voxel.Shared;

public static class Constants
{
    /// <summary>Edge length of a cubic chunk, in blocks. The world is chunked on all three axes.</summary>
    public const int ChunkSize = 16;

    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    /// <summary>Simulation rate: the world advances in fixed 50 ms ticks.</summary>
    public const int TicksPerSecond = 20;

    /// <summary>Default full day-night cycle length (20 minutes at 20 TPS); server-configurable later.</summary>
    public const int DayLengthTicks = 24_000;
}
