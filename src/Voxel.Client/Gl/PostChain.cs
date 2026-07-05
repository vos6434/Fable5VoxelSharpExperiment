using Silk.NET.OpenGL;
using Voxel.Client;

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

    /// <summary>Binds the scene target for world rendering (black clear, no sky).</summary>
    public void BeginScene()
    {
        Scene.Bind();
        _gl.ClearColor(0f, 0f, 0f, 1f);
    }

    /// <summary>Composites scene + depth to the screen, drawing sky only where nothing hit.</summary>
    public void Composite(
        int screenWidth, int screenHeight,
        in SkyState sky,
        (float X, float Y, float Z) forward, (float X, float Y, float Z) right, (float X, float Y, float Z) up,
        float tanHalfFov, float aspect, float skyOpen)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)screenWidth, (uint)screenHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(_vao);
        _composite.Use();
        _composite.SetInt("uScene", 0);
        _composite.SetInt("uDepth", 1);
        _composite.SetVec3("uCamForward", forward.X, forward.Y, forward.Z);
        _composite.SetVec3("uCamRight", right.X, right.Y, right.Z);
        _composite.SetVec3("uCamUp", up.X, up.Y, up.Z);
        _composite.SetFloat("uTanHalfFov", tanHalfFov);
        _composite.SetFloat("uAspect", aspect);
        _composite.SetVec3("uSkyZenith", sky.Zenith.R, sky.Zenith.G, sky.Zenith.B);
        _composite.SetVec3("uSkyHorizon", sky.Horizon.R, sky.Horizon.G, sky.Horizon.B);
        _composite.SetVec3("uSunDir", sky.SunDir.X, sky.SunDir.Y, sky.SunDir.Z);
        _composite.SetVec3("uSunDiscColor", sky.SunDiscColor.R, sky.SunDiscColor.G, sky.SunDiscColor.B);
        _composite.SetFloat("uMoonVisibility", sky.MoonVisibility);
        _composite.SetFloat("uSkyOpen", skyOpen);
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
