using Voxel.Shared;

namespace Voxel.Client.Ui;

public sealed record ItemStack(string Id, int Count);

public sealed class Container
{
    public readonly ItemStack?[] Slots;
    public Container(int size) => Slots = new ItemStack?[size];
}

/// <summary>
/// Player-side item state, ported from the web client: hotbar (10,
/// key-bound), storage, armor, placeholder crafting grid, the mouse-cursor
/// stack, and the creative item list. Same Minecraft click grammar; persisted
/// through Settings instead of localStorage.
/// </summary>
public sealed class PlayerInventory
{
    public readonly Container Hotbar = new(10);
    public readonly Container Storage = new(40);
    public readonly Container Armor = new(4);
    public readonly Container Craft = new(4);
    public readonly Container CraftResult = new(1);
    /// <summary>Stack held on the mouse cursor while GUIs are open.</summary>
    public ItemStack? Cursor;
    /// <summary>Selected hotbar index (0-9).</summary>
    public int Selected;
    /// <summary>Every placeable/holdable thing, for creative slots: blocks then items.</summary>
    public readonly List<string> CreativeList;

    private readonly BlockRegistry _blocks;
    private readonly ItemRegistry _items;
    private readonly Settings _settings;

    public PlayerInventory(BlockRegistry blocks, ItemRegistry items, Settings settings)
    {
        _blocks = blocks;
        _items = items;
        _settings = settings;
        CreativeList =
        [
            .. blocks.Defs.Where(d => d.NumericId != 0).Select(d => d.StringId),
            .. items.Defs.Select(d => d.StringId),
        ];
        if (!LoadFromSettings()) DefaultLoadout();
    }

    public bool IsKnownId(string id) => _blocks.ById(id) is not null || _items.ById(id) is not null;

    public int MaxStackOf(string id) => _items.ById(id)?.MaxStack ?? 100;

    public string DisplayNameOf(string id) =>
        _blocks.ById(id)?.Name ?? _items.ById(id)?.Name ?? id;

    public ItemStack? SelectedStack() => Hotbar.Slots[Selected];

    public Container? ContainerFor(string role) => role switch
    {
        "hotbar" => Hotbar,
        "storage" => Storage,
        "armor" => Armor,
        "craft" => Craft,
        "craft_result" => CraftResult,
        _ => null,
    };

    /// <summary>Standard Minecraft-style slot interaction. button: 0 = left, 2 = right.</summary>
    public void HandleSlotClick(string role, int index, int button)
    {
        if (role == "creative")
        {
            if (Cursor is not null)
            {
                Cursor = null; // creative void: clicking in with a held stack erases it
            }
            else if (index >= 0 && index < CreativeList.Count)
            {
                string id = CreativeList[index];
                Cursor = new ItemStack(id, button == 2 ? 1 : MaxStackOf(id));
            }
            Save();
            return;
        }
        if (role == "craft_result") return; // read-only until recipes exist

        var container = ContainerFor(role);
        if (container is null || index < 0 || index >= container.Slots.Length) return;
        var slot = container.Slots[index];

        if (button == 0)
        {
            if (Cursor is null)
            {
                container.Slots[index] = null;
                Cursor = slot;
            }
            else if (slot is null)
            {
                container.Slots[index] = Cursor;
                Cursor = null;
            }
            else if (slot.Id == Cursor.Id)
            {
                int max = MaxStackOf(slot.Id);
                int moved = Math.Min(Cursor.Count, max - slot.Count);
                container.Slots[index] = slot with { Count = slot.Count + moved };
                Cursor = Cursor.Count - moved <= 0 ? null : Cursor with { Count = Cursor.Count - moved };
            }
            else
            {
                container.Slots[index] = Cursor;
                Cursor = slot;
            }
        }
        else if (button == 2)
        {
            if (Cursor is null)
            {
                if (slot is not null)
                {
                    int take = (slot.Count + 1) / 2;
                    Cursor = slot with { Count = take };
                    container.Slots[index] = slot.Count - take <= 0 ? null : slot with { Count = slot.Count - take };
                }
            }
            else if (slot is null)
            {
                container.Slots[index] = Cursor with { Count = 1 };
                Cursor = Cursor.Count - 1 <= 0 ? null : Cursor with { Count = Cursor.Count - 1 };
            }
            else if (slot.Id == Cursor.Id && slot.Count < MaxStackOf(slot.Id))
            {
                container.Slots[index] = slot with { Count = slot.Count + 1 };
                Cursor = Cursor.Count - 1 <= 0 ? null : Cursor with { Count = Cursor.Count - 1 };
            }
            else
            {
                container.Slots[index] = Cursor;
                Cursor = slot;
            }
        }
        Save();
    }

    private void DefaultLoadout()
    {
        string[] wanted =
        [
            "stone", "dirt", "grass", "oak_planks", "oak_log",
            "glass", "glowstone", "sand", "cobblestone", "snow",
        ];
        for (int i = 0; i < wanted.Length; i++)
        {
            if (_blocks.ById(wanted[i]) is not null)
            {
                Hotbar.Slots[i] = new ItemStack(wanted[i], 100);
            }
        }
        Save();
    }

    public void Save()
    {
        static Settings.StackDto?[] Dump(Container c) =>
            [.. c.Slots.Select(s => s is null ? null : new Settings.StackDto { Id = s.Id, Count = s.Count })];
        _settings.Hotbar = Dump(Hotbar);
        _settings.Storage = Dump(Storage);
        _settings.Armor = Dump(Armor);
        _settings.Selected = Selected;
        _settings.Save();
    }

    private bool LoadFromSettings()
    {
        if (_settings.Hotbar is null) return false;
        Restore(Hotbar, _settings.Hotbar);
        Restore(Storage, _settings.Storage);
        Restore(Armor, _settings.Armor);
        Selected = Math.Clamp(_settings.Selected, 0, 9);
        return true;

        void Restore(Container container, Settings.StackDto?[]? source)
        {
            if (source is null) return;
            for (int i = 0; i < container.Slots.Length && i < source.Length; i++)
            {
                var s = source[i];
                // Drop stacks whose ids no longer exist in /data; migrate old 64-caps.
                if (s is null || !IsKnownId(s.Id))
                {
                    container.Slots[i] = null;
                }
                else
                {
                    int max = MaxStackOf(s.Id);
                    int count = s.Count == 64 && max > 64 ? max : Math.Min(s.Count, max);
                    container.Slots[i] = new ItemStack(s.Id, count);
                }
            }
        }
    }
}
