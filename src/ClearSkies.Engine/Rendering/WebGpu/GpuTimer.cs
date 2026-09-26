using System.Diagnostics;
using ClearSkies.Engine.Gui;
using ImGuiNET;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// GPU time per named pass, from timestamp queries written at the start and end of each timed compute or render
/// pass. Callers ask for a pass's timestamp writes (<see cref="TimeCompute"/>, <see cref="TimeRender"/>) as they
/// begin it; the renderer resolves the frame's queries into its own command buffer (<see cref="Resolve"/>) and
/// they are read back a few frames later, without waiting on the GPU.
///
/// wgpu-native doesn't say how long a timestamp tick is, so it is measured: the GPU clock's ticks between two
/// frames several seconds apart against the CPU clock between them (the queue's latency changes a few ms at most,
/// under 1% over that span). Until then times are shown as if a tick were 1 ns.
/// </summary>
public sealed unsafe class GpuTimer : IDebugUiSystem, IDisposable
{
    private const int MaxQueries = 256;   // two per timed pass
    private const int Ring = 4;           // frames of readbacks in flight

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;
    private readonly QuerySet* _set;
    private readonly GpuBuffer? _resolve;

    private sealed class Readback
    {
        public GpuBuffer Buffer = null!;
        public PfnBufferMapCallback Callback;
        public bool Busy, Mapped;
        public int Count;
        public string[] Names = new string[MaxQueries / 2];
        public int[] Items = new int[MaxQueries / 2];
        public long CpuTicks;             // Stopwatch time the frame was submitted
    }

    private readonly Readback[] _ring = new Readback[Ring];
    private int _nextRing;

    // This frame's passes (query 2k = begin, 2k + 1 = end of pass k).
    private readonly string[] _names = new string[MaxQueries / 2];
    private readonly int[] _items = new int[MaxQueries / 2];
    private int _passes;

    // Results: per name, smoothed ms and passes per frame; and the frame's span, first begin to last end.
    private readonly Dictionary<string, (double ms, double count)> _stats = new();
    private readonly Dictionary<string, (double ticks, int count, long items)> _frameSums = new();
    // Per name, all-time totals of GPU ms and items (bricks) for passes that count them: the cost per item.
    private readonly Dictionary<string, (double ms, long items)> _perItem = new();
    private readonly List<string> _order = new();
    private double _spanMs, _busyMs;

    // Tick length calibration: a reference (GPU ticks, CPU time) pair, replaced once the current one is old.
    private ulong _refTicks;
    private long _refCpu;
    private bool _haveRef;
    private double _nsPerTick = 1.0;
    private bool _calibrated;

    public bool Supported { get; }
    public bool Enabled = true;

    public string DebugName => "GPU timings";

    internal GpuTimer(GpuContext ctx, bool supported)
    {
        _ctx = ctx;
        _api = ctx.Api;
        Supported = supported;
        if (!supported) return;

        var desc = new QuerySetDescriptor { Type = QueryType.Timestamp, Count = MaxQueries };
        _set = _api.DeviceCreateQuerySet(ctx.Device, &desc);
        _resolve = GpuBuffer.Create(ctx, MaxQueries * 8, BufferUsage.QueryResolve | BufferUsage.CopySrc);
        for (int i = 0; i < Ring; i++)
        {
            var r = _ring[i] = new Readback();
            r.Buffer = GpuBuffer.Create(ctx, MaxQueries * 8, BufferUsage.MapRead | BufferUsage.CopyDst);
            r.Callback = PfnBufferMapCallback.From((status, _) =>
            {
                if (status == BufferMapAsyncStatus.Success) r.Mapped = true;
                else r.Busy = false;
            });
        }
    }

    private bool TryAlloc(string name, int items, out uint begin)
    {
        begin = 0;
        if (!Supported || !Enabled || _passes * 2 >= MaxQueries) return false;
        _names[_passes] = name;
        _items[_passes] = items;
        begin = (uint)(_passes * 2);
        _passes++;
        return true;
    }

    /// <summary>Timestamp writes timing a compute pass as <paramref name="name"/>; false when not timing (leave the
    /// descriptor's TimestampWrites null). <paramref name="items"/> (bricks, say), when given, adds a cost per item.</summary>
    internal bool TimeCompute(string name, out ComputePassTimestampWrites writes, int items = 0)
    {
        bool ok = TryAlloc(name, items, out uint b);
        writes = new ComputePassTimestampWrites { QuerySet = _set, BeginningOfPassWriteIndex = b, EndOfPassWriteIndex = b + 1 };
        return ok;
    }

    /// <summary>As <see cref="TimeCompute"/>, for a render pass.</summary>
    internal bool TimeRender(string name, out RenderPassTimestampWrites writes)
    {
        bool ok = TryAlloc(name, 0, out uint b);
        writes = new RenderPassTimestampWrites { QuerySet = _set, BeginningOfPassWriteIndex = b, EndOfPassWriteIndex = b + 1 };
        return ok;
    }

    /// <summary>Records the resolve of this frame's queries (every pass timed since the last resolve, whichever
    /// command buffer it was in, as long as those were submitted before this one) at the end of
    /// <paramref name="enc"/>. Call <see cref="AfterSubmit"/> once it has been submitted.</summary>
    internal void Resolve(CommandEncoder* enc)
    {
        _pending = null;
        if (_passes == 0) return;
        var r = _ring[_nextRing];
        if (!r.Busy)
        {
            uint n = (uint)(_passes * 2);
            _api.CommandEncoderResolveQuerySet(enc, _set, 0, n, _resolve!.Handle, 0);
            _api.CommandEncoderCopyBufferToBuffer(enc, _resolve.Handle, 0, r.Buffer.Handle, 0, n * 8);
            r.Count = _passes;
            Array.Copy(_names, r.Names, _passes);
            Array.Copy(_items, r.Items, _passes);
            _pending = r;
        }
        // (With every readback still in flight this frame's timings are dropped.)
        _passes = 0;
    }

