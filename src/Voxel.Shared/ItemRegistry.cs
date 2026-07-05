using System.Text.Json;

namespace Voxel.Shared;

/// <summary>
/// Standalone items from /data/items. Blocks get an implicit item form (same
/// string id, icon per the block icon rule); this registry only holds
/// explicitly defined items.
/// </summary>

public sealed record ToolStats(string Type, int Tier, double Speed);

public sealed class ItemDefinition
{
    public required int NumericId { get; init; }
    public required string StringId { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public required int MaxStack { get; init; }
    public required ToolStats? Tool { get; init; }
}

public sealed class ItemRegistry
{
    public IReadOnlyList<ItemDefinition> Defs { get; }

    private readonly Dictionary<string, ItemDefinition> _byString = new();

    private ItemRegistry(List<ItemDefinition> defs)
    {
        Defs = defs;
        foreach (var def in defs) _byString[def.StringId] = def;
    }

    public static ItemRegistry FromSources(IEnumerable<DataSource> sources)
    {
        var parsed = sources.Select(s => Parse(s.Source, s.Raw)).ToList();
        parsed.Sort((a, b) => string.CompareOrdinal(a.StringId, b.StringId));
        var defs = new List<ItemDefinition>();
        foreach (var p in parsed)
        {
            if (defs.Any(d => d.StringId == p.StringId))
            {
                throw new DataException(p.StringId, $"duplicate item id \"{p.StringId}\"");
            }
            defs.Add(new ItemDefinition
            {
                NumericId = defs.Count + 1, // 0 reserved for "no item"
                StringId = p.StringId,
                Name = p.Name,
                Icon = p.Icon,
                MaxStack = p.MaxStack,
                Tool = p.Tool,
            });
        }
        return new ItemRegistry(defs);
    }

    public int Count => Defs.Count;

    public ItemDefinition? ById(string stringId) => _byString.GetValueOrDefault(stringId);

    private sealed record Parsed(string StringId, string Name, string Icon, int MaxStack, ToolStats? Tool);

    private static Parsed Parse(string source, JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new DataException(source, "item definition must be an object");
        }

        string stringId = Json.RequiredString(source, obj, "id");
        if (!Json.IsValidId(stringId)) throw new DataException(source, "\"id\" must be a string matching ^[a-z0-9_]+$");
        string name = Json.RequiredString(source, obj, "name");

        string? icon = Json.OptionalString(source, obj, "icon");
        if (icon is null || !icon.EndsWith(".png", StringComparison.Ordinal) || icon.Contains('/'))
        {
            throw new DataException(source, "\"icon\" must be a bare .png filename (required for items)");
        }

        int maxStack = (int)Json.OptionalNumber(source, obj, "maxStack", 100, 1, 100, integer: true);

        ToolStats? tool = null;
        if (obj.TryGetProperty("tool", out var toolRaw) && toolRaw.ValueKind != JsonValueKind.Null)
        {
            if (toolRaw.ValueKind != JsonValueKind.Object) throw new DataException(source, "\"tool\" must be an object");
            string? type = Json.OptionalString(source, toolRaw, "type");
            if (type is null) throw new DataException(source, "\"tool.type\" must be a string");
            int tier = (int)Json.OptionalNumber(source, toolRaw, "tier", 0, 0, int.MaxValue, integer: true);
            double speed = Json.OptionalNumber(source, toolRaw, "speed", 1, double.Epsilon, double.MaxValue, integer: false);
            tool = new ToolStats(type, tier, speed);
        }

        return new Parsed(stringId, name, icon, maxStack, tool);
    }
}
