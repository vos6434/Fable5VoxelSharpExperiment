using Silk.NET.OpenGL;

namespace Voxel.Client.Gl;

/// <summary>
/// The scene render target plus the composite-to-screen pass. Post effects
/// (volumetric fog, plan 06) will insert extra passes between the scene and
/// the composite; for now it is a straight blit through a fullscreen triangle.
/// Built here in plan 02 M0 so the ray-lighting perf gate has real timings.
/// </summary>
public sealed class PostChain : IDisposable
{
    private readonly GL _gl;
    private readonly GlShader _composite;
    private readonly uint _vao;

    public RenderTarget Scene { get; }

    public PostChain(GL gl, string shaderDir, int width, int height)
    {
        _gl = gl;
        Scene = new RenderTarget(gl, width, height);
        _composite = new GlShader(gl,
            File.ReadAllText(Path.Combine(shaderDir, "post.vert")),
            File.ReadAllText(Path.Combine(shaderDir, "composite.frag")));
        // Attribute-less VAO for the fullscreen triangle (core profile
        // requires a bound VAO for any draw; see SkyRenderer).
        _vao = gl.GenVertexArray();
    }

    public void Resize(int width, int height) => Scene.Resize(width, height);

    /// <summary>Binds the scene target for world rendering.</summary>
    public void BeginScene() => Scene.Bind();

    /// <summary>Composites the scene target to the default framebuffer.</summary>
    public void Composite(int screenWidth, int screenHeight)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)screenWidth, (uint)screenHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(_vao);
        _composite.Use();
        _composite.SetInt("uScene", 0);
        _composite.SetInt("uDepth", 1);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, Scene.ColorTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, Scene.DepthTexture);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        Scene.Dispose();
        _composite.Dispose();
    }
}
