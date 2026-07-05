namespace Voxel.Shared;

/// <summary>Thrown when /data content fails validation; message names the offending file and field.</summary>
public sealed class DataException(string source, string message)
    : Exception($"[data] {source}: {message}");
