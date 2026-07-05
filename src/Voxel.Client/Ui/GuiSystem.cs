using System.Text.Json;
using Silk.NET.OpenGL;
using StbImageSharp;
using Voxel.Shared;

namespace Voxel.Client.Ui;

/// <summary>
/// Native port of the web GUI: data-driven textured windows from /data/gui
/// (PNG + JSON slot groups), the 10-slot hotbar HUD, the mouse-cursor stack,
/// and the pause menu. Behavior parity with the current web client:
/// E toggles inventory+creative together, Esc closes all (then pause),
/// holding Ctrl reveals per-window title bars for dragging with an R reset
/// button, click brings a window to front, positions persist. Blocks render
/// in slots as pseudo-3D cubes (top + two sides from the block atlas); items
/// render flat icons.
/// </summary>
public sealed class GuiSystem : IDisposable
{
    public const int Scale = 3;
    private const int BarHeight = 24;

    private sealed record SlotGroup(string Role, int X, int Y, int Cols, int Rows, int Cell, int Size);

    private sealed class GuiAsset
    {
        public required string Id;
        public required string Kind;
        public required string? Title;
        public required uint Texture;
        public required int Width;
        public required int Height;
        public required List<SlotGroup> Groups;
    }

    private readonly record struct SlotHit(string Role, int Index, float X, float Y, int SizePx);

    private readonly GL _gl;
    private readonly ClientData _data;
    private readonly PlayerInventory _inventory;
    private readonly Settings _settings;
    private readonly Dictionary<string, GuiAsset> _assets = new();
    private readonly Dictionary<string, uint> _iconTextures = new();
    private readonly List<string> _openOrder = new(); // draw order; last = front
    private readonly string _dataRoot;

    public bool PauseVisible { get; private set; }
    public bool AnyOpen => _openOrder.Count > 0;
    public bool HotbarUnlocked { get; private set; }

    // Pause menu callbacks (wired by Game).
    public Action? OnResume;

    private (string Target, float OffX, float OffY)? _drag; // target = gui id or "hotbar"
    private bool _ctrlHeld;
    private float _mouseX, _mouseY;
    private int _screenW = 1280, _screenH = 720;

