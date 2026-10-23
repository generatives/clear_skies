namespace ClearSkies.Engine.Core;

/// <summary>
/// The engine's worker threads, for chunk loading, meshing, colliders and clouds. Unlike the .NET thread pool, which
/// runs at least a thread per core (and adds more when work backs up), these are fewer than the cores and run below
/// normal priority, so a burst of streaming work can't take the CPU from the game's own thread: when it did, whatever
/// step the game thread was in stalled for an OS time slice, a hitch of 10-30 ms.
///
/// Three lanes, taken in order: <see cref="Soon"/> for work the player waits on (meshes, colliders); each worker's own
/// (<see cref="Queue(int, Action)"/>), for work that's cheaper where related work ran before (chunks over the same
/// ground share a worker, and so its caches); then shared bulk work (<see cref="Queue(Action)"/>: clouds).
/// </summary>
public static class BackgroundWork
{
    /// <summary>Worker threads: the cores less two, one for the game thread and one for the graphics driver and the
    /// rest of the system; at least one.</summary>
    public static readonly int Workers = System.Math.Max(1, Environment.ProcessorCount - 2);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _soon = new(), _bulk = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action>[] _own =
        Enumerable.Range(0, Workers).Select(_ => new System.Collections.Concurrent.ConcurrentQueue<Action>()).ToArray();
    private static readonly AutoResetEvent[] _wake = Enumerable.Range(0, Workers).Select(_ => new AutoResetEvent(false)).ToArray();
    private static readonly int[] _idle = new int[Workers];
    private static volatile bool _stopping;
    private static int _running;

    static BackgroundWork()
    {
        for (int i = 0; i < Workers; i++)
        {
            int worker = i;
            new Thread(() => Run(worker)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Background work {i}" }.Start();
        }
    }

    /// <summary>Queues bulk work, for any worker.</summary>
    public static void Queue(Action work)
    {
        _bulk.Enqueue(work);
        WakeIdle();
    }

    /// <summary>Queues bulk work for worker <paramref name="worker"/> (wrapped to <see cref="Workers"/>) only.</summary>
    public static void Queue(int worker, Action work)
    {
        worker = (int)((uint)worker % (uint)Workers);
        _own[worker].Enqueue(work);
        _wake[worker].Set();
    }

    /// <summary>Queues work that goes before all bulk work still waiting.</summary>
    public static void Soon(Action work)
    {
        _soon.Enqueue(work);
        WakeIdle();
    }

    /// <summary>Work queued and not started yet, for the debug panels.</summary>
    public static int Waiting => _soon.Count + _bulk.Count + _own.Sum(q => q.Count);

    /// <summary>Drops the work still waiting and waits (up to <paramref name="timeout"/>) for what is running to
    /// finish, so shutting down doesn't free what a job is still using (physics buffers, GPU objects).</summary>
    public static void Stop(TimeSpan timeout)
    {
        _stopping = true;
        while (_soon.TryDequeue(out _) || _bulk.TryDequeue(out _)) { }
        foreach (var own in _own) while (own.TryDequeue(out _)) { }
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _running) > 0 && DateTime.UtcNow < until) Thread.Sleep(1);
    }

    // Wakes the workers waiting for work: shared work can go to any of them. (A busy one looks again when it's done.)
    private static void WakeIdle()
    {
        for (int i = 0; i < Workers; i++)
            if (Volatile.Read(ref _idle[i]) != 0) _wake[i].Set();
    }

    private static bool TakeWork(int worker, out Action work) =>
        _soon.TryDequeue(out work!) || _own[worker].TryDequeue(out work!) || _bulk.TryDequeue(out work!);

    private static void Run(int worker)
    {
        while (true)
        {
            // Marked idle before looking, so work queued meanwhile either is seen or wakes it.
            Interlocked.Exchange(ref _idle[worker], 1);
            if (_stopping || !TakeWork(worker, out var work))
            {
                _wake[worker].WaitOne();
                continue;
            }
            Volatile.Write(ref _idle[worker], 0);
            Interlocked.Increment(ref _running);
            try { work(); }
            catch (Exception ex) { Console.WriteLine($"[background] unhandled: {ex}"); }
            finally { Interlocked.Decrement(ref _running); }
        }
    }
}
