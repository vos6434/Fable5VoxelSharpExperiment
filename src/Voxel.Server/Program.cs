using Voxel.Server;
using Voxel.Shared;

// ---- Repo root (contains /data and /worlds) --------------------------------
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Fable5VoxelSharp.slnx")) ||
            Directory.Exists(Path.Combine(dir.FullName, "data")))
        {
            return dir.FullName;
        }
        dir = dir.Parent!;
    }
    throw new InvalidOperationException("repo root with /data not found");
}

string repoRoot = FindRepoRoot();
var (blocks, items) = DataLoader.LoadRegistries(Path.Combine(repoRoot, "data"));
Console.WriteLine($"[server] loaded {blocks.Count - 1} blocks, {items.Count} items from /data");

var generator = new WorldGen(WorldGen.DefaultSeed, blocks);
string worldPath = Path.Combine(repoRoot, "worlds", "main.db");

// ---- Persistence round-trip self-test (same idea as the web server) --------
(int, int, int)[] testCoords = [(0, 0, 0), (-3, 2, 5), (12, -4, -7)];
using (var probe = new WorldStore(worldPath, generator, blocks))
{
    foreach (var (cx, cy, cz) in testCoords) probe.Load(cx, cy, cz);
}
var store = new WorldStore(worldPath, generator, blocks);
bool roundTripOk = testCoords.All(c =>
{
    var loaded = store.Load(c.Item1, c.Item2, c.Item3).Blocks;
    var fresh = generator.Generate(c.Item1, c.Item2, c.Item3).Chunk.Blocks;
    return loaded.AsSpan().SequenceEqual(fresh);
});
Console.WriteLine(
    $"[server] persistence round-trip {(roundTripOk ? "OK" : "FAILED")} - " +
    $"world {Path.GetFileName(worldPath)}, seed {generator.Seed}, {store.ChunkCount} chunks on disk");
if (!roundTripOk) throw new InvalidOperationException("persistence self-test failed");

// ---- World clock (plan 01: 20 TPS ticks + timescale) ------------------------
var clock = new WorldClock();
long savedWorldTime = long.TryParse(store.GetMeta("worldTime"), out long t0) ? t0 : 0;

// ---- WebSocket game server --------------------------------------------------
var gameServer = new GameServer(store, blocks, generator, clock);
clock.Start(savedWorldTime);
Console.WriteLine($"[server] world clock started at tick {savedWorldTime} ({Constants.TicksPerSecond} TPS)");

// ---- Console commands: tick rate <x> | tick pause | tick resume | tick step [n] | tick status
_ = Task.Run(() =>
{
    while (Console.ReadLine() is { } line)
    {
        string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;

        // spawn [x y z] — drop a debug physics box (plan 03 M1).
        if (parts[0] == "spawn")
        {
            var spawn = generator.FindSpawn();
            float sx = spawn.X, sy = WorldGen.SeaLevel + 20, sz = spawn.Z;
            if (parts.Length >= 4 &&
                float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float px) &&
                float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float py) &&
                float.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out float pz))
            {
                (sx, sy, sz) = (px, py, pz);
            }
            gameServer.RequestSpawnDebugBox(new System.Numerics.Vector3(sx, sy, sz), blocks.Resolve("stone"));
            continue;
        }

        if (parts[0] != "tick")
        {
            Console.WriteLine("[server] commands: spawn [x y z] | tick rate <x> | tick pause | tick resume | tick step [n] | tick status");
            continue;
        }
        switch (parts.ElementAtOrDefault(1))
        {
            case "rate" when parts.Length >= 3 &&
                             float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float scale):
                clock.SetTimescale(scale);
                Console.WriteLine($"[server] timescale set to {clock.Timescale}");
                break;
            case "pause":
                clock.Pause();
                Console.WriteLine("[server] time paused");
                break;
            case "resume":
                clock.Resume();
                Console.WriteLine($"[server] time resumed at x{clock.Timescale}");
                break;
            case "step":
                int n = parts.Length >= 3 && int.TryParse(parts[2], out int steps) ? steps : 1;
                clock.Step(n);
                Console.WriteLine($"[server] stepping {n} tick(s)");
                break;
            case "status":
                Console.WriteLine($"[server] tick {clock.WorldTick}, timescale x{clock.Timescale}{(clock.Paused ? " (paused)" : "")}, day {clock.WorldTick / Constants.DayLengthTicks}");
                break;
            default:
                Console.WriteLine("[server] usage: tick rate <x> | tick pause | tick resume | tick step [n] | tick status");
                break;
        }
    }
    Console.WriteLine("[server] console input closed");
});

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{Protocol.Port}");
var app = builder.Build();

app.UseWebSockets();
app.Map("/", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Fable5Voxel game server: WebSocket connections only.");
        return;
    }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await gameServer.HandleSocket(ws, context.RequestAborted);
});

_ = Task.Run(async () =>
{
    long lastTick = clock.WorldTick;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
    while (await timer.WaitForNextTickAsync())
    {
        long nowTick = clock.WorldTick;
        double tps = (nowTick - lastTick) / 60.0;
        lastTick = nowTick;
        Console.WriteLine(
            $"[server] up {Environment.TickCount64 / 1000}s - {gameServer.PlayerCount} players, " +
            $"{store.ChunkCount} chunks on disk, tick {nowTick} ({tps:F1} TPS, x{clock.Timescale})");
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    clock.Dispose();
    store.SetMeta("worldTime", clock.WorldTick.ToString());
    store.Dispose(); // flush WAL cleanly on shutdown
    Console.WriteLine($"[server] world store closed at tick {clock.WorldTick}");
});

Console.WriteLine($"[server] game server listening on ws://0.0.0.0:{Protocol.Port}");
app.Run();
