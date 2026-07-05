using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbImageWriteSharp;
using Voxel.Client.Gl;
using Voxel.Shared;

namespace Voxel.Client;

public sealed record GameOptions
{
    public required string RepoRoot { get; init; }
    public Uri Server { get; init; } = new($"ws://localhost:{Protocol.Port}");
    public string? ScreenshotPath { get; init; }
    public int ScreenshotAfterFrames { get; init; } = 90;
    public (double X, double Y, double Z)? StartPosition { get; init; }
    public (float Yaw, float Pitch)? StartLook { get; init; }
}

/// <summary>
/// P4: server-fed native client — chunks stream from the C# game server into
/// a 3D-radius StreamingWorld, meshing runs on a thread pool, and the hell
/// atmosphere (sky/fog fade) tracks camera depth like the web client.
/// </summary>
public sealed class Game
{
    private const float FogFar = StreamingWorld.RenderRadius * Constants.ChunkSize;
    private const float FogNear = FogFar - 2 * Constants.ChunkSize;

    private static readonly (float R, float G, float B) SurfaceSky = (0x87 / 255f, 0xCE / 255f, 0xEB / 255f);
    private static readonly (float R, float G, float B) HellSky = (0x2A / 255f, 0x07 / 255f, 0x05 / 255f);

    private readonly GameOptions _options;
    private readonly IWindow _window;
    private readonly string _playerName = $"player-{Random.Shared.Next(10000)}";

    private GL _gl = null!;
    private IKeyboard _keyboard = null!;
    private ClientData _data = null!;
    private Connection _connection = null!;
    private WorldGen _generator = null!;   // display only: biome readout + hell boundary
    private StreamingWorld _world = null!;
    private GlShader _shader = null!;
    private uint _atlasTexture;
    private readonly FlyCamera _camera = new();

    private System.Numerics.Vector2 _lastMouse;
    private bool _firstMouse = true;
    private int _frameCount;
    private double _fpsTimer;
    private int _fpsFrames;
    private double _moveSendTimer;

    public Game(GameOptions options)
    {
        _options = options;
        var windowOptions = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "Fable5VoxelSharp",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            VSync = true,
        };
        _window = Window.Create(windowOptions);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Closing += () => _connection?.Dispose();
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        var input = _window.CreateInput();
        _keyboard = input.Keyboards[0];
        var mouse = input.Mice[0];

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

        _data = ClientData.Load(Path.Combine(_options.RepoRoot, "data"));
        Console.WriteLine($"[client] loaded {_data.Blocks.Count - 1} blocks, {_data.Items.Count} items, {_data.LayerCount} textures");

