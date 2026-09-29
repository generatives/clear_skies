using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Every static-world chunk mesh in one buffer, drawn with one indirect draw: the GPU picks what to draw.
///
/// A chunk's quads (<see cref="ChunkQuad"/>s) sit in a run of consecutive pages of <see cref="PageQuads"/> in the quad
/// buffer (see <see cref="PageAllocator"/>), and the chunk table holds each chunk's position, grid and run. Each frame,
/// before the render pass, a compute pass tests every chunk against the view, sorts the visible ones into distance
/// bands (nearest first, so the depth test rejects most hidden fragments before the costly lighting shader runs on
/// them), lists their pages and writes the draw's instance count; the draw then runs one instance per listed page
/// (vs_world). Drawing one chunk at a time cost about a microsecond of CPU per chunk, 5-8 ms a frame at a long view
/// distance.
///
/// A chunk that doesn't fit (the buffer is at its largest) isn't pooled: <see cref="Add"/> returns -1 and it is drawn
/// on its own, as ships' chunks are.
/// </summary>
public sealed unsafe class WorldMeshPool : IDisposable
{
    public const int PageQuads = 64;
    public const int PageBytes = PageQuads * (int)ChunkQuad.SizeBytes;
    private const int Bands = 1024; // at 4000 blocks' range, about a block apart nearby and 8 at the far end
    private const int SlotInts = 8; // chunk table entry: x, y, z, grid, first page, pages, quads, flags
    private const int Group = 64;   // compute workgroup size

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;
    private readonly int _maxPages;

    private readonly PageAllocator _pages;
    private GpuBuffer _quads;
    private GpuBuffer _visible;     // vec2<u32> (page, slot) per listed page
    private GpuBuffer _chunks;      // SlotInts per slot
    private GpuBuffer _visChunks;   // vec4<u32> (slot, band, offset in band, -) per visible chunk
    private readonly GpuBuffer _counters; // per band: page count, then start; then the visible chunk count
    private readonly GpuBuffer _args;     // drawIndexedIndirect (5 u32), then drawIndirect (4 u32) for the wireframe
    private readonly GpuBuffer _cull;     // CullUniform

    private int[] _table = new int[InitialSlots * SlotInts];
    private readonly Stack<int> _freeSlots = new();
    private int _slotTop;
    private bool[] _dirtyBlocks = new bool[InitialSlots / DirtyBlock];
    private bool _anyDirty;
    private const int InitialSlots = 8192;
    private const int DirtyBlock = 256; // slots per table write

    private readonly ComputePipeline _cullPass, _bandPass, _listPass;
    private BindGroup* _cullGroup, _bandGroup, _listGroup, _drawGroup;
    private BindGroupLayout* _drawLayout;
    private bool _groupsStale = true;

    /// <summary>Chunks in the pool and pages they use; the quad buffer's size (for reports).</summary>
    public int Chunks { get; private set; }
    public int PagesUsed => _pages.Used;
    public ulong CapacityBytes => _quads.SizeBytes;

    public WorldMeshPool(GpuContext ctx)
    {
        _ctx = ctx;
        _api = ctx.Api;
        ulong limit = System.Math.Min(ctx.AdapterLimits.MaxBufferSize, ctx.AdapterLimits.MaxStorageBufferBindingSize);
        limit = System.Math.Min(limit, 1UL << 30);
        _maxPages = (int)(limit / PageBytes);
        int pages = System.Math.Min(_maxPages, (64 << 20) / PageBytes);
        _pages = new PageAllocator(pages);

        _quads     = CreateStorage((ulong)pages * PageBytes, BufferUsage.CopySrc);
        _visible   = CreateStorage((ulong)pages * 8);
        _chunks    = CreateStorage((ulong)InitialSlots * SlotInts * 4);
        _visChunks = CreateStorage((ulong)InitialSlots * 16);
        _counters  = CreateStorage((2 * Bands + 4) * 4);
        _args      = CreateStorage(12 * 4, BufferUsage.Indirect);
        _cull      = GpuBuffer.Create(ctx, CullUniform.Size, BufferUsage.Uniform | BufferUsage.CopyDst);

        _cullPass = new ComputePipeline(ctx, CullWgsl, "cull_main");
        _bandPass = new ComputePipeline(ctx, CullWgsl, "band_main");
        _listPass = new ComputePipeline(ctx, CullWgsl, "list_main");
    }

