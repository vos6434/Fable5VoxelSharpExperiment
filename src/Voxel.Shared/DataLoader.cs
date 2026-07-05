using System.Text.Json;

namespace Voxel.Shared;

/// <summary>Loads the block/item registries from a /data directory on disk.</summary>
public static class DataLoader
{
    public static (BlockRegistry Blocks, ItemRegistry Items) LoadRegistries(string dataRoot)
    {
        return (
            BlockRegistry.FromSources(ReadSources(Path.Combine(dataRoot, "blocks"))),
            ItemRegistry.FromSources(ReadSources(Path.Combine(dataRoot, "items"))));
    }

    private static List<DataSource> ReadSources(string dir)
    {
        var files = Directory.GetFiles(dir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        var sources = new List<DataSource>(files.Length);
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            sources.Add(new DataSource(Path.GetFileName(file), doc.RootElement.Clone()));
        }
        return sources;
    }
}