        _connection = Connection.ConnectAsync(_options.Server, _playerName, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult();
        var localPalette = _data.Blocks.Defs.Select(d => d.StringId).ToArray();
        if (!localPalette.SequenceEqual(_connection.Palette))
        {
            throw new InvalidOperationException("client/server block palette mismatch - restart both after changing /data");
        }
        Console.WriteLine($"[client] online as {_playerName} (#{_connection.PlayerId}) on {_options.Server}");

        _generator = new WorldGen(_connection.Seed, _data.Blocks);
        _world = new StreamingWorld(_gl, _data, _connection);

        var start = _options.StartPosition ?? (_connection.Spawn.X, _connection.Spawn.Y, _connection.Spawn.Z);
        _camera.X = (float)start.X;
        _camera.Y = (float)start.Y;
        _camera.Z = (float)start.Z;
        (_camera.Yaw, _camera.Pitch) = _options.StartLook ?? (-2.356f, -0.35f);

        _shader = new GlShader(
            _gl,
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "chunk.vert")),
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "chunk.frag")));
        _atlasTexture = CreateAtlasTexture();
        _gl.Enable(EnableCap.DepthTest);
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

    private void OnUpdate(double dt)
    {
        _camera.Update(_keyboard, (float)dt);
        _world.Update(_camera.X, _camera.Y, _camera.Z);

        // Position sync at 10 Hz (P5 renders the other players).
        _moveSendTimer += dt;
        if (_moveSendTimer >= 0.1)
        {
            _moveSendTimer = 0;
            _connection.SendMove(_camera.X, _camera.Y, _camera.Z, _camera.Yaw, _camera.Pitch);
        }
    }

    private void OnRender(double dt)
    {
        // Hell atmosphere: fade sky/fog to dark red approaching the boundary.
        double hellB = _generator.HellBoundary(_camera.X, _camera.Z);
        float hellT = (float)Math.Clamp((hellB + 24 - _camera.Y) / 32, 0, 1);
        float skyR = SurfaceSky.R + (HellSky.R - SurfaceSky.R) * hellT;
        float skyG = SurfaceSky.G + (HellSky.G - SurfaceSky.G) * hellT;
        float skyB = SurfaceSky.B + (HellSky.B - SurfaceSky.B) * hellT;
        float fogNear = FogNear + (12 - FogNear) * hellT;
        float fogFar = FogFar + (64 - FogFar) * hellT;

        _gl.ClearColor(skyR, skyG, skyB, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = _window.FramebufferSize;
        float aspect = size.X / (float)Math.Max(1, size.Y);
        float[] viewProj = Mat4.Multiply(
            Mat4.PerspectiveGl(75f * MathF.PI / 180f, aspect, 0.1f, 1000f),
            Mat4.View(_camera.X, _camera.Y, _camera.Z, _camera.Yaw, _camera.Pitch));

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", _camera.X, _camera.Y, _camera.Z);
        _shader.SetVec3("uFogColor", skyR, skyG, skyB);
        _shader.SetFloat("uFogNear", fogNear);
        _shader.SetFloat("uFogFar", fogFar);
        _shader.SetInt("uAtlas", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);

        long triangles = 0;
        int draws = 0;

        // Solid pass: opaque + alpha-tested cutout, backface culled.
        _shader.SetFloat("uAlphaTest", 0.5f);
        _gl.Enable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        foreach (var (cx, cy, cz, mesh) in _world.SolidMeshes())
        {
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
            triangles += mesh.IndexCount / 3;
            draws++;
        }

        // Translucent pass: blended, no depth writes, both faces (water from below).
        _shader.SetFloat("uAlphaTest", 0.01f);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        foreach (var (cx, cy, cz, mesh) in _world.TranslucentMeshes())
        {
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
            triangles += mesh.IndexCount / 3;
            draws++;
        }
        _gl.DepthMask(true);

        _frameCount++;
        _fpsFrames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 1.0)
        {
            var s = _world.Stats;
            _window.Title =
                $"Fable5VoxelSharp - {_fpsFrames} fps - {draws} draws {triangles} tris - " +
                $"pos {_camera.X:F0} {_camera.Y:F0} {_camera.Z:F0} - " +
                $"biome {_generator.BiomeAt(_camera.X, _camera.Y, _camera.Z)} - " +
                $"chunks {s.Loaded} loaded {s.Rendered} rendered, net {s.AwaitingNet} mesh {s.PendingMesh}";
            _fpsFrames = 0;
            _fpsTimer = 0;
        }

        if (_world.DisconnectReason is not null)
        {
            Console.WriteLine($"[client] disconnected: {_world.DisconnectReason}");
            _window.Close();
        }

        if (_options.ScreenshotPath is not null && _frameCount == _options.ScreenshotAfterFrames)
        {
            var s = _world.Stats;
            Console.WriteLine(
                $"[client] at screenshot: {s.Loaded} loaded, {s.Rendered} rendered, " +
                $"{draws} draws, {triangles} tris, biome {_generator.BiomeAt(_camera.X, _camera.Y, _camera.Z)}");
            SaveScreenshot(_options.ScreenshotPath);
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