    public GuiSystem(GL gl, ClientData data, PlayerInventory inventory, Settings settings, string dataRoot)
    {
        _gl = gl;
        _data = data;
        _inventory = inventory;
        _settings = settings;
        _dataRoot = dataRoot;
        HotbarUnlocked = settings.HotbarUnlocked;

        string guiDir = Path.Combine(dataRoot, "gui");
        foreach (string file in Directory.GetFiles(guiDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string id = root.GetProperty("id").GetString()!;
            string textureName = root.GetProperty("texture").GetString()!;
            var (texture, w, h) = LoadTexture(Path.Combine(guiDir, textureName));
            var groups = new List<SlotGroup>();
            foreach (var g in root.GetProperty("slotGroups").EnumerateArray())
            {
                groups.Add(new SlotGroup(
                    g.GetProperty("role").GetString()!,
                    g.GetProperty("x").GetInt32(), g.GetProperty("y").GetInt32(),
                    g.GetProperty("cols").GetInt32(), g.GetProperty("rows").GetInt32(),
                    g.GetProperty("cell").GetInt32(), g.GetProperty("size").GetInt32()));
            }
            _assets[id] = new GuiAsset
            {
                Id = id,
                Kind = root.GetProperty("kind").GetString()!,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Texture = texture,
                Width = w,
                Height = h,
                Groups = groups,
            };
        }
        Console.WriteLine($"[client] gui: {_assets.Count} definitions loaded");
    }

    private (uint Tex, int W, int H) LoadTexture(string path)
    {
        var image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        unsafe
        {
            fixed (byte* p = image.Data)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                    (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        return (tex, image.Width, image.Height);
    }

    private uint IconTexture(string relPath)
    {
        if (_iconTextures.TryGetValue(relPath, out uint tex)) return tex;
        tex = LoadTexture(Path.Combine(_dataRoot, relPath)).Tex;
        _iconTextures[relPath] = tex;
        return tex;
    }

    // ---- open/close/pause ----------------------------------------------------

    public void ToggleInventoryAndCreative()
    {
        if (CloseAll()) return;
        Open("inventory");
        Open("creative_menu");
    }

    public void Open(string id)
    {
        if (_openOrder.Contains(id) || !_assets.TryGetValue(id, out var asset) || asset.Kind != "window") return;
        if (!_settings.GuiPositions.ContainsKey(id))
        {
            float w = asset.Width * Scale;
            float h = asset.Height * Scale;
            // Prefer a spot that doesn't overlap already-open windows (web parity).
            var pos = ClampToScreen((_screenW - w) / 2, (_screenH - h) / 2, w, h);
            for (int attempt = 0; attempt < 9; attempt++)
            {
                var candidate = ClampToScreen(
                    (_screenW - w) / 2 + (attempt % 3 == 1 ? w + 24 : attempt % 3 == 2 ? -(w + 24) : 0) * ((attempt / 3) + 1) / 2f,
                    (_screenH - h) / 2 + (attempt / 3) * 48, w, h);
                if (!OverlapsOpenWindow(candidate.X, candidate.Y, w, h))
                {
                    pos = candidate;
                    break;
                }
            }
            _settings.GuiPositions[id] = [pos.X, pos.Y];
        }
        _openOrder.Add(id);
    }

    private bool OverlapsOpenWindow(float x, float y, float w, float h)
    {
        foreach (string openId in _openOrder)
        {
            var other = _assets[openId];
            var (ox, oy) = WindowPos(other);
            float ow = other.Width * Scale;
            float oh = other.Height * Scale;
            if (x < ox + ow && x + w > ox && y < oy + oh && y + h > oy) return true;
        }
        return false;
    }

    public bool CloseAll()
    {
        if (_openOrder.Count == 0) return false;
        _openOrder.Clear();
        _drag = null;
        return true;
    }

    public void ShowPause() => PauseVisible = true;

    public void HidePause() => PauseVisible = false;

    public void SetCtrlHeld(bool held) => _ctrlHeld = held;

    public void SetScreenSize(int w, int h)
    {
        _screenW = w;
        _screenH = h;
    }

    public void ResetHotbarPosition()
    {
        _settings.HotbarPos = null;
        _settings.Save();
    }

    // ---- geometry -------------------------------------------------------------

    private (float X, float Y) WindowPos(GuiAsset asset)
    {
        if (_settings.GuiPositions.TryGetValue(asset.Id, out var p) && p.Length == 2)
        {
            return ClampToScreen(p[0], p[1], asset.Width * Scale, asset.Height * Scale);
        }
        return ((_screenW - asset.Width * Scale) / 2f, (_screenH - asset.Height * Scale) / 2f);
    }

    private (float X, float Y) HotbarPos()
    {
        var asset = _assets["hotbar"];
        if (_settings.HotbarPos is { Length: 2 } p)
        {
            return ClampToScreen(p[0], p[1], asset.Width * Scale, asset.Height * Scale);
        }
        return ((_screenW - asset.Width * Scale) / 2f, _screenH - asset.Height * Scale - 8);
    }

    private (float X, float Y) ClampToScreen(float x, float y, float w, float h)
    {
        return (Math.Clamp(x, 0, Math.Max(0, _screenW - w)), Math.Clamp(y, 0, Math.Max(0, _screenH - h)));
    }

    private IEnumerable<SlotHit> SlotsOf(GuiAsset asset, float originX, float originY)
    {
        foreach (var g in asset.Groups)
        {
            for (int row = 0; row < g.Rows; row++)
            {
                for (int col = 0; col < g.Cols; col++)
                {
                    yield return new SlotHit(
                        g.Role, row * g.Cols + col,
                        originX + (g.X + col * g.Cell) * Scale,
                        originY + (g.Y + row * g.Cell) * Scale,
                        g.Size * Scale);
                }
            }
        }
    }

    // ---- input -----------------------------------------------------------------

    public void OnMouseMove(float x, float y)
    {
        _mouseX = x;
        _mouseY = y;
        if (_drag is { } d)
        {
            var (target, offX, offY) = d;
            if (target == "hotbar")
            {
                var asset = _assets["hotbar"];
                var pos = ClampToScreen(x - offX, y - offY, asset.Width * Scale, asset.Height * Scale);
                _settings.HotbarPos = [pos.X, pos.Y];
            }
            else
            {
                var asset = _assets[target];
                var pos = ClampToScreen(x - offX, y - offY, asset.Width * Scale, asset.Height * Scale);
                _settings.GuiPositions[target] = [pos.X, pos.Y];
            }
        }
    }

    public void OnMouseUp()
    {
        if (_drag is not null)
        {
            _drag = null;
            _settings.Save();
        }
    }

    /// <summary>Returns true when the click was consumed by the UI.</summary>
    public bool OnMouseDown(float x, float y, int button)
    {
        if (PauseVisible) return HandlePauseClick(x, y);

        // Windows, topmost first.
        for (int i = _openOrder.Count - 1; i >= 0; i--)
        {
            string id = _openOrder[i];
            var asset = _assets[id];
            var (wx, wy) = WindowPos(asset);
            float w = asset.Width * Scale;
            float h = asset.Height * Scale;

            if (_ctrlHeld && Inside(x, y, wx, wy - BarHeight, w, BarHeight))
            {
                BringToFront(id);
                float resetX = wx + w - BarHeight;
                if (x >= resetX)
                {
                    _settings.GuiPositions.Remove(id);
                    _settings.Save();
                }
                else
                {
                    _drag = (id, x - wx, y - wy);
                }
                return true;
            }

            if (Inside(x, y, wx, wy, w, h))
            {
                BringToFront(id);
                foreach (var slot in SlotsOf(asset, wx, wy))
                {
                    if (Inside(x, y, slot.X, slot.Y, slot.SizePx, slot.SizePx))
                    {
                        if (slot.Role == "creative")
                        {
                            _inventory.HandleSlotClick("creative", CreativeIndex(asset, slot.Index), button);
                        }
                        else
                        {
                            _inventory.HandleSlotClick(slot.Role, slot.Index, button);
                        }
                        return true;
                    }
                }
                return true; // clicks on window background are consumed
            }
        }

        // Hotbar HUD (item moves while a GUI is open; dragging when unlocked).
        var hotbar = _assets["hotbar"];
        var (hx, hy) = HotbarPos();
        if (Inside(x, y, hx, hy, hotbar.Width * Scale, hotbar.Height * Scale))
        {
            foreach (var slot in SlotsOf(hotbar, hx, hy))
            {
                if (Inside(x, y, slot.X, slot.Y, slot.SizePx, slot.SizePx))
                {
                    if (AnyOpen)
                    {
                        _inventory.HandleSlotClick("hotbar", slot.Index, button);
                    }
                    else
                    {
                        _inventory.Selected = slot.Index;
                        _inventory.Save();
                    }
                    return true;
                }
            }
            if (HotbarUnlocked)
            {
                _drag = ("hotbar", x - hx, y - hy);
                return true;
            }
            return AnyOpen;
        }

        return false;
    }

    private static int CreativeIndex(GuiAsset asset, int slotIndex) => slotIndex;

    private void BringToFront(string id)
    {
        _openOrder.Remove(id);
        _openOrder.Add(id);
    }

    private static bool Inside(float px, float py, float x, float y, float w, float h) =>
        px >= x && px < x + w && py >= y && py < y + h;

    // ---- pause menu -------------------------------------------------------------

    private (float X, float Y, float W, float H) PausePanel()
    {
        const float w = 340, h = 220;
        return ((_screenW - w) / 2f, (_screenH - h) / 2f, w, h);
    }

    private (float X, float Y, float W, float H) PauseButton(int row)
    {
        var (px, py, pw, _) = PausePanel();
        return (px + 24, py + 56 + row * 52, pw - 48, 40);
    }

    private bool HandlePauseClick(float x, float y)
    {
        var resume = PauseButton(0);
        var unlock = PauseButton(1);
        var reset = PauseButton(2);
        if (Inside(x, y, resume.X, resume.Y, resume.W, resume.H))
        {
            HidePause();
            OnResume?.Invoke();
        }
        else if (Inside(x, y, unlock.X, unlock.Y, unlock.W, unlock.H))
        {
            HotbarUnlocked = !HotbarUnlocked;
            _settings.HotbarUnlocked = HotbarUnlocked;
            _settings.Save();
        }
        else if (Inside(x, y, reset.X, reset.Y, reset.W, reset.H))
        {
            ResetHotbarPosition();
        }
        return true; // the pause menu consumes all clicks
    }

    // ---- drawing ------------------------------------------------------------------

    public void Draw(UiBatch batch, UiFont font, bool mouseCaptured)
    {
        DrawHotbar(batch, font);

        foreach (string id in _openOrder)
        {
            DrawWindow(batch, font, _assets[id]);
        }

        if (_inventory.Cursor is not null && !mouseCaptured)
        {
            DrawStack(batch, font, _mouseX - 24, _mouseY - 24, 48, _inventory.Cursor);
        }

        if (PauseVisible) DrawPause(batch, font);
    }

    private void DrawHotbar(UiBatch batch, UiFont font)
    {
        var asset = _assets["hotbar"];
        var (hx, hy) = HotbarPos();
        batch.TexturedRect(asset.Texture, hx, hy, asset.Width * Scale, asset.Height * Scale);
        if (HotbarUnlocked)
        {
            batch.BorderRect(hx - 2, hy - 2, asset.Width * Scale + 4, asset.Height * Scale + 4, 2, 1f, 0.88f, 0.4f, 0.9f);
        }
        foreach (var slot in SlotsOf(asset, hx, hy))
        {
            var stack = _inventory.Hotbar.Slots[slot.Index];
            if (stack is not null) DrawStack(batch, font, slot.X, slot.Y, slot.SizePx, stack);
            if (slot.Index == _inventory.Selected)
            {
                batch.BorderRect(slot.X - 3, slot.Y - 3, slot.SizePx + 6, slot.SizePx + 6, 3, 1f, 0.88f, 0.4f, 1f);
            }
        }
    }

    private void DrawWindow(UiBatch batch, UiFont font, GuiAsset asset)
    {
        var (wx, wy) = WindowPos(asset);
        float w = asset.Width * Scale;
        float h = asset.Height * Scale;
        batch.TexturedRect(asset.Texture, wx, wy, w, h);

        foreach (var slot in SlotsOf(asset, wx, wy))
        {
            ItemStack? stack = slot.Role switch
            {
                "creative" => slot.Index < _inventory.CreativeList.Count
                    ? new ItemStack(_inventory.CreativeList[slot.Index], 0)
                    : null,
                _ => _inventory.ContainerFor(slot.Role)?.Slots.ElementAtOrDefault(slot.Index),
            };
            if (stack is not null) DrawStack(batch, font, slot.X, slot.Y, slot.SizePx, stack);
            if (Inside(_mouseX, _mouseY, slot.X, slot.Y, slot.SizePx, slot.SizePx) && !PauseVisible)
            {
                batch.SolidRect(slot.X, slot.Y, slot.SizePx, slot.SizePx, 1, 1, 1, 0.35f);
            }
        }

        if (_ctrlHeld)
        {
            batch.SolidRect(wx, wy - BarHeight, w, BarHeight - 2, 0.09f, 0.09f, 0.09f, 0.94f);
            batch.BorderRect(wx, wy - BarHeight, w, BarHeight - 2, 2, 1f, 0.88f, 0.4f, 1f);
            font.DrawShadowed(batch, wx + 8, wy - BarHeight + 3, asset.Title ?? asset.Id);
            float resetX = wx + w - BarHeight;
            batch.SolidRect(resetX, wy - BarHeight + 2, BarHeight - 4, BarHeight - 6, 0.37f, 0.37f, 0.37f, 1f);
            font.DrawShadowed(batch, resetX + 6, wy - BarHeight + 3, "R");
        }
    }

    private void DrawStack(UiBatch batch, UiFont font, float x, float y, int sizePx, ItemStack stack)
    {
        var block = _data.Blocks.ById(stack.Id);
        if (block is not null)
        {
            DrawBlockCube(batch, x, y, sizePx, block);
        }
        else if (_data.Items.ById(stack.Id) is { } item)
        {
            batch.TexturedRect(IconTexture(Path.Combine("items", item.Icon)), x, y, sizePx, sizePx);
        }
        if (stack.Count > 1)
        {
            string text = stack.Count.ToString();
            font.DrawShadowed(batch, x + sizePx - font.Measure(text) - 1, y + sizePx - font.LineHeight + 2, text);
        }
    }

    /// <summary>Pseudo-3D block icon: isometric top + left + right faces from the block atlas.</summary>
    private void DrawBlockCube(UiBatch batch, float x, float y, int sizePx, BlockDefinition block)
    {
        float s = sizePx * 0.82f;
        float cx = x + sizePx / 2f;
        float cy = y + sizePx / 2f;
        int topLayer = _data.RenderTable[block.NumericId * 6 + (int)FaceDir.Py];
        int sideLayer = _data.RenderTable[block.NumericId * 6 + (int)FaceDir.Pz];

        // Top face (diamond).
        batch.AtlasQuad(
            [cx, cx + s / 2, cx, cx - s / 2],
            [cy - s / 2, cy - s / 4, cy, cy - s / 4],
            [0, 1, 1, 0], [1, 1, 0, 0],
            topLayer, 1.0f);
        // Left face.
        batch.AtlasQuad(
            [cx - s / 2, cx, cx, cx - s / 2],
            [cy - s / 4, cy, cy + s / 2, cy + s / 4],
            [0, 1, 1, 0], [1, 1, 0, 0],
            sideLayer, 0.6f);
        // Right face.
        batch.AtlasQuad(
            [cx, cx + s / 2, cx + s / 2, cx],
            [cy, cy - s / 4, cy + s / 4, cy + s / 2],
            [0, 1, 1, 0], [1, 1, 0, 0],
            sideLayer, 0.8f);
    }

    private void DrawPause(UiBatch batch, UiFont font)
    {
        batch.SolidRect(0, 0, _screenW, _screenH, 0, 0, 0, 0.45f);
        var (px, py, pw, ph) = PausePanel();
        batch.SolidRect(px, py, pw, ph, 0.08f, 0.08f, 0.08f, 0.94f);
        batch.BorderRect(px, py, pw, ph, 2, 0.35f, 0.35f, 0.35f, 1f);
        string title = "Paused";
        font.DrawShadowed(batch, px + (pw - font.Measure(title)) / 2, py + 16, title);

        DrawPauseButton(batch, font, 0, "Resume", false);
        DrawPauseButton(batch, font, 1, $"[{(HotbarUnlocked ? "x" : " ")}] Unlock hotbar dragging", false);
        DrawPauseButton(batch, font, 2, "Reset hotbar position", false);
    }

    private void DrawPauseButton(UiBatch batch, UiFont font, int row, string label, bool _)
    {
        var (bx, by, bw, bh) = PauseButton(row);
        bool hover = Inside(_mouseX, _mouseY, bx, by, bw, bh);
        batch.SolidRect(bx, by, bw, bh, hover ? 0.5f : 0.42f, hover ? 0.5f : 0.42f, hover ? 0.5f : 0.42f, 1f);
        batch.BorderRect(bx, by, bw, bh, 2, 0.62f, 0.62f, 0.62f, 1f);
        font.DrawShadowed(batch, bx + (bw - font.Measure(label)) / 2, by + (bh - font.LineHeight) / 2, label);
    }

    public void Dispose()
    {
        foreach (var asset in _assets.Values) _gl.DeleteTexture(asset.Texture);
        foreach (uint tex in _iconTextures.Values) _gl.DeleteTexture(tex);
    }
}
