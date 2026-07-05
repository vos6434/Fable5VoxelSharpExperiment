using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxel.Client.Ui;

/// <summary>
/// Client-local persistence (the native analog of the web client's
/// localStorage): inventory contents, GUI window positions, hotbar placement.
/// One JSON file next to the repo data.
/// </summary>
public sealed class Settings
{
    public sealed class StackDto
    {
        [JsonPropertyName("id")] public required string Id { get; set; }
        [JsonPropertyName("count")] public required int Count { get; set; }
    }

    [JsonPropertyName("hotbar")] public StackDto?[]? Hotbar { get; set; }
    [JsonPropertyName("storage")] public StackDto?[]? Storage { get; set; }
    [JsonPropertyName("armor")] public StackDto?[]? Armor { get; set; }
    [JsonPropertyName("selected")] public int Selected { get; set; }
    [JsonPropertyName("guiPositions")] public Dictionary<string, float[]> GuiPositions { get; set; } = new();
    [JsonPropertyName("hotbarPos")] public float[]? HotbarPos { get; set; }
    [JsonPropertyName("hotbarUnlocked")] public bool HotbarUnlocked { get; set; }

    // ---- lighting quality (plan 02 M8) ----------------------------------
    /// <summary>Block lights per cluster that cast shadow rays (0/2/4/8).</summary>
    [JsonPropertyName("shadowedLightCap")] public int ShadowedLightCap { get; set; } = 8;
    /// <summary>Shadow/light region radius in chunks around the camera (4–6).</summary>
    [JsonPropertyName("shadowRegionRadius")] public int ShadowRegionRadius { get; set; } = 5;

    [JsonIgnore] private string _path = "";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        Settings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings()
                : new Settings();
        }
        catch
        {
            settings = new Settings(); // corrupt settings never block startup
        }
        settings._path = path;
        return settings;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // best-effort persistence; never crash the game over settings
        }
    }
}
