using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voxel.Shared;

/// <summary>Validation helpers mirroring the TypeScript loaders' semantics and error style.</summary>
internal static partial class Json
{
    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static string? OptionalString(string source, JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new DataException(source, $"\"{key}\" must be a string");
        return v.GetString();
    }

    public static string RequiredString(string source, JsonElement obj, string key)
    {
        string? v = OptionalString(source, obj, key);
        if (string.IsNullOrEmpty(v)) throw new DataException(source, $"missing required string \"{key}\"");
        return v;
    }

    public static string? OptionalPng(string source, JsonElement obj, string key)
    {
        string? v = OptionalString(source, obj, key);
        if (v is null) return null;
        if (!v.EndsWith(".png", StringComparison.Ordinal) || v.Contains('/') || v.Contains('\\'))
        {
            throw new DataException(source, $"\"{key}\" must be a bare .png filename, got \"{v}\"");
        }
        return v;
    }

    public static double OptionalNumber(
        string source, JsonElement obj, string key,
        double fallback, double min, double max, bool integer)
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        if (v.ValueKind != JsonValueKind.Number) throw new DataException(source, $"\"{key}\" must be a number");
        double value = v.GetDouble();
        if (value < min || value > max) throw new DataException(source, $"\"{key}\" must be in [{min}, {max}], got {value}");
        if (integer && value != Math.Floor(value)) throw new DataException(source, $"\"{key}\" must be an integer");
        return value;
    }

    public static T OptionalEnum<T>(
        string source, JsonElement obj, string key, T fallback,
        params (string Name, T Value)[] allowed) where T : struct
    {
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = v.GetString()!;
            foreach (var (name, value) in allowed)
            {
                if (name == s) return value;
            }
        }
        throw new DataException(source, $"\"{key}\" must be one of {string.Join(" | ", allowed.Select(a => a.Name))}");
    }
}
