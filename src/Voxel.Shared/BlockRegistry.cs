using System.Text.Json;

namespace Voxel.Shared;

/// <summary>
/// Data-driven block registry, ported from the web version: definitions parse
/// from /data/blocks JSON; numeric IDs are assigned by ordinal-sorted string
/// ID with 0 reserved for air, so every process that loads the same data
/// files derives the identical palette (the golden tests pin this).
/// </summary>

public enum FaceDir { Px = 0, Nx = 1, Py = 2, Ny = 3, Pz = 4, Nz = 5 }

public enum Transparency { Opaque, Cutout, Translucent }

public enum Collision { Solid, None, Liquid }

public sealed record BlockTextures(string? All, string? Top, string? Bottom, string? Side);

/// <summary>Emitted light color, parsed from optional "#RRGGBB" JSON (default white).</summary>
public readonly record struct LightColor(float R, float G, float B)
{
    public static readonly LightColor White = new(1f, 1f, 1f);

    public static LightColor Parse(string source, string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' ||
            !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            throw new DataException(source, $"\"lightColor\" must be \"#RRGGBB\", got \"{hex}\"");
        }
        return new LightColor(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    }
}

public sealed record DropEntry(string Item, int Count, double Chance);

public sealed record ToolRule(string Type, int MinTier, bool Required);

public sealed class BlockDefinition
{
    public required int NumericId { get; init; }
    public required string StringId { get; init; }
    public required string Name { get; init; }
    public required BlockTextures Textures { get; init; }
    public required string? Icon { get; init; }
    public required double Hardness { get; init; }
    public required ToolRule Tool { get; init; }
    public required int LightEmission { get; init; }
    public required LightColor LightColor { get; init; }
    public required Transparency Transparency { get; init; }
    public required Collision Collision { get; init; }
    public required string Sounds { get; init; }
    public required IReadOnlyList<DropEntry> Drops { get; init; }
    public required IReadOnlyDictionary<string, bool> Flags { get; init; }

    /// <summary>Texture filename for one face: top/bottom use their key else "all"; sides use "side" else "all".</summary>
    public string FaceTexture(FaceDir face)
    {
        string? resolved = face switch
        {
            FaceDir.Py => Textures.Top ?? Textures.All,
            FaceDir.Ny => Textures.Bottom ?? Textures.All,
            _ => Textures.Side ?? Textures.All,
        };
        return resolved ?? throw new DataException(StringId, $"no texture for face {face}");
    }

    /// <summary>Inventory-icon texture: explicit icon wins, else derived from world texture.</summary>
    public string IconTexture() => Icon ?? Textures.All ?? Textures.Top ?? Textures.Side ?? "";
}

/// <summary>One JSON document plus where it came from, for error messages.</summary>
public sealed record DataSource(string Source, JsonElement Raw);

public sealed class BlockRegistry
{
    public const ushort AirId = 0;

    /// <summary>Indexed by numericId; [0] is air.</summary>
    public IReadOnlyList<BlockDefinition> Defs { get; }

    /// <summary>Opaque[numericId] = 1 if the block hides faces behind it.</summary>
    public byte[] Opaque { get; }

    private readonly Dictionary<string, BlockDefinition> _byString = new();

    private BlockRegistry(List<BlockDefinition> defs)
    {
        Defs = defs;
        Opaque = new byte[defs.Count];
        foreach (var def in defs)
        {
            _byString[def.StringId] = def;
            Opaque[def.NumericId] =
                (byte)(def.NumericId != AirId && def.Transparency == Transparency.Opaque ? 1 : 0);
        }
    }

    public static BlockRegistry FromSources(IEnumerable<DataSource> sources)
    {
        var parsed = sources.Select(s => BlockParser.Parse(s.Source, s.Raw)).ToList();
        parsed.Sort((a, b) => string.CompareOrdinal(a.StringId, b.StringId));
        var defs = new List<BlockDefinition> { Air() };
        foreach (var p in parsed)
        {
            if (defs.Any(d => d.StringId == p.StringId))
            {
                throw new DataException(p.StringId, $"duplicate block id \"{p.StringId}\"");
            }
            defs.Add(p.WithNumericId(defs.Count));
        }
        return new BlockRegistry(defs);
    }

    public int Count => Defs.Count;

    public BlockDefinition Get(int numericId)
    {
        if (numericId < 0 || numericId >= Defs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(numericId), $"unknown block numericId {numericId}");
        }
        return Defs[numericId];
    }

    public BlockDefinition? ById(string stringId) => _byString.GetValueOrDefault(stringId);

    /// <summary>String → numeric ID; throws with a clear message for typos in code/data.</summary>
    public ushort Resolve(string stringId)
    {
        var def = ById(stringId) ?? throw new DataException(stringId, $"unknown block id \"{stringId}\"");
        return (ushort)def.NumericId;
    }

    private static BlockDefinition Air() => new()
    {
        NumericId = AirId,
        StringId = "air",
        Name = "Air",
        Textures = new BlockTextures(null, null, null, null),
        Icon = null,
        Hardness = -1,
        Tool = new ToolRule("none", 0, false),
        LightEmission = 0,
        Transparency = Transparency.Translucent,
        Collision = Collision.None,
        Sounds = "none",
        Drops = [],
        Flags = new Dictionary<string, bool>(),
        LightColor = LightColor.White,
    };
}