    private GpuBuffer CreateStorage(ulong size, BufferUsage extra = BufferUsage.None)
        => GpuBuffer.Create(_ctx, System.Math.Max(16UL, size), BufferUsage.Storage | BufferUsage.CopyDst | extra);

    /// <summary>Pools a chunk mesh: <paramref name="quadCount"/> packed quads of the chunk at <paramref name="pos"/> in
    /// grid <paramref name="grid"/> (the static world, at the origin unrotated). Returns its slot, hidden until
    /// <see cref="SetShown"/>, or -1 if the pool is full.</summary>
    public int Add(ReadOnlySpan<byte> quads, uint quadCount, int grid, ChunkPosition pos)
    {
        int pages = (int)((quadCount + PageQuads - 1) / PageQuads);
        if (pages == 0) return -1;
        int first = _pages.Alloc(pages);
        if (first < 0 && Grow(pages)) first = _pages.Alloc(pages);
        if (first < 0) return -1;

        _quads.Write((ulong)first * PageBytes, quads);
        int slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : NewSlot();
        var e = _table.AsSpan(slot * SlotInts, SlotInts);
        e[0] = pos.X; e[1] = pos.Y; e[2] = pos.Z; e[3] = grid;
        e[4] = first; e[5] = pages; e[6] = (int)quadCount; e[7] = 0;
        MarkDirty(slot);
        Chunks++;
        return slot;
    }

    public void Remove(int slot)
    {
        var e = _table.AsSpan(slot * SlotInts, SlotInts);
        _pages.Free(e[4], e[5]);
        e.Clear();
        MarkDirty(slot);
        _freeSlots.Push(slot);
        Chunks--;
    }

    /// <summary>Whether the chunk is drawn (it's in the rendering layer; see EntityPresenceSystem).</summary>
    public void SetShown(int slot, bool shown)
    {
        ref int flags = ref _table[slot * SlotInts + 7];
        int f = shown ? 1 : 0;
        if (flags == f) return;
        flags = f;
        MarkDirty(slot);
    }

    private int NewSlot()
    {
        int slot = _slotTop++;
        if (_slotTop * SlotInts <= _table.Length) return slot;
        // The table doubles; the GPU's copy is rewritten whole from the CPU's.
        int slots = _table.Length / SlotInts * 2;
        Array.Resize(ref _table, slots * SlotInts);
        _dirtyBlocks = new bool[slots / DirtyBlock];
        Array.Fill(_dirtyBlocks, true);
        _anyDirty = true;
        _chunks.Dispose();
        _visChunks.Dispose();
        _chunks    = CreateStorage((ulong)slots * SlotInts * 4);
        _visChunks = CreateStorage((ulong)slots * 16);
        _groupsStale = true;
        return slot;
    }

    private void MarkDirty(int slot)
    {
        _dirtyBlocks[slot / DirtyBlock] = true;
        _anyDirty = true;
    }

    /// <summary>Makes room for a run of <paramref name="pages"/> more by doubling the quad buffer (copying what it
    /// holds), up to the device's limit.</summary>
    private bool Grow(int pages)
    {
        int cap = _pages.Capacity;
        if (cap >= _maxPages) return false;
        int next = cap;
        while (next < _pages.Top + pages && next < _maxPages) next = System.Math.Min(_maxPages, next * 2);
        if (next < _pages.Top + pages) return false;

        var quads = CreateStorage((ulong)next * PageBytes, BufferUsage.CopySrc);
        if (_pages.Top > 0) _ctx.CopyBufferToBuffer(_quads, quads, (ulong)_pages.Top * PageBytes);
        _quads.Dispose();
        _quads = quads;
        _visible.Dispose();
        _visible = CreateStorage((ulong)next * 8);
        _pages.Capacity = next;
        _groupsStale = true;
        return true;
    }

