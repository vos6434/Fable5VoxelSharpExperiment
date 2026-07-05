using Voxel.Client;

// --screenshot <path> [--frames N]: render N frames, save a PNG, exit.
// Used for automated verification of the native renderer.
string? screenshotPath = null;
int screenshotFrames = 90;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--screenshot" && i + 1 < args.Length) screenshotPath = args[++i];
    else if (args[i] == "--frames" && i + 1 < args.Length) screenshotFrames = int.Parse(args[++i]);
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

new Game(FindRepoRoot(), screenshotPath, screenshotFrames).Run();
