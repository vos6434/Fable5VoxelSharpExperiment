using Silk.NET.OpenGL;

namespace Voxel.Client.Gl;

/// <summary>
/// Offscreen framebuffer: one HDR-ish color texture (RGBA16F) + a sampleable
/// depth texture. The scene renders here; the PostChain composites it to the
/// screen, inserting post effects (fog, plan 06) in between. Depth is a
/// texture so those effects can reconstruct world position.
/// </summary>
public sealed class RenderTarget : IDisposable
{
    private readonly GL _gl;
    public uint Fbo { get; private set; }
    public uint ColorTexture { get; private set; }
    public uint DepthTexture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public RenderTarget(GL gl, int width, int height)
    {
        _gl = gl;
        Allocate(width, height);
    }

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Release();
        Allocate(width, height);
    }

    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    private unsafe void Allocate(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        Fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);

        ColorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
            (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ColorTexture, 0);

        DepthTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
            (uint)Width, (uint)Height, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, DepthTexture, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"render target incomplete: {status}");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void Release()
    {
        _gl.DeleteFramebuffer(Fbo);
        _gl.DeleteTexture(ColorTexture);
        _gl.DeleteTexture(DepthTexture);
    }

    public void Dispose() => Release();
}
