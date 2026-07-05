using System.Globalization;
using Voxel.Client;
using Voxel.Shared;

// Flags:
//   --server ws://host:port     game server (default ws://localhost:8081)
//   --screenshot <path>         save a PNG after --frames frames, then exit
//   --frames N                  frames before the screenshot (default 90)
//   --pos x y z                 start position override (verification teleports)
//   --look yaw pitch            start look angles (radians)
string? screenshotPath = null;
int screenshotFrames = 90;
Uri server = new($"ws://localhost:{Protocol.Port}");
(double, double, double)? startPos = null;
(float, float)? startLook = null;

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

new Game(new GameOptions
{
    RepoRoot = FindRepoRoot(),
    Server = server,
    ScreenshotPath = screenshotPath,
    ScreenshotAfterFrames = screenshotFrames,
    StartPosition = startPos,
    StartLook = startLook,
}).Run();
