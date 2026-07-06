using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbImageWriteSharp;
using Voxel.Client.Gl;
using Voxel.Client.Ui;
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
    /// <summary>Verification hook: after streaming settles, place a glowstone column and break a block via the interaction code path.</summary>
    public bool DemoEdits { get; init; }
    /// <summary>Verification hook: place a tall glowstone column at the camera's feet (cave pillar test).</summary>
    public bool DemoPillar { get; init; }
    /// <summary>Verification hook: open the inventory + creative windows with a cursor stack.</summary>
    public bool DemoGui { get; init; }
    /// <summary>Verification hook: show the pause menu.</summary>
    public bool DemoPause { get; init; }
    /// <summary>Verification hook: open the debug menu (time slider).</summary>
    public bool DemoDebug { get; init; }
    /// <summary>Verification hook: pin the day/night clock to a fixed tick.</summary>
    public long? ForceTimeTicks { get; init; }
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
    private GlShader _colorShader = null!;
    private ColorMesh _cubeLines = null!;
    private ColorMesh _cubeTriangles = null!;
    private uint _atlasTexture;
    private PostChain _postChain = null!;
    private OccupancyVolume _occupancy = null!;
    private LightVolume _lights = null!;
    private GpuTimer _timerSky = null!;
    private GpuTimer _timerWorld = null!;
    private GpuTimer _timerUi = null!;
    private readonly FlyCamera _camera = new();
    private readonly RemotePlayers _players = new();
    private readonly ClientClock _clock = new();
    private EntityRenderer _entities = null!;
    private (int X, int Y, int Z)? _glueCorner1;
    private (int X, int Y, int Z)? _glueCorner2;
    private uint _gunHoldEntityId;
    private int _gunHoldDistanceCenti = 400; // 4 blocks default
    private static readonly float[] IdentityMat = Mat4.Identity();

    private Settings _settings = null!;
    private PlayerInventory _inventory = null!;
    private GuiSystem _gui = null!;
    private UiBatch _uiBatch = null!;
    private UiFont _font = null!;
    private IMouse _mouse = null!;

    private const float Reach = 6f;
    private AimTarget? _target;

    private System.Numerics.Vector2 _lastMouse;
    private bool _firstMouse = true;
    private int _frameCount;
    private double _fpsTimer;
    private int _fpsFrames;
    private int _lastFps;
    private double _moveSendTimer;
    private long? _pendingTimeTick;    // debug slider: latest scrub not yet sent
    private float _timeControlCooldown;
    private readonly float[] _frustumPlanes = new float[24];

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

        _mouse = mouse;
        mouse.MouseMove += (_, pos) =>
        {
            if (_camera.Captured)
            {
                if (_firstMouse) { _lastMouse = pos; _firstMouse = false; return; }
                _camera.ApplyMouseDelta(pos.X - _lastMouse.X, pos.Y - _lastMouse.Y);
                _lastMouse = pos;
            }
            else
            {
                _gui?.OnMouseMove(pos.X, pos.Y);
                _lastMouse = pos;
            }
        };
        mouse.MouseDown += (_, button) =>
        {
            int uiButton = button == MouseButton.Right ? 2 : 0;
            if (_camera.Captured)
            {
                // Glue in hand (WorldEdit-style): LMB sets corner 1, RMB sets
                // corner 2, Ctrl targets the adjacent air cell instead of the solid,
                // Shift+RMB glues the box into a contraption, Shift+LMB clears.
                if (_inventory.SelectedStack()?.Id == "glue")
                {
                    bool shift = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
                    bool ctrl = _keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight);
                    if (button == MouseButton.Left && shift)
                        _connection.SendUseItem(Protocol.ItemAction.GlueClear, 0, 0, 0);
                    else if (button == MouseButton.Right && shift)
                        _connection.SendUseItem(Protocol.ItemAction.GlueActivate, 0, 0, 0);
                    else if (button == MouseButton.Left && GlueCornerFromAim(ctrl) is { } c1)
                        _connection.SendUseItem(Protocol.ItemAction.GlueCorner1, c1.X, c1.Y, c1.Z);
                    else if (button == MouseButton.Right && GlueCornerFromAim(ctrl) is { } c2)
                        _connection.SendUseItem(Protocol.ItemAction.GlueCorner2, c2.X, c2.Y, c2.Z);
                    return;
                }
                if (_inventory.SelectedStack()?.Id == "physics_gun")
                {
                    if (button == MouseButton.Right)
                    {
                        if (_gunHoldEntityId == 0)
                            _connection.SendUseItem(Protocol.ItemAction.GunGrab, 0, 0, 0);
                        else
                            _connection.SendUseItem(Protocol.ItemAction.GunRelease, 0, 0, 0);
                    }
                    else if (button == MouseButton.Left && _gunHoldEntityId != 0)
                        _connection.SendUseItem(Protocol.ItemAction.GunThrow, 0, 0, 0);
                    return;
                }
                if (button == MouseButton.Left) BreakTargeted();
                else if (button == MouseButton.Right) PlaceAtTarget();
                return;
            }
            if (_gui.OnMouseDown(_lastMouse.X, _lastMouse.Y, uiButton)) return;
            // Click on empty space with nothing open: capture and play.
            if (!_gui.AnyOpen && !_gui.PauseVisible) SetCaptured(true);
        };
        mouse.MouseUp += (_, _) => _gui?.OnMouseUp();
        mouse.Scroll += (_, wheel) =>
        {
            if (!_camera.Captured) return;
            if (_inventory.SelectedStack()?.Id == "physics_gun" && _gunHoldEntityId != 0)
            {
                int dir = wheel.Y < 0 ? -50 : 50; // 0.5 block steps
                _gunHoldDistanceCenti = Math.Clamp(_gunHoldDistanceCenti + dir, 200, 800);
                _connection.SendUseItem(Protocol.ItemAction.GunSetDistance, 0, 0, _gunHoldDistanceCenti);
                return;
            }
            int hotbarDir = wheel.Y < 0 ? 1 : -1;
            _inventory.Selected = (_inventory.Selected + hotbarDir + 10) % 10;
            _inventory.Save();
        };
        _keyboard.KeyDown += (_, key, _) =>
        {
            switch (key)
            {
                case Key.E:
                    // Toggle inventory + creative together (web parity).
                    if (_gui.CloseAll())
                    {
                        if (!_gui.PauseVisible) SetCaptured(true);
                    }
                    else
                    {
                        SetCaptured(false);
                        _gui.Open("inventory");
                        _gui.Open("creative_menu");
                    }
                    return;
                case Key.Escape:
                    if (_gui.PauseVisible)
                    {
                        _gui.HidePause();
                        SetCaptured(true);
                    }
                    else if (_gui.CloseAll())
                    {
                        SetCaptured(true);
                    }
                    else if (_camera.Captured)
                    {
                        SetCaptured(false);
                        _gui.ShowPause();
                    }
                    return;
                case Key.F3:
                    if (_gui.DebugVisible)
                    {
                        _gui.HideDebug();
                        if (!_gui.AnyOpen && !_gui.PauseVisible) SetCaptured(true);
                    }
                    else
                    {
                        _gui.ShowDebug();
                        SetCaptured(false);
                    }
                    return;
                default:
                {
                    int? slot = key switch
                    {
                        >= Key.Number1 and <= Key.Number9 => key - Key.Number1,
                        Key.Number0 => 9,
                        _ => null,
                    };
                    if (slot is int s)
                    {
                        _inventory.Selected = s;
                        _inventory.Save();
                    }
                    break;
                }
            }
        };

        _data = ClientData.Load(Path.Combine(_options.RepoRoot, "data"));
        Console.WriteLine($"[client] loaded {_data.Blocks.Count - 1} blocks, {_data.Items.Count} items, {_data.LayerCount} textures");

        _connection = Connection.ConnectAsync(_options.Server, _playerName, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult();
        var localPalette = _data.Blocks.Defs.Select(d => d.StringId).ToArray();
        if (!localPalette.SequenceEqual(_connection.Palette))
        {
            throw new ClientConnectException("client/server block palette mismatch - restart both after changing /data");
        }
        Console.WriteLine($"[client] online as {_playerName} (#{_connection.PlayerId}) on {_options.Server}");

        _generator = new WorldGen(_connection.Seed, _data.Blocks);
        _world = new StreamingWorld(_gl, _data, _connection);
        _entities = new EntityRenderer(_gl, _data);

        if (_options.ForceTimeTicks is long forced)
        {
            _clock.Force(forced, Constants.DayLengthTicks);
        }

        var start = _options.StartPosition ?? (_connection.Spawn.X, _connection.Spawn.Y, _connection.Spawn.Z);
        _camera.X = (float)start.X;
        _camera.Y = (float)start.Y;
        _camera.Z = (float)start.Z;
        (_camera.Yaw, _camera.Pitch) = _options.StartLook ?? (-2.356f, -0.35f);

        _shader = new GlShader(
            _gl,
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "chunk.vert")),
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "chunk.frag")));
        _colorShader = new GlShader(
            _gl,
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "color.vert")),
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "color.frag")));
        _cubeLines = ColorMesh.UnitCubeLines(_gl);
        _cubeTriangles = ColorMesh.UnitCubeTriangles(_gl);
        _atlasTexture = CreateAtlasTexture();
        _gl.Enable(EnableCap.DepthTest);

        // ---- Render pipeline (plan 02 M0): offscreen scene + composite + timers
        _settings = Settings.Load(Path.Combine(_options.RepoRoot, "settings.json"));
        string shaderDir = Path.Combine(_options.RepoRoot, "shaders");
        _postChain = new PostChain(_gl, shaderDir, _window.FramebufferSize.X, _window.FramebufferSize.Y);
        int shadowRadius = Math.Clamp(_settings.ShadowRegionRadius, 4, 6);
        _occupancy = new OccupancyVolume(_gl, _data.Blocks.Opaque, _data.EmissiveMask, shadowRadius);
        _lights = new LightVolume(_gl, _data.Blocks, shadowRadius);
        _timerSky = new GpuTimer(_gl, "sky");
        _timerWorld = new GpuTimer(_gl, "world");
        _timerUi = new GpuTimer(_gl, "ui");
        _window.FramebufferResize += size => _postChain.Resize(size.X, size.Y);

        // ---- GUI system -----------------------------------------------------
        _inventory = new PlayerInventory(_data.Blocks, _data.Items, _settings);
        _gui = new GuiSystem(_gl, _data, _inventory, _settings, Path.Combine(_options.RepoRoot, "data"));
        _gui.OnResume = () => SetCaptured(true);

        // Debug menu: slider t (0..1) spans clock time 00:00..24:00; ticks are
        // 06:00-anchored (day boundary = sunrise), hence the ±6h shifts.
        _gui.DebugState = () =>
        {
            double ticks = Math.Max(0, _clock.WorldTicks);
            double dayFraction = ticks % _clock.DayLengthTicks / _clock.DayLengthTicks;
            float sliderT = (float)((dayFraction * 24 + 6) % 24 / 24);
            return (sliderT, _clock.Describe(), _clock.Timescale == 0f);
        };
        _gui.OnDebugTime = t =>
        {
            long day = (long)(Math.Max(0, _clock.WorldTicks) / _clock.DayLengthTicks);
            double dayFraction = ((t * 24 - 6) + 24) % 24 / 24;
            long tick = day * _clock.DayLengthTicks + (long)(dayFraction * _clock.DayLengthTicks);
            _clock.OnSync(tick, _clock.Timescale, _clock.DayLengthTicks); // instant local feedback
            _pendingTimeTick = tick; // sent to the server throttled (OnUpdate)
        };
        _gui.OnDebugPause = paused =>
        {
            // Resume always returns to x1 (an explicit rate is still available
            // via the server's "tick rate" console command).
            float scale = paused ? 0f : 1f;
            _clock.OnSync((long)Math.Max(0, _clock.WorldTicks), scale, _clock.DayLengthTicks);
            _connection.SendTimeControl(-1, scale);
        };
        // Quality knob (plan 02 M8): cycle the shadowed-light cap, persisted.
        _gui.DebugLightCap = () => Math.Clamp(_settings.ShadowedLightCap, 0, 8);
        _gui.OnDebugCycleLightCap = () =>
        {
            _settings.ShadowedLightCap = _settings.ShadowedLightCap switch { 0 => 2, 2 => 4, 4 => 8, _ => 0 };
            _settings.Save();
        };
        _gui.SetScreenSize(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        _window.FramebufferResize += size => _gui.SetScreenSize(size.X, size.Y);
        var uiShader = new GlShader(
            _gl,
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "ui.vert")),
            File.ReadAllText(Path.Combine(_options.RepoRoot, "shaders", "ui.frag")));
        _uiBatch = new UiBatch(_gl, uiShader, _atlasTexture);
        _font = new UiFont(_gl, 18f);
    }

    private void SetCaptured(bool captured)
    {
        _camera.Captured = captured;
        _mouse.Cursor.CursorMode = captured ? CursorMode.Disabled : CursorMode.Normal;
        _firstMouse = true;
    }

    // ---- interaction ---------------------------------------------------------

    private (double X, double Y, double Z) AimDirection()
    {
        float cy = MathF.Cos(_camera.Yaw), sy = MathF.Sin(_camera.Yaw);
        float cp = MathF.Cos(_camera.Pitch), sp = MathF.Sin(_camera.Pitch);
        return (-sy * cp, sp, -cy * cp);
    }

    private bool IsTargetable(int x, int y, int z)
    {
        ushort id = _world.GetBlock(x, y, z);
        return id != 0 && _data.Blocks.Get(id).Collision != Collision.Liquid;
    }

    private void DrawThickBoxEdge(float ox, float oy, float oz, float sx, float sy, float sz, float r, float g, float b)
    {
        _colorShader.SetInt("uUseModel", 0);
        _colorShader.SetVec3("uOrigin", ox, oy, oz);
        _colorShader.SetVec3("uScale", sx, sy, sz);
        _colorShader.SetFloat4("uColor", r, g, b, 1f);
        _cubeTriangles.Draw();
    }

    private void DrawGlueCornerMarker((int X, int Y, int Z) c, float r, float g, float b)
    {
        const float pad = 0.006f;
        _colorShader.SetInt("uUseModel", 0);
        _colorShader.SetVec3("uOrigin", c.X - pad, c.Y - pad, c.Z - pad);
        _colorShader.SetVec3("uScale", 1 + pad * 2, 1 + pad * 2, 1 + pad * 2);
        _colorShader.SetFloat4("uColor", r, g, b, 1f);
        _cubeTriangles.Draw();
    }

    /// <summary>WorldEdit-style AABB outline from block corners (inclusive).</summary>
    private void DrawGlueSelectionBox(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        const float pad = 0.002f;
        const float t = 0.04f;
        float ox = minX - pad, oy = minY - pad, oz = minZ - pad;
        float sx = maxX - minX + 1 + pad * 2;
        float sy = maxY - minY + 1 + pad * 2;
        float sz = maxZ - minZ + 1 + pad * 2;
        float d = t * 2;
        // Bright lime — readable on water, grass, and night terrain.
        const float r = 0.35f, g = 1f, b = 0.3f;

        // Bottom (y = oy)
        DrawThickBoxEdge(ox, oy - t, oz - t, sx, d, d, r, g, b);
        DrawThickBoxEdge(ox, oy - t, oz + sz - t, sx, d, d, r, g, b);
        DrawThickBoxEdge(ox - t, oy - t, oz, d, d, sz, r, g, b);
        DrawThickBoxEdge(ox + sx - t, oy - t, oz, d, d, sz, r, g, b);
        // Top (y = oy + sy)
        DrawThickBoxEdge(ox, oy + sy - t, oz - t, sx, d, d, r, g, b);
        DrawThickBoxEdge(ox, oy + sy - t, oz + sz - t, sx, d, d, r, g, b);
        DrawThickBoxEdge(ox - t, oy + sy - t, oz, d, d, sz, r, g, b);
        DrawThickBoxEdge(ox + sx - t, oy + sy - t, oz, d, d, sz, r, g, b);
        // Vertical pillars
        DrawThickBoxEdge(ox - t, oy, oz - t, d, sy, d, r, g, b);
        DrawThickBoxEdge(ox + sx - t, oy, oz - t, d, sy, d, r, g, b);
        DrawThickBoxEdge(ox - t, oy, oz + sz - t, d, sy, d, r, g, b);
        DrawThickBoxEdge(ox + sx - t, oy, oz + sz - t, d, sy, d, r, g, b);
    }

    private RaycastHit? WorldAim()
    {
        var (dx, dy, dz) = AimDirection();
        return Raycast.Cast(_camera.X, _camera.Y, _camera.Z, dx, dy, dz, Reach, IsTargetable);
    }

    private AimTarget? Aim()
    {
        var (dx, dy, dz) = AimDirection();
        var world = Raycast.Cast(_camera.X, _camera.Y, _camera.Z, dx, dy, dz, Reach, IsTargetable);
        var entity = _entities.Raycast(_camera.X, _camera.Y, _camera.Z, dx, dy, dz, Reach);

        if (world is null && entity is null) return null;
        if (world is null) return new AimTarget.Entity(entity!.Value);
        if (entity is null) return new AimTarget.World(world.Value);
        return world.Value.Distance <= entity.Value.Distance
            ? new AimTarget.World(world.Value)
            : new AimTarget.Entity(entity.Value);
    }

    /// <summary>WorldEdit corner: solid under crosshair, or adjacent air cell when ctrl is held.</summary>
    private (int X, int Y, int Z)? GlueCornerFromAim(bool allowAir)
    {
        if (WorldAim() is not RaycastHit hit) return null;
        if (allowAir)
        {
            int ax = hit.X + hit.Nx, ay = hit.Y + hit.Ny, az = hit.Z + hit.Nz;
            if (_world.GetBlock(ax, ay, az) != 0) return null;
            return (ax, ay, az);
        }
        if (_world.GetBlock(hit.X, hit.Y, hit.Z) is ushort id && id != 0
            && _data.Blocks.Get(id).Collision != Collision.Liquid)
            return (hit.X, hit.Y, hit.Z);
        return null;
    }

    private (int X, int Y, int Z)? GluePreviewCorner()
    {
        if (WorldAim() is not RaycastHit hit) return null;
        bool ctrl = _keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight);
        if (ctrl)
        {
            int ax = hit.X + hit.Nx, ay = hit.Y + hit.Ny, az = hit.Z + hit.Nz;
            if (_world.GetBlock(ax, ay, az) != 0) return null;
            return (ax, ay, az);
        }
        return (hit.X, hit.Y, hit.Z);
    }

    private void BreakTargeted()
    {
        if (Aim() is not { } target) return;
        switch (target)
        {
            case AimTarget.World w:
                _world.SetBlock(w.Hit.X, w.Hit.Y, w.Hit.Z, 0);
                _connection.SendSetBlock(w.Hit.X, w.Hit.Y, w.Hit.Z, 0);
                break;
            case AimTarget.Entity e:
                _connection.SendEntityBlock(e.Hit.EntityId, e.Hit.LocalX, e.Hit.LocalY, e.Hit.LocalZ, 0);
                break;
        }
    }

    private void PlaceAtTarget()
    {
        if (Aim() is not { } target) return;
        var stack = _inventory.SelectedStack();
        var blockDef = stack is null ? null : _data.Blocks.ById(stack.Id);
        if (blockDef is null) return;
        ushort blockId = (ushort)blockDef.NumericId;

        switch (target)
        {
            case AimTarget.World w:
            {
                int px = w.Hit.X + w.Hit.Nx;
                int py = w.Hit.Y + w.Hit.Ny;
                int pz = w.Hit.Z + w.Hit.Nz;
                if (_world.GetBlock(px, py, pz) != 0) return;
                _world.SetBlock(px, py, pz, blockId);
                _connection.SendSetBlock(px, py, pz, blockId);
                break;
            }
            case AimTarget.Entity e:
            {
                int px = e.Hit.LocalX + e.Hit.Nx;
                int py = e.Hit.LocalY + e.Hit.Ny;
                int pz = e.Hit.LocalZ + e.Hit.Nz;
                if (!_entities.IsEmptyCell(e.Hit.EntityId, px, py, pz)) return;
                _connection.SendEntityBlock(e.Hit.EntityId, px, py, pz, blockId);
                break;
            }
        }
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

        // Route server events: world takes chunks/blocks, players and clock take the rest.
        while (_connection.Events.TryDequeue(out var evt))
        {
            _world.HandleEvent(evt);
            _players.Handle(evt, _connection.PlayerId);
            _entities.Handle(evt);
            if (evt is ServerEvent.TimeSynced sync)
            {
                _clock.OnSync(sync.WorldTick, sync.Timescale, sync.DayLengthTicks);
            }
            else if (evt is ServerEvent.GlueSelectionUpdated glue)
            {
                _glueCorner1 = glue.Corner1;
                _glueCorner2 = glue.Corner2;
            }
            else if (evt is ServerEvent.GunHoldUpdated gun)
            {
                _gunHoldEntityId = gun.EntityId;
            }
        }

        _world.Update(_camera.X, _camera.Y, _camera.Z);
        _occupancy.Update(_world, _camera.X, _camera.Y, _camera.Z, uploadBudget: 24);
        _lights.Update(_world, _occupancy.OriginChunk, dt);
        _players.Update((float)dt);
        _entities.Update((float)dt);
        _target = Aim();

        // Position sync at 10 Hz.
        _moveSendTimer += dt;
        if (_moveSendTimer >= 0.1)
        {
            _moveSendTimer = 0;
            _connection.SendMove(_camera.X, _camera.Y, _camera.Z, _camera.Yaw, _camera.Pitch);
        }

        // Debug time slider: scrubbing updates the local clock every frame but
        // sends to the server at most 10x/s; the last value always goes out.
        _timeControlCooldown -= (float)dt;
        if (_pendingTimeTick is long pendingTick && _timeControlCooldown <= 0)
        {
            _connection.SendTimeControl(pendingTick, -1f);
            _pendingTimeTick = null;
            _timeControlCooldown = 0.1f;
        }

        _gui.SetCtrlHeld(_keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight));

        // Verification hooks.
        if (_options.DemoGui && _frameCount == 240)
        {
            _gui.Open("inventory");
            _gui.Open("creative_menu");
            int hellIdx = _inventory.CreativeList.IndexOf("hellstone");
            if (hellIdx >= 0) _inventory.HandleSlotClick("creative", hellIdx, 0); // stack on cursor
            _gui.OnMouseMove(_window.FramebufferSize.X * 0.72f, _window.FramebufferSize.Y * 0.68f);
        }
        if (_options.DemoPause && _frameCount == 240)
        {
            _gui.ShowPause();
            _gui.OnMouseMove(_window.FramebufferSize.X * 0.5f, _window.FramebufferSize.Y * 0.42f);
        }
        if (_options.DemoDebug && _frameCount == 240)
        {
            _gui.ShowDebug();
            SetCaptured(false);
        }
        if (_options.DemoDebug && _frameCount == 300)
        {
            // Click the slider at its midpoint (= 12:00) through the real input
            // path, exercising hit-testing, clock math, and the server round trip.
            float sliderMidX = _window.FramebufferSize.X - 12 - 380 + 16 + (380 - 32) / 2f;
            _gui.OnMouseDown(sliderMidX, 12 + 78, 0);
            _gui.OnMouseUp();
        }
        if (_options.DemoDebug && _frameCount == 330)
        {
            // Then click the pause button; the HUD should show "(paused)".
            float centerX = _window.FramebufferSize.X - 12 - 380 + 190;
            _gui.OnMouseDown(centerX, 12 + 116 + 18, 0);
            _gui.OnMouseUp();
        }
        if (_options.DemoEdits && _frameCount == 240)
        {
            var (dx, dy, dz) = AimDirection();
            int bx = (int)Math.Floor(_camera.X + dx * 8);
            int bz = (int)Math.Floor(_camera.Z + dz * 8);
            ushort glow = _data.Blocks.Resolve("glowstone");
            int surface = _generator.SurfaceHeight(bx, bz);
            for (int i = 1; i <= 3; i++)
            {
                _world.SetBlock(bx, surface + i, bz, glow);
                _connection.SendSetBlock(bx, surface + i, bz, glow);
            }
            _world.SetBlock(bx + 2, surface, bz, 0);
            _connection.SendSetBlock(bx + 2, surface, bz, 0);
            Console.WriteLine($"[client] demo edits at ({bx}, {surface + 1}..{surface + 3}, {bz}) + break ({bx + 2}, {surface}, {bz})");
        }
        if (_options.DemoPillar && _frameCount == 240)
        {
            ushort glow = _data.Blocks.Resolve("glowstone");
            int bx = (int)MathF.Floor(_camera.X);
            int bz = (int)MathF.Floor(_camera.Z);
            int by = (int)MathF.Floor(_camera.Y);
            for (int i = 0; i < 12; i++)
            {
                _world.SetBlock(bx, by + i, bz, glow);
                _connection.SendSetBlock(bx, by + i, bz, glow);
            }
            Console.WriteLine($"[client] demo pillar at ({bx}, {by}..{by + 11}, {bz})");
        }
    }

    private void OnRender(double dt)
    {
        var size = _window.FramebufferSize;
        float aspect = size.X / (float)Math.Max(1, size.Y);
        const float fovY = 75f * MathF.PI / 180f;

        // Day/night sky from the world clock; blend toward hell red underground.
        var sky = SkyState.Compute(_clock.WorldTicks, _clock.DayLengthTicks);
        double hellB = _generator.HellBoundary(_camera.X, _camera.Z);
        float hellT = (float)Math.Clamp((hellB + 24 - _camera.Y) / 32, 0, 1);
        (float R, float G, float B) Blend((float R, float G, float B) c, (float R, float G, float B) h, float t)
            => (c.R + (h.R - c.R) * t, c.G + (h.G - c.G) * t, c.B + (h.B - c.B) * t);
        var horizon = Blend(sky.Horizon, HellSky, hellT);
        var zenith = Blend(sky.Zenith, HellSky, hellT);
        float fogNear = FogNear + (12 - FogNear) * hellT;
        float fogFar = FogFar + (64 - FogFar) * hellT;
        // Lighting composite terms (plan 02 M6/M7): a small always-on floor
        // (red-tinted in hell), sky ambient that the per-fragment openness ray
        // gates (caves go dark), and the sun/moon directional light (N·L +
        // shadow rays in the shader). Hell fades the surface terms out.
        var ambientSky = Blend((0.045f, 0.050f, 0.085f), (0.55f, 0.56f, 0.60f), sky.DayAmount);
        ambientSky = (ambientSky.R * (1f - hellT), ambientSky.G * (1f - hellT), ambientSky.B * (1f - hellT));
        var ambientFloor = Blend((0.035f, 0.035f, 0.045f), (0.52f, 0.16f, 0.10f), hellT);
        float dirScale = 0.45f * (1f - hellT);
        var dirColor = (R: sky.DirLightColor.R * dirScale, G: sky.DirLightColor.G * dirScale, B: sky.DirLightColor.B * dirScale);

        // Camera basis for the sky ray reconstruction.
        var (fx, fy, fz) = AimDirection();
        var forward = ((float)fx, (float)fy, (float)fz);
        var right = Normalize(Cross(forward, (0f, 1f, 0f)));
        var up = Cross(right, forward);

        // ---- Scene pass (offscreen): black clear + world only; sky is drawn
        // in the composite pass so mesh cracks stay black instead of sky-blue.
        _postChain.BeginScene();
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _timerWorld.Begin();
        float[] viewProj = Mat4.Multiply(
            Mat4.PerspectiveGl(fovY, aspect, 0.1f, 1000f),
            Mat4.View(_camera.X, _camera.Y, _camera.Z, _camera.Yaw, _camera.Pitch));

        _shader.Use();
        _shader.SetMatrix("uViewProj", viewProj);
        _shader.SetVec3("uCameraPos", _camera.X, _camera.Y, _camera.Z);
        _shader.SetVec3("uFogColor", horizon.R, horizon.G, horizon.B);
        _shader.SetFloat("uFogNear", fogNear);
        _shader.SetFloat("uFogFar", fogFar);
        _shader.SetVec3("uAmbientFloor", ambientFloor.R, ambientFloor.G, ambientFloor.B);
        _shader.SetVec3("uAmbientSky", ambientSky.R, ambientSky.G, ambientSky.B);
        _shader.SetVec3("uDirColor", dirColor.R, dirColor.G, dirColor.B);
        _shader.SetVec3("uDirDir", sky.DirLightDir.X, sky.DirLightDir.Y, sky.DirLightDir.Z);
        _shader.SetInt("uAtlas", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _atlasTexture);
        _shader.SetInt("uOccupancy", 1);
        _occupancy.Bind(TextureUnit.Texture1);
        var (ox, oy, oz) = _occupancy.OriginWorld;
        _shader.SetVec3("uOccupancyOrigin", ox, oy, oz);
        _shader.SetFloat("uOccupancySize", _occupancy.Size);
        _shader.SetInt("uLights", 2);
        _lights.Bind(TextureUnit.Texture2);
        var (lx, ly, lz) = _lights.BuiltOriginWorld;
        _shader.SetVec3("uLightsOrigin", lx, ly, lz);
        _shader.SetInt("uLightClusters", _lights.Clusters);
        _shader.SetInt("uShadowedLightCap", Math.Clamp(_settings.ShadowedLightCap, 0, 8));

        long triangles = 0;
        int draws = 0;
        Frustum.Extract(viewProj, _frustumPlanes);
        const float chunkRadius = 13.86f; // sqrt(3) * 8, chunk bounding sphere
        bool ChunkVisible(int cx, int cy, int cz) => Frustum.SphereVisible(
            _frustumPlanes,
            cx * Constants.ChunkSize + 8, cy * Constants.ChunkSize + 8, cz * Constants.ChunkSize + 8,
            chunkRadius);

        // Solid pass: opaque + alpha-tested cutout, backface culled.
        _shader.SetFloat("uAlphaTest", 0.5f);
        _shader.SetMatrix("uModel", IdentityMat); // chunks use identity; entities override
        _gl.Enable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        foreach (var (cx, cy, cz, mesh) in _world.SolidMeshes())
        {
            if (!ChunkVisible(cx, cy, cz)) continue;
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
            triangles += mesh.IndexCount / 3;
            draws++;
        }

        // Physics entities (plan 03): opaque, same lighting uniforms, own model
        // matrix. Drawn in the opaque phase for correct depth vs translucency.
        draws += _entities.Count;
        _entities.Render(_shader);
        _shader.SetMatrix("uModel", IdentityMat); // restore for the chunk liquid/translucent passes

        // Liquid surfaces (water/lava tops): alpha blend with depth writes.
        // Depth-write keeps each pixel a single blend; adjacent chunks' quads
        // abut on an exact integer edge (no overlap) so there is no seam.
        _shader.SetFloat("uAlphaTest", 0.01f);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(true);
        foreach (var (cx, cy, cz, mesh) in _world.LiquidSurfaceMeshes())
        {
            if (!ChunkVisible(cx, cy, cz)) continue;
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
            triangles += mesh.IndexCount / 3;
            draws++;
        }

        // Translucent pass (water sides, lava): blended, no depth writes.
        _gl.DepthMask(false);
        foreach (var (cx, cy, cz, mesh) in _world.TranslucentMeshes())
        {
            if (!ChunkVisible(cx, cy, cz)) continue;
            _shader.SetVec3("uChunkOrigin", cx * Constants.ChunkSize, cy * Constants.ChunkSize, cz * Constants.ChunkSize);
            mesh.Draw();
            triangles += mesh.IndexCount / 3;
            draws++;
        }
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        // Flat-color pass: remote players, then the block target outline.
        _colorShader.Use();
        _colorShader.SetMatrix("uViewProj", viewProj);
        _gl.Enable(EnableCap.CullFace);
        foreach (var p in _players.All)
        {
            if (!p.Seen) continue;
            _colorShader.SetVec3("uOrigin", p.X - 0.3f, p.Y - 0.9f, p.Z - 0.3f);
            _colorShader.SetVec3("uScale", 0.6f, 1.8f, 0.6f);
            _colorShader.SetFloat4("uColor", p.Color.R, p.Color.G, p.Color.B, 1f);
            _cubeTriangles.Draw();
            draws++;
        }
        // Glue box selection: two corners + live preview wireframe.
        if (_glueCorner1 is { } c1)
        {
            var c2 = _glueCorner2 ?? GluePreviewCorner();
            if (c2 is { } c2v)
            {
                _gl.Disable(EnableCap.CullFace);
                DrawGlueSelectionBox(
                    Math.Min(c1.X, c2v.X), Math.Min(c1.Y, c2v.Y), Math.Min(c1.Z, c2v.Z),
                    Math.Max(c1.X, c2v.X), Math.Max(c1.Y, c2v.Y), Math.Max(c1.Z, c2v.Z));
                draws += 12;
                _gl.Enable(EnableCap.CullFace);
            }
            DrawGlueCornerMarker(c1, 0.95f, 1f, 0.85f);
            draws++;
            if (_glueCorner2 is { } c2set)
            {
                DrawGlueCornerMarker(c2set, 0.55f, 1f, 1f);
                draws++;
            }
        }
        if (_target is AimTarget.World worldTarget)
        {
            _colorShader.SetInt("uUseModel", 0);
            _colorShader.SetVec3("uOrigin", worldTarget.Hit.X - 0.001f, worldTarget.Hit.Y - 0.001f, worldTarget.Hit.Z - 0.001f);
            _colorShader.SetVec3("uScale", 1.002f, 1.002f, 1.002f);
            _colorShader.SetFloat4("uColor", 0.07f, 0.07f, 0.07f, 1f);
            _cubeLines.Draw();
        }
        else if (_target is AimTarget.Entity entityTarget)
        {
            _colorShader.SetInt("uUseModel", 1);
            _colorShader.SetMatrix("uModel", _entities.GetModelMatrix(entityTarget.Hit.EntityId));
            _colorShader.SetVec3("uOrigin", entityTarget.Hit.LocalX - 0.001f, entityTarget.Hit.LocalY - 0.001f, entityTarget.Hit.LocalZ - 0.001f);
            _colorShader.SetVec3("uScale", 1.002f, 1.002f, 1.002f);
            _colorShader.SetFloat4("uColor", 0.07f, 0.07f, 0.07f, 1f);
            _cubeLines.Draw();
            _colorShader.SetInt("uUseModel", 0);
        }
        _timerWorld.End();

        var hellSky = new SkyState(sky.SunDir, zenith, horizon, sky.SunDiscColor, sky.LightColor, sky.DayAmount, sky.MoonVisibility,
            sky.DirLightDir, sky.DirLightColor);
        float tanHalfFov = MathF.Tan(fovY / 2f);
        int surfaceY = _generator.SurfaceHeight((int)MathF.Floor(_camera.X), (int)MathF.Floor(_camera.Z));
        float skyOpen = Math.Clamp((_camera.Y - (surfaceY - 4f)) / 10f, 0f, 1f) * (1f - hellT);

        // ---- Composite: sky only on clear depth when the camera can see it ----
        _timerSky.Begin();
        _postChain.Composite(size.X, size.Y, in hellSky, forward, right, up, tanHalfFov, aspect, skyOpen);
        _timerSky.End();

        // ---- UI overlay (drawn directly to the screen) ----------------------
        _timerUi.Begin();
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _uiBatch.Begin(size.X, size.Y);

        var stats = _world.Stats;
        var held = _inventory.SelectedStack();
        string[] hudLines =
        [
            $"fps {_lastFps}  draws {draws}  tris {triangles}",
            $"online as {_playerName} (#{_connection.PlayerId})  players {_players.Count + 1}  |  {_clock.Describe()}",
            $"pos {_camera.X:F1} {_camera.Y:F1} {_camera.Z:F1}  biome {_generator.BiomeAt(_camera.X, _camera.Y, _camera.Z)}",
            $"chunks {stats.Loaded} loaded, {stats.Rendered} rendered  net {stats.AwaitingNet}  mesh {stats.PendingMesh} ({stats.Workers} workers)  entities {_entities.Count}",
            $"hand {(held is null ? "empty" : _inventory.DisplayNameOf(held.Id))}  |  render distance {StreamingWorld.RenderRadius}",
            $"gpu  sky {_timerSky.Milliseconds:F2}ms  world {_timerWorld.Milliseconds:F2}ms  ui {_timerUi.Milliseconds:F2}ms",
        ];
        for (int i = 0; i < hudLines.Length; i++)
        {
            _font.DrawShadowed(_uiBatch, 8, 8 + i * (_font.LineHeight + 2), hudLines[i]);
        }

        if (_camera.Captured)
        {
            _font.DrawShadowed(_uiBatch, size.X / 2f - _font.Measure("+") / 2, size.Y / 2f - _font.LineHeight / 2, "+");
        }

        // Nametags above remote players (within ~48 blocks, projected to screen).
        foreach (var p in _players.All)
        {
            if (!p.Seen) continue;
            float ddx = p.X - _camera.X, ddy = p.Y - _camera.Y, ddz = p.Z - _camera.Z;
            if (ddx * ddx + ddy * ddy + ddz * ddz > 48 * 48) continue;
            if (ProjectToScreen(viewProj, p.X, p.Y + 1.25f, p.Z, size.X, size.Y) is (float sx, float sy))
            {
                _font.DrawShadowed(_uiBatch, sx - _font.Measure(p.Name) / 2, sy - _font.LineHeight, p.Name);
            }
        }

        _gui.Draw(_uiBatch, _font, _camera.Captured);
        _uiBatch.End();
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _timerUi.End();

        _frameCount++;
        _fpsFrames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 1.0)
        {
            _lastFps = _fpsFrames;
            _window.Title = $"Fable5VoxelSharp - {_lastFps} fps";
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
                $"{draws} draws, {triangles} tris, biome {_generator.BiomeAt(_camera.X, _camera.Y, _camera.Z)}, " +
                $"time {_clock.Describe()}, gpu sky/world/ui {_timerSky.Milliseconds:F2}/{_timerWorld.Milliseconds:F2}/{_timerUi.Milliseconds:F2}ms");
            SaveScreenshot(_options.ScreenshotPath);
            _window.Close();
        }
    }

    private static (float X, float Y, float Z) Cross((float X, float Y, float Z) a, (float X, float Y, float Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (float X, float Y, float Z) Normalize((float X, float Y, float Z) v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? (v.X / len, v.Y / len, v.Z / len) : v;
    }

    private static (float X, float Y)? ProjectToScreen(float[] m, float x, float y, float z, int w, int h)
    {
        float cx = m[0] * x + m[4] * y + m[8] * z + m[12];
        float cy = m[1] * x + m[5] * y + m[9] * z + m[13];
        float cw = m[3] * x + m[7] * y + m[11] * z + m[15];
        if (cw <= 0.1f) return null; // behind the camera
        float sx = (cx / cw * 0.5f + 0.5f) * w;
        float sy = (1f - (cy / cw * 0.5f + 0.5f)) * h;
        if (sx < -100 || sx > w + 100 || sy < -50 || sy > h + 50) return null;
        return (sx, sy);
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