    private Readback? _pending;

    /// <summary>Starts reading back what <see cref="Resolve"/> recorded, and takes in any readbacks that have
    /// arrived. Never waits for the GPU.</summary>
    internal void AfterSubmit()
    {
        if (_pending is { } r)
        {
            r.Busy = true;
            r.Mapped = false;
            r.CpuTicks = Stopwatch.GetTimestamp();
            _api.BufferMapAsync(r.Buffer.Handle, MapMode.Read, 0, (nuint)(r.Count * 16), r.Callback, null);
            _nextRing = (_nextRing + 1) % Ring;
            _pending = null;
        }
        if (!Supported) return;
        _ctx.Poll(false);
        foreach (var rb in _ring)
            if (rb.Busy && rb.Mapped) Read(rb);
    }

    private void Read(Readback r)
    {
        var t = (ulong*)_api.BufferGetConstMappedRange(r.Buffer.Handle, 0, (nuint)(r.Count * 16));
        _frameSums.Clear();
        ulong first = ulong.MaxValue, last = 0;
        double busy = 0;
        for (int k = 0; k < r.Count; k++)
        {
            ulong b = t[2 * k], e = t[2 * k + 1];
            if (e < b || b == 0) continue; // not written, or a clock oddity
            first = System.Math.Min(first, b);
            last = System.Math.Max(last, e);
            var s = _frameSums.GetValueOrDefault(r.Names[k]);
            _frameSums[r.Names[k]] = (s.ticks + (e - b), s.count + 1, s.items + r.Items[k]);
            busy += e - b;
        }
        _api.BufferUnmap(r.Buffer.Handle);
        r.Busy = r.Mapped = false;
        if (first == ulong.MaxValue) return;

        Calibrate(first, r.CpuTicks);

        foreach (var (name, (ticks, count, items)) in _frameSums)
        {
            if (items > 0)
            {
                var pi = _perItem.GetValueOrDefault(name);
                _perItem[name] = (pi.ms + ticks * _nsPerTick * 1e-6, pi.items + items);
            }
            if (!_stats.ContainsKey(name)) _order.Add(name);
            var s = _stats.GetValueOrDefault(name);
            _stats[name] = (Ema(s.ms, ticks * _nsPerTick * 1e-6), Ema(s.count, count));
        }
        // Passes not run this frame decay toward zero.
        foreach (var name in _order)
            if (!_frameSums.ContainsKey(name)) { var s = _stats[name]; _stats[name] = (Ema(s.ms, 0), Ema(s.count, 0)); }
        _spanMs = Ema(_spanMs, (last - first) * _nsPerTick * 1e-6);
        _busyMs = Ema(_busyMs, busy * _nsPerTick * 1e-6);
    }

    private void Calibrate(ulong gpuTicks, long cpuTicks)
    {
        if (!_haveRef || gpuTicks <= _refTicks)
        {
            _refTicks = gpuTicks; _refCpu = cpuTicks; _haveRef = true;
            return;
        }
        double cpuNs = (cpuTicks - _refCpu) * (1e9 / Stopwatch.Frequency);
        if (cpuNs < 3e9) return; // wait for a long enough span
        double ns = cpuNs / (gpuTicks - _refTicks);
        if (ns > 1e-3 && ns < 1e4)
        {
            _nsPerTick = ns;
            _calibrated = true;
        }
        if (cpuNs > 30e9) { _refTicks = gpuTicks; _refCpu = cpuTicks; } // keep the reference fresh
    }

    /// <summary>Average GPU microseconds per item of the passes named <paramref name="name"/> so far.</summary>
    public double MicrosPerItem(string name)
        => _perItem.TryGetValue(name, out var pi) && pi.items > 0 ? pi.ms * 1000.0 / pi.items : 0;

    private static double Ema(double prev, double sample) => prev + 0.05 * (sample - prev);

    public void DrawDebugUi()
    {
        if (!Supported)
        {
            ImGui.TextWrapped("This GPU/backend doesn't support timestamp queries, so GPU time can't be measured.");
            return;
        }
        ImGui.Checkbox("Measure", ref Enabled);
        ImGui.TextDisabled(_calibrated ? $"tick = {_nsPerTick:F3} ns (measured)" : "tick length not measured yet (assuming 1 ns)");
        ImGui.Text($"Timed passes: {_busyMs:F2} ms busy per frame, first start to last end {_spanMs:F2} ms");
        ImGui.Separator();
        var rows = new List<(string name, double ms, double count)>(_order.Count);
        foreach (var name in _order) { var s = _stats[name]; rows.Add((name, s.ms, s.count)); }
        rows.Sort((a, b) => b.ms.CompareTo(a.ms));
        foreach (var (name, ms, count) in rows)
            ImGui.Text($"{ms,7:F2} ms  {name}" + (count > 1.05 ? $"  (x{count:F1})" : "") +
                       (_perItem.TryGetValue(name, out var pi) && pi.items > 0 ? $"  {pi.ms * 1000.0 / pi.items:F2} us/brick" : ""));
        if (ImGui.Button("Reset per-brick averages")) _perItem.Clear();
        ImGui.Separator();
        ImGui.TextWrapped("Only work inside timed passes is counted. Buffer uploads (meshes, chunk data, light lists) are " +
                          "copies outside any pass and aren't included.");
    }

    public void Dispose()
    {
        if (!Supported) return;
        foreach (var r in _ring) r.Buffer.Dispose();
        _resolve?.Dispose();
        _api.QuerySetRelease(_set);
    }
}
