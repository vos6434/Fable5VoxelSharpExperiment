using Silk.NET.OpenGL;

namespace Voxel.Client.Gl;

/// <summary>
/// Double-buffered GL timer query for one named GPU pass. Reads last frame's
/// result to avoid stalling the pipeline; exposes a smoothed millisecond
/// figure for the HUD. The perf gate in plan 02 M3 relies on these being real
/// measurements, not CPU-side guesses.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    private readonly GL _gl;
    private readonly uint[] _queries = new uint[2];
    private int _frame;
    private bool _primed;

    public string Name { get; }
    public double Milliseconds { get; private set; }

    public GpuTimer(GL gl, string name)
    {
        _gl = gl;
        Name = name;
        _gl.GenQueries(_queries);
    }

    public void Begin()
    {
        _gl.BeginQuery(QueryTarget.TimeElapsed, _queries[_frame]);
    }

    public void End()
    {
        _gl.EndQuery(QueryTarget.TimeElapsed);

        // Read the *other* buffer (last frame) — its result is ready by now.
        if (_primed)
        {
            uint other = _queries[_frame ^ 1];
            _gl.GetQueryObject(other, QueryObjectParameterName.Result, out ulong nanos);
            double ms = nanos / 1_000_000.0;
            Milliseconds = Milliseconds * 0.9 + ms * 0.1;
        }
        _frame ^= 1;
        if (_frame == 0) _primed = true;
    }

    public void Dispose() => _gl.DeleteQueries(_queries);
}
