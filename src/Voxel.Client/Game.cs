using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbImageWriteSharp;
using Voxel.Client.Gl;
using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// P3 foundation: window + OpenGL 3.3, chunk shader with texture array, fly
/// camera, and a locally generated patch of terrain around spawn (chunk
/// streaming from the server arrives in P4; the same meshes/materials carry
/// over unchanged).
/// </summary>
public sealed class Game
{
    private const int PatchRadiusH = 3;  // chunks, horizontal
    private const int PatchRadiusV = 2;  // chunks, vertical
    private const float FogFar = 96f;
    private const float FogNear = 64f;

    private static readonly (float R, float G, float B) SkyColor = (0x87 / 255f, 0xCE / 255f, 0xEB / 255f);

    private readonly IWindow _window;
    private readonly string _repoRoot;
    private readonly string? _screenshotPath;
    private readonly int _screenshotAfterFrames;

    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private ClientData _data = null!;
    private GlShader _shader = null!;
    private uint _atlasTexture;
    private readonly FlyCamera _camera = new();
    private readonly List<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> _solidMeshes = new();
    private readonly List<(int Cx, int Cy, int Cz, ChunkMesh Mesh)> _translucentMeshes = new();

    private System.Numerics.Vector2 _lastMouse;
    private bool _firstMouse = true;
    private int _frameCount;
    private double _fpsTimer;
    private int _fpsFrames;

    public Game(string repoRoot, string? screenshotPath, int screenshotAfterFrames)
    {
        _repoRoot = repoRoot;
        _screenshotPath = screenshotPath;
        _screenshotAfterFrames = screenshotAfterFrames;

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "Fable5VoxelSharp",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            VSync = true,
        };
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl.Viewport(size);
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        var mouse = _input.Mice[0];

        mouse.MouseMove += (_, pos) =>
        {
            if (_firstMouse) { _lastMouse = pos; _firstMouse = false; return; }
            _camera.ApplyMouseDelta(pos.X - _lastMouse.X, pos.Y - _lastMouse.Y);
            _lastMouse = pos;
        };
        mouse.MouseDown += (m, _) =>
        {
            if (!_camera.Captured)
            {
                _camera.Captured = true;
                m.Cursor.CursorMode = CursorMode.Disabled;
            }
        };
        _keyboard.KeyDown += (_, key, _) =>
        {
            if (key == Key.Escape && _camera.Captured)
            {
                _camera.Captured = false;
                mouse.Cursor.CursorMode = CursorMode.Normal;
            }
        };

        _data = ClientData.Load(Path.Combine(_repoRoot, "data"));
        Console.WriteLine($"[client] loaded {_data.Blocks.Count - 1} blocks, {_data.Items.Count} items, {_data.LayerCount} textures");

        _shader = new GlShader(
            _gl,
            File.ReadAllText(Path.Combine(_repoRoot, "shaders", "chunk.vert")),
            File.ReadAllText(Path.Combine(_repoRoot, "shaders", "chunk.frag")));
        _atlasTexture = CreateAtlasTexture();

        BuildLocalPatch();

