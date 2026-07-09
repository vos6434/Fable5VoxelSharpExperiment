using System.Globalization;
using Voxel.Client;
using Voxel.Server;
using Voxel.Shared;

// Flags:
//   --server ws://host:port     join a game server; without it the client
//                               plays offline, hosting worlds/main.db
//                               in-process on an ephemeral loopback port
//   --screenshot <path>         save a PNG after --frames frames, then exit
//   --frames N                  frames before the screenshot (default 90)
//   --pos x y z                 start position override (verification teleports)
//   --look yaw pitch            start look angles (radians)
string? screenshotPath = null;
int screenshotFrames = 90;
Uri? server = null;
(double, double, double)? startPos = null;
(float, float)? startLook = null;
bool demoEdits = false;
bool demoPillar = false;
bool demoGui = false;
bool demoPause = false;
bool demoDebug = false;
long? forceTime = null;

double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server": server = new Uri(args[++i]); break;
        case "--screenshot": screenshotPath = args[++i]; break;
        case "--frames": screenshotFrames = int.Parse(args[++i]); break;
        case "--pos": startPos = (D(args[++i]), D(args[++i]), D(args[++i])); break;
        case "--look": startLook = (F(args[++i]), F(args[++i])); break;
        case "--demo-edits": demoEdits = true; break;
        case "--demo-pillar": demoPillar = true; break;
        case "--demo-gui": demoGui = true; break;
        case "--demo-pause": demoPause = true; break;
        case "--demo-debug": demoDebug = true; break;
        case "--time": forceTime = long.Parse(args[++i]); break;
        default: throw new ArgumentException($"unknown argument {args[i]}");
    }
}

string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "data")) &&
            Directory.Exists(Path.Combine(dir.FullName, "shaders")))
        {
            return dir.FullName;
        }
        dir = dir.Parent!;
    }
    throw new InvalidOperationException("repo root with /data and /shaders not found");
}

string repoRoot = FindRepoRoot();

// Offline (the default): host the world in-process — same authoritative
// server, ephemeral loopback port — and connect to ourselves.
ServerHost? host = null;
if (server is null)
{
    Console.WriteLine("[client] offline mode - hosting the world locally (use --server to join a server)");
    try
    {
        host = ServerHost.StartAsync(repoRoot, Path.Combine(repoRoot, "worlds", "main.db"), "http://127.0.0.1:0")
            .GetAwaiter().GetResult();
    }
    catch (WorldLockedException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  {ex.Message}.");
        Console.Error.WriteLine("  A server is probably running on this world - join it instead:");
        Console.Error.WriteLine($"    dotnet run --project src/Voxel.Client -- --server ws://localhost:{Protocol.Port}");
        Environment.Exit(1);
    }
    server = host!.Uri;
}

try
{
    new Game(new GameOptions
    {
        RepoRoot = repoRoot,
        Server = server,
        ScreenshotPath = screenshotPath,
        ScreenshotAfterFrames = screenshotFrames,
        StartPosition = startPos,
        StartLook = startLook,
        DemoEdits = demoEdits,
        DemoPillar = demoPillar,
        DemoGui = demoGui,
        DemoPause = demoPause,
        DemoDebug = demoDebug,
        ForceTimeTicks = forceTime,
    }).Run();
}
catch (ClientConnectException ex)
{
    // The game server is unreachable or rejected us. Fail with a clear,
    // actionable message instead of an unhandled-exception stack dump.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  Could not connect to the game server at {server}.");
    Console.Error.WriteLine($"  Reason: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Check the address and that the server is running, or run without");
    Console.Error.WriteLine("  --server to play offline.");
    Environment.Exit(1);
}
finally
{
    // Flush the offline world to disk after the window closes.
    host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