/// <summary>Parsed block definition before a numeric id is assigned.</summary>
public sealed record ParsedBlock(
    string StringId, string Name, BlockTextures Textures, string? Icon, double Hardness,
    ToolRule Tool, int LightEmission, LightColor LightColor, Transparency Transparency, Collision Collision,
    string Sounds, IReadOnlyList<DropEntry> Drops, IReadOnlyDictionary<string, bool> Flags)
{
    public BlockDefinition WithNumericId(int numericId) => new()
    {
        NumericId = numericId,
        StringId = StringId,
        Name = Name,
        Textures = Textures,
        Icon = Icon,
        Hardness = Hardness,
        Tool = Tool,
        LightEmission = LightEmission,
        LightColor = LightColor,
        Transparency = Transparency,
        Collision = Collision,
        Sounds = Sounds,
        Drops = Drops,
        Flags = Flags,
    };
}

public static class BlockParser
{
    public static ParsedBlock Parse(string source, JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new DataException(source, "block definition must be an object");
        }

        string stringId = Json.RequiredString(source, obj, "id");
        if (!Json.IsValidId(stringId)) throw new DataException(source, $"\"id\" must match ^[a-z0-9_]+$, got \"{stringId}\"");
        if (stringId == "air") throw new DataException(source, "\"air\" is a reserved id");

        BlockTextures textures;
        if (obj.TryGetProperty("textures", out var texturesRaw))
        {
            if (texturesRaw.ValueKind != JsonValueKind.Object)
            {
                throw new DataException(source, "\"textures\" must be an object");
            }
            textures = new BlockTextures(
                Json.OptionalPng(source, texturesRaw, "all"),
                Json.OptionalPng(source, texturesRaw, "top"),
                Json.OptionalPng(source, texturesRaw, "bottom"),
                Json.OptionalPng(source, texturesRaw, "side"));
        }
        else
        {
            textures = new BlockTextures(null, null, null, null);
        }
        if ((textures.Top ?? textures.All) is null) throw new DataException(source, "textures do not cover face \"py\" (need \"all\" or the specific key)");
        if ((textures.Bottom ?? textures.All) is null) throw new DataException(source, "textures do not cover face \"ny\" (need \"all\" or the specific key)");
        if ((textures.Side ?? textures.All) is null) throw new DataException(source, "textures do not cover face \"px\" (need \"all\" or the specific key)");

        string? icon = Json.OptionalPng(source, obj, "icon");

        double hardness = Json.OptionalNumber(source, obj, "hardness", double.NaN, -1, 1e9, integer: false);
        if (double.IsNaN(hardness)) throw new DataException(source, "missing required number \"hardness\" (-1 = unbreakable)");

        ToolRule tool;
        if (obj.TryGetProperty("tool", out var toolRaw) && toolRaw.ValueKind != JsonValueKind.Null)
        {
            if (toolRaw.ValueKind != JsonValueKind.Object) throw new DataException(source, "\"tool\" must be an object");
            tool = new ToolRule(
                Json.OptionalString(source, toolRaw, "type") ?? "none",
                (int)Json.OptionalNumber(source, toolRaw, "minTier", 0, 0, 16, integer: true),
                toolRaw.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True);
        }
        else
        {
            tool = new ToolRule("none", 0, false);
        }

        List<DropEntry> drops;
        if (!obj.TryGetProperty("drops", out var dropsRaw) || dropsRaw.ValueKind == JsonValueKind.Null)
        {
            drops = [new DropEntry(stringId, 1, 1)];
        }
        else if (dropsRaw.ValueKind != JsonValueKind.Array)
        {
            throw new DataException(source, "\"drops\" must be an array");
        }
        else
        {
            drops = [];
            int i = 0;
            foreach (var entry in dropsRaw.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new DataException(source, $"drops[{i}] must be an object");
                drops.Add(new DropEntry(
                    Json.RequiredString(source, entry, "item"),
                    (int)Json.OptionalNumber(source, entry, "count", 1, 1, 100, integer: true),
                    Json.OptionalNumber(source, entry, "chance", 1, 0, 1, integer: false)));
                i++;
            }
        }

        Dictionary<string, bool> flags = new();
        if (obj.TryGetProperty("flags", out var flagsRaw))
        {
            if (flagsRaw.ValueKind != JsonValueKind.Object) throw new DataException(source, "\"flags\" must be an object");
            foreach (var prop in flagsRaw.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                {
                    throw new DataException(source, $"flags.{prop.Name} must be a boolean");
                }
                flags[prop.Name] = prop.Value.ValueKind == JsonValueKind.True;
            }
        }

        return new ParsedBlock(
            stringId,
            Json.RequiredString(source, obj, "name"),
            textures,
            icon,
            hardness,
            tool,
            (int)Json.OptionalNumber(source, obj, "lightEmission", 0, 0, 15, integer: true),
            Json.OptionalString(source, obj, "lightColor") is { } lc ? LightColor.Parse(source, lc) : LightColor.White,
            Json.OptionalEnum(source, obj, "transparency", Transparency.Opaque,
                ("opaque", Transparency.Opaque), ("cutout", Transparency.Cutout), ("translucent", Transparency.Translucent)),
            Json.OptionalEnum(source, obj, "collision", Collision.Solid,
                ("solid", Collision.Solid), ("none", Collision.None), ("liquid", Collision.Liquid)),
            Json.OptionalString(source, obj, "sounds") ?? "stone",
            drops,
            flags);
    }
}
