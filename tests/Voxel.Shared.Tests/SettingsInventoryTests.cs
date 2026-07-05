using Voxel.Client.Ui;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

/// <summary>
/// Regression tests for the settings/inventory persistence round-trip
/// (the native analog of the web client's localStorage path).
/// </summary>
public class SettingsInventoryTests
{
    private static (BlockRegistry Blocks, ItemRegistry Items) Registries { get; } =
        DataLoader.LoadRegistries(Path.Combine(Golden.RepoRoot, "data"));

    private static string TempSettings(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fv-settings-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Fresh_settings_gets_default_loadout()
    {
        string path = TempSettings("");
        File.Delete(path);
        var settings = Settings.Load(path);
        var inv = new PlayerInventory(Registries.Blocks, Registries.Items, settings);
        Assert.Equal("stone", inv.Hotbar.Slots[0]!.Id);
        Assert.Equal(100, inv.Hotbar.Slots[0]!.Count);
    }

    [Fact]
    public void Saved_stacks_survive_a_reload()
    {
        string path = TempSettings("");
        File.Delete(path);

        // First session: default loadout, then simulate a creative pick into storage.
        var settings1 = Settings.Load(path);
        var inv1 = new PlayerInventory(Registries.Blocks, Registries.Items, settings1);
        int hellIdx = inv1.CreativeList.IndexOf("hellstone");
        inv1.HandleSlotClick("creative", hellIdx, 0);  // cursor = 100 hellstone
        inv1.HandleSlotClick("storage", 5, 0);          // place into storage slot 5
        Assert.NotNull(inv1.Storage.Slots[5]);

        // Second session: everything must come back.
        var settings2 = Settings.Load(path);
        var inv2 = new PlayerInventory(Registries.Blocks, Registries.Items, settings2);
        Assert.Equal("stone", inv2.Hotbar.Slots[0]?.Id);
        Assert.Equal("hellstone", inv2.Storage.Slots[5]?.Id);
        Assert.Equal(100, inv2.Storage.Slots[5]?.Count);

        File.Delete(path);
    }

    [Fact]
    public void Old_64_stacks_migrate_to_current_max()
    {
        string path = TempSettings("""
            { "hotbar": [ { "id": "stone", "count": 64 } ], "selected": 0 }
            """);
        var settings = Settings.Load(path);
        var inv = new PlayerInventory(Registries.Blocks, Registries.Items, settings);
        Assert.Equal(100, inv.Hotbar.Slots[0]?.Count);
        File.Delete(path);
    }

    [Fact]
    public void Unknown_ids_are_dropped_not_fatal()
    {
        string path = TempSettings("""
            { "hotbar": [ { "id": "ruby_ore", "count": 3 }, { "id": "dirt", "count": 7 } ], "selected": 1 }
            """);
        var settings = Settings.Load(path);
        var inv = new PlayerInventory(Registries.Blocks, Registries.Items, settings);
        Assert.Null(inv.Hotbar.Slots[0]);
        Assert.Equal("dirt", inv.Hotbar.Slots[1]?.Id);
        File.Delete(path);
    }
}
