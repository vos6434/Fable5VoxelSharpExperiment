using StbImageSharp;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Everything the renderer derives from /data at startup: registries, the
/// texture-array pixel data (one 16x16 RGBA layer per unique world texture,
/// rows flipped so v grows upward — same convention as the web client), the
/// per-face layer table, and the translucent-pass mask.
/// </summary>
public sealed class ClientData
{
    public const int TilePx = 16;

    public required BlockRegistry Blocks { get; init; }
    public required ItemRegistry Items { get; init; }
    /// <summary>RGBA pixels, LayerCount layers of 16x16.</summary>
    public required byte[] AtlasPixels { get; init; }
    public required int LayerCount { get; init; }
    /// <summary>renderTable[numericId * 6 + faceIndex] = atlas layer (FACE_DIRS order).</summary>
    public required ushort[] RenderTable { get; init; }
    /// <summary>translucentMask[numericId] = 1 for alpha-blended blocks (water, ice).</summary>
    public required byte[] TranslucentMask { get; init; }
    /// <summary>emissiveMask[numericId] = 1 for light-emitting blocks (rendered fullbright).</summary>
    public required byte[] EmissiveMask { get; init; }

    public static ClientData Load(string dataRoot)
    {
        var (blocks, items) = DataLoader.LoadRegistries(dataRoot);

        // Unique world textures across all block faces, in deterministic order.
        var layerByName = new Dictionary<string, int>();
        var textureNames = new List<string>();
        var faceDirs = Enum.GetValues<FaceDir>();
        foreach (var def in blocks.Defs)
        {
            if (def.NumericId == 0) continue;
            foreach (var face in faceDirs)
            {
                string name = def.FaceTexture(face);
                if (layerByName.TryAdd(name, textureNames.Count)) textureNames.Add(name);
            }
        }

        const int layerBytes = TilePx * TilePx * 4;
        var pixels = new byte[layerBytes * textureNames.Count];
        for (int layer = 0; layer < textureNames.Count; layer++)
        {
            string path = Path.Combine(dataRoot, "blocks", textureNames[layer]);
            var image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            if (image.Width != TilePx || image.Height != TilePx)
            {
                throw new DataException(textureNames[layer], $"texture is {image.Width}x{image.Height}, expected 16x16");
            }
            // Flip rows so that v=1 samples the image's top row (block top edge).
            for (int row = 0; row < TilePx; row++)
            {
                Array.Copy(
                    image.Data, row * TilePx * 4,
                    pixels, layer * layerBytes + (TilePx - 1 - row) * TilePx * 4,
                    TilePx * 4);
            }
        }

        var renderTable = new ushort[blocks.Count * 6];
        var translucentMask = new byte[blocks.Count];
        var emissiveMask = new byte[blocks.Count];
        foreach (var def in blocks.Defs)
        {
            if (def.NumericId == 0) continue;
            translucentMask[def.NumericId] = (byte)(def.Transparency == Transparency.Translucent ? 1 : 0);
            emissiveMask[def.NumericId] = (byte)(def.LightEmission > 0 ? 1 : 0);
            foreach (var face in faceDirs)
            {
                renderTable[def.NumericId * 6 + (int)face] = (ushort)layerByName[def.FaceTexture(face)];
            }
        }

        return new ClientData
        {
            Blocks = blocks,
            Items = items,
            AtlasPixels = pixels,
            LayerCount = textureNames.Count,
            RenderTable = renderTable,
            TranslucentMask = translucentMask,
            EmissiveMask = emissiveMask,
        };
    }
}
