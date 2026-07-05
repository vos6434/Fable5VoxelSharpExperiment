using Silk.NET.OpenGL;
using Voxel.Client.Gl;

namespace Voxel.Client.Ui;

/// <summary>
/// Immediate-mode sprite batch for the GUI: screen-pixel coordinates,
/// top-left origin, per-vertex color multiply. Batches until the bound
/// texture (or sampling mode) changes, then flushes one draw. Mode 0 samples
/// a 2D texture (gui art, icons, font, or the built-in white pixel for solid
/// rects); mode 1 samples the block texture array (pseudo-3D slot cubes).
/// </summary>
public sealed class UiBatch : IDisposable
{
    private const int FloatsPerVertex = 9; // pos2 uv2 layer1 color4

    private readonly GL _gl;
    private readonly GlShader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _whiteTex;
    private readonly uint _atlasTex;
    private readonly List<float> _verts = new(4096);
    private uint _currentTex;
    private int _currentMode;

    public UiBatch(GL gl, GlShader shader, uint atlasTexture)
    {
        _gl = gl;
        _shader = shader;
        _atlasTex = atlasTexture;

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        const uint stride = FloatsPerVertex * sizeof(float);
        unsafe
        {
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        }
        gl.BindVertexArray(0);

        _whiteTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _whiteTex);
        ReadOnlySpan<byte> white = [255, 255, 255, 255];
        unsafe
        {
            fixed (byte* p = white)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, 1, 1, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }

    public void Begin(int viewportW, int viewportH)
    {
        _shader.Use();
        _shader.SetVec2("uViewport", viewportW, viewportH);
        _shader.SetInt("uTex", 0);
        _shader.SetInt("uAtlas", 1);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2DArray, _atlasTex);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _verts.Clear();
        _currentTex = _whiteTex;
        _currentMode = 0;
    }

    public void End() => Flush();

    private void Use(uint tex, int mode)
    {
        if (tex == _currentTex && mode == _currentMode) return;
        Flush();
        _currentTex = tex;
        _currentMode = mode;
    }

    private void Flush()
    {
        if (_verts.Count == 0) return;
        _shader.SetInt("uMode", _currentMode);
        if (_currentMode == 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _currentTex);
        }
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verts);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, span, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_verts.Count / FloatsPerVertex));
        _verts.Clear();
    }

    // ---- primitives ----------------------------------------------------------

    private void Vertex(float x, float y, float u, float v, float layer, float r, float g, float b, float a)
    {
        _verts.AddRange([x, y, u, v, layer, r, g, b, a]);
    }

    /// <summary>Arbitrary quad (corners clockwise from top-left) with explicit UVs.</summary>
    public void Quad(
        uint tex, int mode,
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys,
        ReadOnlySpan<float> us, ReadOnlySpan<float> vs,
        float layer, float r, float g, float b, float a)
    {
        Use(tex, mode);
        Vertex(xs[0], ys[0], us[0], vs[0], layer, r, g, b, a);
        Vertex(xs[1], ys[1], us[1], vs[1], layer, r, g, b, a);
        Vertex(xs[2], ys[2], us[2], vs[2], layer, r, g, b, a);
        Vertex(xs[0], ys[0], us[0], vs[0], layer, r, g, b, a);
        Vertex(xs[2], ys[2], us[2], vs[2], layer, r, g, b, a);
        Vertex(xs[3], ys[3], us[3], vs[3], layer, r, g, b, a);
    }

    public void TexturedRect(uint tex, float x, float y, float w, float h,
        float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1,
        float r = 1, float g = 1, float b = 1, float a = 1)
    {
        Quad(tex, 0, [x, x + w, x + w, x], [y, y, y + h, y + h], [u0, u1, u1, u0], [v0, v0, v1, v1], 0, r, g, b, a);
    }

    public void SolidRect(float x, float y, float w, float h, float r, float g, float b, float a)
    {
        TexturedRect(_whiteTex, x, y, w, h, 0, 0, 1, 1, r, g, b, a);
    }

    public void BorderRect(float x, float y, float w, float h, float thickness, float r, float g, float b, float a)
    {
        SolidRect(x, y, w, thickness, r, g, b, a);
        SolidRect(x, y + h - thickness, w, thickness, r, g, b, a);
        SolidRect(x, y + thickness, thickness, h - 2 * thickness, r, g, b, a);
        SolidRect(x + w - thickness, y + thickness, thickness, h - 2 * thickness, r, g, b, a);
    }

    /// <summary>Block-face quad sampling the chunk texture array (mode 1).</summary>
    public void AtlasQuad(
        ReadOnlySpan<float> xs, ReadOnlySpan<float> ys,
        ReadOnlySpan<float> us, ReadOnlySpan<float> vs,
        int layer, float brightness)
    {
        Quad(0, 1, xs, ys, us, vs, layer, brightness, brightness, brightness, 1);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteTexture(_whiteTex);
    }
}
