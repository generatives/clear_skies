namespace ClearSkies.Engine.Core;

/// <summary>
/// The engine's worker threads, for chunk loading, meshing, colliders and clouds. Unlike the .NET thread pool, which
/// runs at least a thread per core (and adds more when work backs up), these are fewer than the cores and run below
/// normal priority, so a burst of streaming work can't take the CPU from the game's own thread: when it did, whatever
/// step the game thread was in stalled for an OS time slice, a hitch of 10-30 ms.
///
/// Two lanes: <see cref="Soon"/> for work the player waits on (meshes, colliders) is always taken before
/// <see cref="Queue"/>'s bulk work (loading and generating chunks, clouds).
/// </summary>
public static class BackgroundWork
{
    /// <summary>Worker threads: the cores less two, one for the game thread and one for the graphics driver and the
    /// rest of the system; at least one.</summary>
    public static readonly int Workers = System.Math.Max(1, Environment.ProcessorCount - 2);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _soon = new(), _bulk = new();
    private static readonly SemaphoreSlim _ready = new(0);

    static BackgroundWork()
    {
        for (int i = 0; i < Workers; i++)
            new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Background work {i}" }.Start();
    }

    /// <summary>Queues bulk work.</summary>
    public static void Queue(Action work)
    {
        _bulk.Enqueue(work);
        _ready.Release();
    }

    /// <summary>Queues work that goes before all bulk work still waiting.</summary>
    public static void Soon(Action work)
    {
        _soon.Enqueue(work);
        _ready.Release();
    }

    /// <summary>Work queued and not started yet, for the debug panels.</summary>
    public static int Waiting => _soon.Count + _bulk.Count;

    private static void Run()
    {
        while (true)
        {
            _ready.Wait();
            if (!_soon.TryDequeue(out var work) && !_bulk.TryDequeue(out work)) continue;
            try { work(); }
            catch (Exception ex) { Console.WriteLine($"[background] unhandled: {ex}"); }
        }
    }
}