    /// <summary>Writes the chunk table's changes (one write per run of changed blocks), then records the culling
    /// passes into <paramref name="encoder"/>, ahead of the render pass that draws what they list.</summary>
    public void Cull(CommandEncoder* encoder, in Frustum frustum, Vector3D<float> camera, float range)
    {
        FlushTable();
        EnsureGroups();

        var u = new CullUniform { Camera = new Vector4D<float>(camera, 1f / System.Math.Max(range, 1f)), Slots = (uint)_slotTop };
        frustum.CopyPlanes(new Span<Vector4D<float>>(&u.P0, 6));
        _cull.Write(0, new ReadOnlySpan<CullUniform>(&u, 1));

        _api.CommandEncoderClearBuffer(encoder, _counters.Handle, 0, _counters.SizeBytes);
        uint groups = (uint)System.Math.Max(1, (_slotTop + Group - 1) / Group);
        _cullPass.Record(encoder, _cullGroup, groups, timingName: "World culling");
        _bandPass.Record(encoder, _bandGroup, 1);
        _listPass.Record(encoder, _listGroup, groups);
    }

    private void FlushTable()
    {
        if (!_anyDirty) return;
        _anyDirty = false;
        int blocks = (_slotTop + DirtyBlock - 1) / DirtyBlock;
        for (int b = 0; b < blocks;)
        {
            if (!_dirtyBlocks[b]) { b++; continue; }
            int end = b;
            while (end < blocks && _dirtyBlocks[end]) _dirtyBlocks[end++] = false;
            int from = b * DirtyBlock * SlotInts, to = System.Math.Min(end * DirtyBlock, _slotTop) * SlotInts;
            _chunks.Write<int>((ulong)from * 4, (ReadOnlySpan<int>)_table.AsSpan(from, to - from));
            b = end;
        }
        Array.Clear(_dirtyBlocks);
    }

    /// <summary>Draws the listed pages: filled, or as quad outlines for the wireframe. <paramref name="layout"/> is
    /// the draw's group-1 layout (the quads, the page list and the chunk table).</summary>
    public void Draw(RenderPassEncoder* pass, BindGroupLayout* layout, GpuBuffer quadIndices, bool wireframe)
    {
        if (Chunks == 0) return;
        if (_drawLayout != layout) { _drawLayout = layout; _groupsStale = true; }
        EnsureGroups();
        _api.RenderPassEncoderSetBindGroup(pass, 1, _drawGroup, 0, null);
        if (wireframe) _api.RenderPassEncoderDrawIndirect(pass, _args.Handle, 5 * 4);
        else
        {
            _api.RenderPassEncoderSetIndexBuffer(pass, quadIndices.Handle, IndexFormat.Uint32, 0, quadIndices.SizeBytes);
            _api.RenderPassEncoderDrawIndexedIndirect(pass, _args.Handle, 0);
        }
    }

