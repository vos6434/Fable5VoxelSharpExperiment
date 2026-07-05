using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Voxel.Client;

/// <summary>
/// Background meshing: jobs go to worker threads over a channel, results come
/// back on a concurrent queue the main thread drains (GL uploads must happen
/// on the GL thread). Jobs carry copies of chunk data so edits on the main
/// thread can't race a mesh in progress; stale results are dropped by the
/// caller via version comparison.
/// </summary>
public sealed class MesherPool : IDisposable
{
    public readonly record struct Job(
        (int Cx, int Cy, int Cz) Key,
        long Version,
        ushort[] Blocks,
        ushort[]?[] Neighbors);

    public readonly record struct Completed(Job Job, MeshResult Result);

    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentQueue<Completed> _results = new();
    private readonly CancellationTokenSource _cts = new();
    private int _pending;

    public int WorkerCount { get; }

    public int Pending => Volatile.Read(ref _pending);

    public MesherPool(byte[] opaque, ushort[] renderTable, byte[] translucentMask)
    {
        WorkerCount = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        for (int i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(() => WorkerLoop(opaque, renderTable, translucentMask))
            {
                IsBackground = true,
                Name = $"mesher-{i}",
            };
            thread.Start();
        }
    }

    public void Submit(Job job)
    {
        Interlocked.Increment(ref _pending);
        _jobs.Writer.TryWrite(job);
    }

    /// <summary>Drains completed jobs on the calling (GL) thread, up to maxResults per call.</summary>
    public void DrainResults(int maxResults, Action<Completed> apply)
    {
        while (maxResults-- > 0 && _results.TryDequeue(out var completed))
        {
            apply(completed);
        }
    }

    private void WorkerLoop(byte[] opaque, ushort[] renderTable, byte[] translucentMask)
    {
        var reader = _jobs.Reader;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!reader.TryRead(out var job))
                {
                    if (!reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult()) return;
                    continue;
                }
                var result = GreedyMesher.Mesh(job.Blocks, job.Neighbors, opaque, renderTable, translucentMask);
                _results.Enqueue(new Completed(job, result));
                Interlocked.Decrement(ref _pending);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _cts.Cancel();
}
