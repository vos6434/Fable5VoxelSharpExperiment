using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace Voxel.Client.Ui;

/// <summary>
/// ASCII bitmap font baked at load time from a system monospace TTF via
/// stb_truetype. Glyphs land in one 512x512 alpha texture; drawing goes
/// through the UiBatch with a shadow pass for legibility over the world.
/// </summary>
public sealed class UiFont : IDisposable
{
    private const int BitmapSize = 512;
    private const int FirstChar = 32;
    private const int CharCount = 96;

    public float LineHeight { get; }

    private readonly GL _gl;
    private readonly uint _texture;
    private readonly StbTrueType.stbtt_bakedchar[] _chars;

    public unsafe UiFont(GL gl, float pixelHeight)
    {
        _gl = gl;
        LineHeight = pixelHeight;

        string fontPath = FindFont();
        byte[] ttf = File.ReadAllBytes(fontPath);
        var alpha = new byte[BitmapSize * BitmapSize];
        _chars = new StbTrueType.stbtt_bakedchar[CharCount];

        fixed (byte* ttfPtr = ttf)
        fixed (byte* bitmapPtr = alpha)
        fixed (StbTrueType.stbtt_bakedchar* charsPtr = _chars)
        {
            int rows = StbTrueType.stbtt_BakeFontBitmap(
                ttfPtr, 0, pixelHeight, bitmapPtr, BitmapSize, BitmapSize, FirstChar, CharCount, charsPtr);
            if (rows <= 0) throw new InvalidOperationException($"font bake failed for {fontPath}");
        }

        // Alpha-only -> white RGBA so the batch's color multiply tints it.
        var rgba = new byte[BitmapSize * BitmapSize * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            rgba[i * 4] = 255;
            rgba[i * 4 + 1] = 255;
            rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = alpha[i];
        }

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        fixed (byte* p = rgba)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                BitmapSize, BitmapSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        Console.WriteLine($"[client] font baked from {Path.GetFileName(fontPath)}");
    }

    private static string FindFont()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "consola.ttf", "cour.ttf", "lucon.ttf", "arial.ttf", "segoeui.ttf" })
        {
            string path = Path.Combine(fonts, name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("no usable system font found for UI text");
    }

    public float Measure(string text)
    {
        float width = 0;
        foreach (char c in text)
        {
            int i = c - FirstChar;
            if (i < 0 || i >= CharCount) continue;
            width += _chars[i].xadvance;
        }
        return width;
    }

    /// <summary>Draws text with its baseline-adjusted top at y (screen pixels).</summary>
    public void Draw(UiBatch batch, float x, float y, string text, float r, float g, float b, float a = 1)
    {
        float penX = x;
        float baseline = y + LineHeight * 0.8f;
        foreach (char c in text)
        {
            int i = c - FirstChar;
            if (i < 0 || i >= CharCount) continue;
            var bc = _chars[i];
            float gx = penX + bc.xoff;
            float gy = baseline + bc.yoff;
            float gw = bc.x1 - bc.x0;
            float gh = bc.y1 - bc.y0;
            batch.TexturedRect(
                _texture, gx, gy, gw, gh,
                bc.x0 / (float)BitmapSize, bc.y0 / (float)BitmapSize,
                bc.x1 / (float)BitmapSize, bc.y1 / (float)BitmapSize,
                r, g, b, a);
            penX += bc.xadvance;
        }
    }

    public void DrawShadowed(UiBatch batch, float x, float y, string text, float r = 1, float g = 1, float b = 1)
    {
        Draw(batch, x + 1, y + 1, text, 0.1f, 0.1f, 0.1f, 0.9f);
        Draw(batch, x, y, text, r, g, b);
    }

    public void Dispose() => _gl.DeleteTexture(_texture);
}