    private void EnsureGroups()
    {
        if (!_groupsStale) return;
        _groupsStale = false;
        Release(ref _cullGroup); Release(ref _bandGroup); Release(ref _listGroup); Release(ref _drawGroup);
        _cullGroup = _cullPass.CreateBindGroup(new[] { (0u, _cull), (1u, _chunks), (2u, _counters), (3u, _visChunks) });
        _bandGroup = _bandPass.CreateBindGroup(new[] { (2u, _counters), (5u, _args) });
        _listGroup = _listPass.CreateBindGroup(new[] { (1u, _chunks), (2u, _counters), (3u, _visChunks), (4u, _visible) });
        if (_drawLayout == null) return;
        BindGroupEntry* entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 1, Buffer = _quads.Handle,   Offset = 0, Size = _quads.SizeBytes };
        entries[1] = new BindGroupEntry { Binding = 2, Buffer = _visible.Handle, Offset = 0, Size = _visible.SizeBytes };
        entries[2] = new BindGroupEntry { Binding = 3, Buffer = _chunks.Handle,  Offset = 0, Size = _chunks.SizeBytes };
        var desc = new BindGroupDescriptor { Layout = _drawLayout, EntryCount = 3, Entries = entries };
        _drawGroup = _api.DeviceCreateBindGroup(_ctx.Device, &desc);
    }

    private void Release(ref BindGroup* group)
    {
        if (group != null) _api.BindGroupRelease(group);
        group = null;
    }

    public void Dispose()
    {
        Release(ref _cullGroup); Release(ref _bandGroup); Release(ref _listGroup); Release(ref _drawGroup);
        _cullPass.Dispose(); _bandPass.Dispose(); _listPass.Dispose();
        _quads.Dispose(); _visible.Dispose(); _chunks.Dispose(); _visChunks.Dispose();
        _counters.Dispose(); _args.Dispose(); _cull.Dispose();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CullUniform
    {
        public const int Size = 7 * 16 + 16;
        public Vector4D<float> P0, P1, P2, P3, P4, P5;
        public Vector4D<float> Camera; // xyz, w: 1 / the distance the bands cover
        public uint Slots, Pad0, Pad1, Pad2;
    }

    // Three passes: cull_main tests each chunk against the view and claims room in its distance band; band_main (one
    // thread) turns the bands' page counts into start offsets and writes the draw arguments; list_main writes each
    // visible chunk's pages at its place. Bands are spaced by the square root of the distance, so the near ones, where
    // the order matters most, are narrow.
    private const string CullWgsl = @"
struct Cull { planes: array<vec4<f32>, 6>, camera: vec4<f32>, slots: vec4<u32> };
@group(0) @binding(0) var<uniform> cull: Cull;
@group(0) @binding(1) var<storage, read> chunks: array<vec4<i32>>;
@group(0) @binding(2) var<storage, read_write> counters: array<atomic<u32>>;
@group(0) @binding(3) var<storage, read_write> visChunks: array<vec4<u32>>;
@group(0) @binding(4) var<storage, read_write> visible: array<vec2<u32>>;
@group(0) @binding(5) var<storage, read_write> args: array<u32>;

const BANDS: u32 = 1024u;
const PAGE_QUADS: u32 = 64u;
const VISIBLE_COUNT: u32 = 2048u; // counters[2 * BANDS]

@compute @workgroup_size(64)
fn cull_main(@builtin(global_invocation_id) id: vec3<u32>) {
    let slot = id.x;
    if (slot >= cull.slots.x) { return; }
    let at = chunks[2u * slot];
    let span = chunks[2u * slot + 1u];
    if (span.w == 0 || span.y == 0) { return; }
    let lo = vec3<f32>(at.xyz * 32);
    let hi = lo + vec3<f32>(32.0);
    for (var i = 0u; i < 6u; i++) {
        let p = cull.planes[i];
        let v = select(lo, hi, p.xyz >= vec3<f32>(0.0));
        if (dot(p.xyz, v) + p.w < 0.0) { return; }
    }
    let d = length(lo + vec3<f32>(16.0) - cull.camera.xyz);
    let band = min(BANDS - 1u, u32(sqrt(d * cull.camera.w) * f32(BANDS)));
    let offset = atomicAdd(&counters[band], u32(span.y));
    let n = atomicAdd(&counters[VISIBLE_COUNT], 1u);
    visChunks[n] = vec4<u32>(slot, band, offset, 0u);
}

@compute @workgroup_size(1)
fn band_main() {
    var total = 0u;
    for (var b = 0u; b < BANDS; b++) {
        atomicStore(&counters[BANDS + b], total);
        total += atomicLoad(&counters[b]);
    }
    args[0] = 6u * PAGE_QUADS; args[1] = total; args[2] = 0u; args[3] = 0u; args[4] = 0u;
    args[5] = 8u * PAGE_QUADS; args[6] = total; args[7] = 0u; args[8] = 0u;
}

@compute @workgroup_size(64)
fn list_main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x >= atomicLoad(&counters[VISIBLE_COUNT])) { return; }
    let v = visChunks[id.x];
    let span = chunks[2u * v.x + 1u];
    let start = atomicLoad(&counters[BANDS + v.y]) + v.z;
    for (var p = 0u; p < u32(span.y); p++) {
        visible[start + p] = vec2<u32>(u32(span.x) + p, v.x);
    }
}
";
}