        _gl.Enable(EnableCap.DepthTest);
        _gl.ClearColor(SkyColor.R, SkyColor.G, SkyColor.B, 1f);
    }

    private unsafe uint CreateAtlasTexture()
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, tex);
        fixed (byte* p = _data.AtlasPixels)
        {
            _gl.TexImage3D(
                TextureTarget.Texture2DArray, 0, (int)InternalFormat.Rgba8,
                ClientData.TilePx, ClientData.TilePx, (uint)_data.LayerCount,
                0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return tex;
    }

    /// <summary>Generates and meshes a local box of chunks around spawn (no server yet in P3).</summary>
    private void BuildLocalPatch()
    {
        var generator = new WorldGen(WorldGen.DefaultSeed, _data.Blocks);
        var spawn = generator.FindSpawn();
        _camera.X = spawn.X;
        _camera.Y = spawn.Y + 20;
        _camera.Z = spawn.Z;
        _camera.Yaw = -2.356f;   // face +x/+z, like the web client's spawn view
        _camera.Pitch = -0.35f;

        int scx = Coords.WorldToChunk(spawn.X);
        int scy = Coords.WorldToChunk(spawn.Y);
        int scz = Coords.WorldToChunk(spawn.Z);

        var chunks = new Dictionary<(int, int, int), ChunkData>();
        for (int cx = scx - PatchRadiusH - 1; cx <= scx + PatchRadiusH + 1; cx++)
        {
            for (int cy = scy - PatchRadiusV - 1; cy <= scy + PatchRadiusV + 1; cy++)
            {
                for (int cz = scz - PatchRadiusH - 1; cz <= scz + PatchRadiusH + 1; cz++)
                {
                    chunks[(cx, cy, cz)] = generator.Generate(cx, cy, cz).Chunk;
                }
            }
        }

        long triangles = 0;
        for (int cx = scx - PatchRadiusH; cx <= scx + PatchRadiusH; cx++)
        {
            for (int cy = scy - PatchRadiusV; cy <= scy + PatchRadiusV; cy++)
            {
                for (int cz = scz - PatchRadiusH; cz <= scz + PatchRadiusH; cz++)
                {
                    var result = GreedyMesher.Mesh(
                        chunks[(cx, cy, cz)].Blocks,
                        [
                            chunks.GetValueOrDefault((cx + 1, cy, cz))?.Blocks,
                            chunks.GetValueOrDefault((cx - 1, cy, cz))?.Blocks,
                            chunks.GetValueOrDefault((cx, cy + 1, cz))?.Blocks,
                            chunks.GetValueOrDefault((cx, cy - 1, cz))?.Blocks,
                            chunks.GetValueOrDefault((cx, cy, cz + 1))?.Blocks,
                            chunks.GetValueOrDefault((cx, cy, cz - 1))?.Blocks,
                        ],
                        _data.Blocks.Opaque, _data.RenderTable, _data.TranslucentMask);
                    if (result.Solid is not null)
                    {
                        _solidMeshes.Add((cx, cy, cz, new ChunkMesh(_gl, result.Solid)));
                        triangles += result.Solid.IndexCount / 3;
                    }
                    if (result.Translucent is not null)
                    {
                        _translucentMeshes.Add((cx, cy, cz, new ChunkMesh(_gl, result.Translucent)));
                        triangles += result.Translucent.IndexCount / 3;
                    }
                }
            }
        }
        Console.WriteLine(
            $"[client] local patch: {_solidMeshes.Count} solid + {_translucentMeshes.Count} translucent meshes, {triangles} tris");
    }

    private void OnUpdate(double dt)
    {
        _camera.Update(_keyboard, (float)dt);
    }

    private void OnRender(double dt)
    {
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = _window.FramebufferSize;
        float aspect = size.X / (float)Math.Max(1, size.Y);
        float[] viewProj = Mat4.Multiply(
            Mat4.PerspectiveGl(75f * MathF.PI / 180f, aspect, 0.1f, 1000f),
            Mat4.View(_camera.X, _camera.Y, _camera.Z, _camera.Yaw, _camera.Pitch));

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", _camera.X, _camera.Y, _camera.Z);
        _shader.SetVec3("uFogColor", SkyColor.R, SkyColor.G, SkyColor.B);
        _shader.SetFloat("uFogNear", FogNear);
        _shader.SetFloat("uFogFar", FogFar);
        _shader.SetInt("uAtlas", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

        // Solid pass: opaque + alpha-tested cutout, backface culled.
        _shader.SetFloat("uAlphaTest", 0.5f);
        _gl.Enable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        foreach (var (cx, cy, cz, mesh) in _solidMeshes)
        {
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
        }

        // Translucent pass: blended, no depth writes, both faces (water from below).
        _shader.SetFloat("uAlphaTest", 0.01f);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        foreach (var (cx, cy, cz, mesh) in _translucentMeshes)
        {
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
        }
        _gl.DepthMask(true);

        _frameCount++;
        _fpsFrames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 1.0)
        {
            _window.Title =
                $"Fable5VoxelSharp - {_fpsFrames} fps - pos {_camera.X:F1} {_camera.Y:F1} {_camera.Z:F1} - " +
                $"{_solidMeshes.Count + _translucentMeshes.Count} meshes";
            _fpsFrames = 0;
            _fpsTimer = 0;
        }

        if (_screenshotPath is not null && _frameCount == _screenshotAfterFrames)
        {
            SaveScreenshot(_screenshotPath);
            _window.Close();
        }
    }

    private unsafe void SaveScreenshot(string path)
    {
        var size = _window.FramebufferSize;
        int w = size.X, h = size.Y;
        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels)
        {
            _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        // GL reads bottom-up; flip to top-down for the PNG.
        var flipped = new byte[pixels.Length];
        for (int row = 0; row < h; row++)
        {
            Array.Copy(pixels, row * w * 4, flipped, (h - 1 - row) * w * 4, w * 4);
        }
        using var stream = File.Create(path);
        new ImageWriter().WritePng(flipped, w, h, ColorComponents.RedGreenBlueAlpha, stream);
        Console.WriteLine($"[client] screenshot saved to {path}");
    }
}
