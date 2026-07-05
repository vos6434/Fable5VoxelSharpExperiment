using System.Text.Json;
using Voxel.Shared;

namespace Voxel.Shared.Tests;

public class RegistryValidationTests
{
    private static DataSource Source(string json) =>
        new("test.json", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Valid_minimal_block_parses_with_defaults()
    {
        var registry = BlockRegistry.FromSources([Source(
            """{ "id": "ruby", "name": "Ruby", "textures": { "all": "ruby.png" }, "hardness": 3 }""")]);
        var def = registry.ById("ruby")!;
        Assert.Equal(1, def.NumericId);
        Assert.Equal(Transparency.Opaque, def.Transparency);
        Assert.Equal(Collision.Solid, def.Collision);
        Assert.Equal("stone", def.Sounds);
        Assert.Equal("none", def.Tool.Type);
        var drop = Assert.Single(def.Drops);
        Assert.Equal(("ruby", 1, 1.0), (drop.Item, drop.Count, drop.Chance));
        Assert.Equal("ruby.png", def.FaceTexture(FaceDir.Py));
        Assert.Equal("ruby.png", def.IconTexture());
    }

    [Theory]
    [InlineData("""{ "name": "X", "textures": { "all": "x.png" }, "hardness": 1 }""", "id")]
    [InlineData("""{ "id": "air", "name": "X", "textures": { "all": "x.png" }, "hardness": 1 }""", "reserved")]
    [InlineData("""{ "id": "Bad-Id", "name": "X", "textures": { "all": "x.png" }, "hardness": 1 }""", "id")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "all": "x.png" } }""", "hardness")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "top": "x.png" }, "hardness": 1 }""", "face")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "all": "x.jpg" }, "hardness": 1 }""", ".png")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "all": "x.png" }, "hardness": 1, "transparency": "invisible" }""", "transparency")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "all": "x.png" }, "hardness": 1, "lightEmission": 16 }""", "lightEmission")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "all": "x.png" }, "hardness": 1, "lightColor": "orange" }""", "lightColor")]
    [InlineData("""{ "id": "x", "name": "X", "textures": { "all": "x.png" }, "hardness": 1, "lightColor": "#FFAA" }""", "lightColor")]
    public void Invalid_blocks_fail_loudly(string json, string messageContains)
    {
        var ex = Assert.Throws<DataException>(() => BlockRegistry.FromSources([Source(json)]));
        Assert.Contains(messageContains, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Light_color_parses_hex_and_defaults_to_white()
    {
        var registry = BlockRegistry.FromSources([
            Source("""{ "id": "torch", "name": "T", "textures": { "all": "t.png" }, "hardness": 1, "lightEmission": 14, "lightColor": "#FF8000" }"""),
            Source("""{ "id": "plain", "name": "P", "textures": { "all": "p.png" }, "hardness": 1 }"""),
        ]);
        var torch = registry.ById("torch")!.LightColor;
        Assert.Equal(1f, torch.R, 3);
        Assert.Equal(128f / 255f, torch.G, 3);
        Assert.Equal(0f, torch.B, 3);
        Assert.Equal(LightColor.White, registry.ById("plain")!.LightColor);
    }

    [Fact]
    public void Duplicate_block_ids_are_rejected()
    {
        var json = """{ "id": "x", "name": "X", "textures": { "all": "x.png" }, "hardness": 1 }""";
        Assert.Throws<DataException>(() => BlockRegistry.FromSources([Source(json), Source(json)]));
    }

    [Fact]
    public void Item_maxStack_range_is_1_to_100()
    {
        Assert.Throws<DataException>(() => ItemRegistry.FromSources([Source(
            """{ "id": "x", "name": "X", "icon": "x.png", "maxStack": 101 }""")]));
        var items = ItemRegistry.FromSources([Source(
            """{ "id": "x", "name": "X", "icon": "x.png" }""")]);
        Assert.Equal(100, items.ById("x")!.MaxStack);
    }
}
