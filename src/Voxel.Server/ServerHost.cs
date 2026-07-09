using Voxel.Shared;

namespace Voxel.Server;

/// <summary>
/// Reusable server bootstrap: registries, world store, clock, game server,
/// and the Kestrel WebSocket endpoint. The dedicated server (Program.cs)
/// wraps it with a console command loop and stats; the client embeds it on
/// an ephemeral loopback port for offline play (integrated server).
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public BlockRegistry Blocks { get; }
    public WorldGen Generator { get; }
    public WorldStore Store { get; }
    public WorldClock Clock { get; }
    public GameServer Game { get; }

    /// <summary>ws:// endpoint to connect to, with the actual port after binding.</summary>
    public Uri Uri { get; }

    private ServerHost(WebApplication app, BlockRegistry blocks, WorldGen generator,
        WorldStore store, WorldClock clock, GameServer game, Uri uri)
    {
        _app = app;
        Blocks = blocks;
        Generator = generator;
        Store = store;
        Clock = clock;
        Game = game;
        Uri = uri;
    }

    /// <summary>
    /// Opens the world and starts listening. <paramref name="listenUrl"/> picks
    /// the audience: "http://0.0.0.0:8081" (dedicated) or "http://127.0.0.1:0"
    /// (embedded, ephemeral port). Throws <see cref="WorldLockedException"/> if
    /// another server process has the world open.
    /// </summary>
    public static async Task<ServerHost> StartAsync(string repoRoot, string worldPath, string listenUrl)
    {
        var (blocks, _) = DataLoader.LoadRegistries(Path.Combine(repoRoot, "data"));
        var generator = new WorldGen(WorldGen.DefaultSeed, blocks);
        var store = new WorldStore(worldPath, generator, blocks);
        var clock = new WorldClock();
        long savedWorldTime = long.TryParse(store.GetMeta("worldTime"), out long t0) ? t0 : 0;
        var game = new GameServer(store, blocks, generator, clock);
        clock.Start(savedWorldTime);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(listenUrl);
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
            await game.HandleSocket(ws, context.RequestAborted);
        });

        try
        {
            await app.StartAsync();
        }
        catch
        {
            clock.Dispose();
            store.Dispose();
            await app.DisposeAsync();
            throw;
        }

        var bound = new Uri(app.Urls.First());
        var uri = new Uri($"ws://{bound.Host}:{bound.Port}");
        Console.WriteLine(
            $"[server] world {Path.GetFileName(worldPath)}, seed {generator.Seed}, " +
            $"clock at tick {savedWorldTime} ({Constants.TicksPerSecond} TPS) - listening on {uri}");
        return new ServerHost(app, blocks, generator, store, clock, game, uri);
    }

    /// <summary>Completes when shutdown is triggered (Ctrl-C on the dedicated server).</summary>
    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>Stops accepting clients, then flushes the world to disk.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(); // aborts live sockets via RequestAborted
        Game.PersistAllEntities();
        Clock.Dispose();
        Store.SetMeta("worldTime", Clock.WorldTick.ToString());
        Store.Dispose(); // flush WAL cleanly
        await _app.DisposeAsync();
        Console.WriteLine($"[server] world store closed at tick {Clock.WorldTick}");
    }
}
