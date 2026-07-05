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

// ---- WebSocket game server --------------------------------------------------
var gameServer = new GameServer(store, blocks, generator);

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
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
    while (await timer.WaitForNextTickAsync())
    {
        Console.WriteLine($"[server] up {Environment.TickCount64 / 1000}s - {gameServer.PlayerCount} players, {store.ChunkCount} chunks on disk");
    }
});

Console.WriteLine($"[server] game server listening on ws://0.0.0.0:{Protocol.Port}");
app.Run();
