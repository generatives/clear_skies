using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>A voxel grid's registration in the <see cref="GridStore"/>: the static world or one ship. Owned by
/// its <see cref="ChunkVolume"/>; <see cref="Index"/> is -1 until the grid's first chunk is uploaded.</summary>
public sealed class GridHandle
{
    /// <summary>Descriptor index in the GPU grid table, or -1 while not registered.</summary>
    public int Index { get; internal set; } = -1;

    internal bool IsWorld;
    internal readonly Dictionary<ChunkPosition, ChunkRecord> Chunks = new();

    // Chunk-table section. A ship's covers its chunk box: entry index = TableBase + cell, where
    // cell = wrap(c.x, TDX) + TDX*(wrap(c.y, TDY) + TDY*wrap(c.z, TDZ)), the entry holding the chunk's coordinate as a
    // tag. The world's is instead a block of entries for its loaded chunks only, found through the world index: a
    // TDX x TDY x TDZ grid of 16-bit offsets into the block, wrapped the same way (see GridStore).
    internal int TableBase = -1, TDX, TDY, TDZ;

    // Chunk box of the grid's records (inclusive), and whether it changed since the bounds were last derived.
    internal ChunkPosition BoxMin, BoxMax;
    internal bool HasBox, BoxDirty;

    // Light slots owned by this grid (its surface bricks); SlotListPos in GridStore finds a slot's place here.
    internal readonly List<int> Slots = new();

    // Voxel bounds of the solid bricks (grid space), for ships' world-space occluder bounds.
    internal bool HasSolid;
    internal Vector3D<float> SolidMin, SolidMax;

    /// <summary>Bumped whenever any of this grid's chunks change.</summary>
    internal int Version;

    internal Mat4 VoxelToWorld = Mat4.Identity, WorldToVoxel = Mat4.Identity;

    // Whether SetPose ran since the last descriptor upload. A grid with no pose this frame (a ship whose physics
    // body doesn't exist yet) is hidden from rays and lookups rather than traced at a made-up position.
    internal bool Posed;
}

/// <summary>One chunk position a grid has storage for.</summary>
internal sealed class ChunkRecord
{
    public ChunkPosition Pos;
    public int TableIndex;
    public int OccSlot = -1;          // occupancy pool slot, or -1 for a uniform chunk
    public ulong Solid, Air;          // per-8³-brick "has solid" / "has air" (bit = bx + 4*(by + 4*bz))
    public ulong Surface;             // bricks holding (conservatively) a surface air voxel
    public int[]? BrickSlots;         // light slot per brick, -1 = none
    public bool Virtual;              // a ship's air chunk next to its blocks, with no ChunkEntry behind it
    public int WorldCell = -1;        // world only: its world index cell, and its entry's offset in the world block
    public int WorldSlot = -1;
}

/// <summary>
/// GPU storage shared by the static world and every ship, per the ray-traced lighting design doc:
/// <list type="bullet">
/// <item>an occupancy pool of 4 KB chunk slots (1 bit per voxel, word = ly + 32*lz, bit = lx);</item>
/// <item>a chunk table of <c>(occupancy slot or sentinel, cx, cy, cz)</c> entries: a section per ship, indexed by
/// wrapping the chunk coordinate to the ship's chunk box, and a block of entries for the world's loaded chunks. The
/// coordinate is a tag, so a wrapped lookup that lands on a different chunk reads as unloaded instead of aliasing
/// onto it;</item>
/// <item>the world index, after the chunk table's entries in the same buffer: a grid of 16-bit offsets into the
/// world's block (0xFFFF = none), one per chunk position, wrapped on the chunk coordinate. It spans the view distance
/// both ways and the 64 streamed layers, so the loaded chunks never wrap onto one another, and it costs 2 bytes per
/// position where a full entry would cost 288;</item>
/// <item>a brick table (64 entries per chunk entry) giving each 8³ brick's light slot, or <see cref="NoSurface"/>;</item>
/// <item>a light pool of <see cref="WordsPerSlot"/>-word brick slots, allocated only for bricks that contain a
/// surface air voxel: per voxel a 16-bit display value (RGB 4 bits each, sun 2 bits, AO 2 bits — the only part the
/// fragment shader reads) and a 32-bit accumulation word (bounce RGB and AO, 8 bits each). See GpuRayLightPass;</item>
/// <item>per light slot, its grid, chunk and brick — <c>(brick | grid &lt;&lt; 6, cx, cy, cz)</c> — so a work
/// list is just slot numbers;</item>
/// <item>per grid, its transforms, table section and voxel bounds.</item>
/// </list>
/// Nothing here reallocates as the camera moves: loads and unloads take and return slots, and only a pool that
/// runs out grows (by copying into a buffer twice the size, which bumps <see cref="BindingVersion"/>).
/// </summary>
public sealed class GridStore : IDisposable
{
    public const int WordsPerChunk = 1024;
    public const int VoxelsPerBrick = 512;

    /// <summary>Light-pool words per brick slot: 256 display words (two 16-bit voxels each) then 512
    /// accumulation words.</summary>
    public const int WordsPerSlot = 768;
    public const int SlotBytes = WordsPerSlot * 4;

    // Chunk-table occupancy codes (entry.x). >= 0 is an occupancy slot.
    public const int OccUnloaded = -1, OccAllAir = -2, OccAllSolid = -3;
    public const uint NoSurface = 0xFFFFFFFFu;

    /// <summary>Chunk-table entry: two vec4&lt;i32&gt; — (occupancy slot or code, cx, cy, cz) and (solid brick
    /// mask low, high, 0, 0).</summary>
    public const int ChunkEntryBytes = 32;

    /// <summary>A fresh display word (two voxels): full sun, no light, no AO — ambient and sun only.</summary>
    public const uint EmptyDisplayPair = 0x30003000u;

    /// <summary>WGSL for every shader that reads the store: the grid descriptor (see <see cref="GridDesc"/>) and the
    /// chunk-table lookup. The including shader declares the <c>chunkTable</c> and <c>grids</c> bindings.</summary>
    internal const string LookupWgsl = @"
struct GridDesc {
    v2w: mat4x4<f32>,
    w2v: mat4x4<f32>,
    table: vec4<i32>,  // x: chunk-table base, yzw: section (or world index) dims in chunks (0 = unused descriptor)
    bmin: vec4<i32>,   // xyz: voxel bounds [bmin, bmax) of the grid's chunks, grid space; w: the world index's
    bmax: vec4<i32>,   // position in chunkTable (vec4s), 0 for a ship's plain section
};

// a mod n in [0, n). Unsigned arithmetic only: signed % returns wrong results for negative operands on at least
// one backend (measured: -1887 % 19 gave 0), which broke every lookup at negative coordinates.
fn wrapi(a: i32, n: i32) -> i32 {
    let un = u32(n);
    if (a >= 0) { return i32(u32(a) % un); }
    return n - 1 - i32(u32(-(a + 1)) % un);
}

// Chunk-table entry index of chunk c in grid g, or -1 when that chunk isn't stored. Tables wrap, so the entry's own
// coordinate tag decides whether it really is this chunk. Each entry is two vec4s: (occupancy slot or code, cx, cy,
// cz) and (solid-brick mask low, high, 0, 0).
// A ship's cell is its entry. The world's cell is instead a 16-bit offset (0xFFFF = none) into its block of entries,
// packed eight to a vec4 in the world index.
fn entryOf(g: i32, c: vec3<i32>) -> i32 {
    let t = grids[g].table;
    if (t.y <= 0) { return -1; }
    let cell = wrapi(c.x, t.y) + t.y * (wrapi(c.y, t.z) + t.z * wrapi(c.z, t.w));
    var idx = t.x + cell;
    let index = grids[g].bmin.w;
    if (index > 0) {
        let word = u32(chunkTable[index + (cell >> 3u)][(cell >> 1u) & 3]);
        let offset = (word >> (u32(cell & 1) * 16u)) & 0xFFFFu;
        if (offset == 0xFFFFu) { return -1; }
        idx = t.x + i32(offset);
    }
    let e = chunkTable[2 * idx];
    if (e.y != c.x || e.z != c.y || e.w != c.z) { return -1; }
    return idx;
}
";

    private const int S = ChunkData.Size;
    private const int UnusedTag = int.MinValue;

    /// <summary>The most world chunks that can be loaded at once: the world index's offsets are 16-bit.</summary>
    public const int MaxWorldChunks = NoWorldSlot - 1;

    private readonly GpuContext _ctx;

    internal GpuBuffer OccPool { get; private set; }
    internal GpuBuffer ChunkTable { get; private set; }
    internal GpuBuffer BrickTable { get; private set; }
    internal GpuBuffer LightPool { get; private set; }
    internal GpuBuffer SlotInfo { get; private set; }
    internal GpuBuffer Grids { get; private set; }

    /// <summary>Bumped whenever a buffer above is replaced (pool growth). Bind groups over them are stale.</summary>
    public int BindingVersion { get; private set; }

    private int _occCapacity, _occNext;
    private readonly Stack<int> _occFree = new();
    private int _tableCapacity;

    /// <summary>Chunk-table entries the tables currently hold (any entry index is below this).</summary>
    internal int TableCapacity => _tableCapacity;
    private readonly List<(int start, int count)> _tableFree = new();
    private int _tableNext;
    private int _lightCapacity, _lightNext;
    private readonly Stack<int> _lightFree = new();

    // CPU mirror of each light slot's owner; -1 grid = free.
    internal int[] SlotGrid = Array.Empty<int>();
    internal ChunkPosition[] SlotChunk = Array.Empty<ChunkPosition>();
    internal byte[] SlotBrick = Array.Empty<byte>();
    private int[] _slotListPos = Array.Empty<int>();

    // Grid registry; grows (with the descriptor buffer) when every index is taken, so there is no ship limit.
    private GridHandle?[] _grids = new GridHandle?[16];
    private GridDesc[] _descs = new GridDesc[16];
    // The world index: _worldDim x WorldLayers x _worldDim cells after the chunk table's entries (see the class
    // summary), and the world's block of entries it points into, handed out as offsets.
    public const int WorldLayers = 64;
    private const int NoWorldSlot = 0xFFFF;
    private readonly int _worldDim;
    private readonly ulong _worldIndexBytes;
    private readonly Dictionary<int, ChunkRecord> _worldCells = new();
    private int _worldCapacity, _worldNext;
    private readonly Stack<int> _worldFree = new();

    /// <summary>Light slots allocated since the lighting system last drained this. They hold
    /// <see cref="EmptyDisplayPair"/> and zeroed accumulation, and need lighting.</summary>
    internal List<int> NewSlots { get; } = new();

    /// <summary>Occupancy changes since the lighting system last drained this: the grid-space voxel box that changed
    /// (the edited blocks' bounds for an edit, the whole chunk for a load or unload), and whether the chunk had or has
    /// any solid (an all-air change can't have changed any shadow), and whether it was an edit that placed a
    /// light-blocking block (the only kind of change that can darken what was already lit).</summary>
    internal List<(GridHandle grid, Vector3D<float> min, Vector3D<float> max, bool solid, bool darkens)> ChangedChunks { get; } = new();

    public int LightSlotsInUse => _lightNext - _lightFree.Count;
    public int LightSlotCapacity => _lightCapacity;
    public int OccSlotsInUse => _occNext - _occFree.Count;
    public int OccSlotCapacity => _occCapacity;
    public int LightSlotHighWater => _lightNext;
    public int WorldChunkCount => _worldCells.Count;

    // Scratch.
    private readonly uint[] _brickRun = new uint[64];
    private readonly uint[] _emptyBrick;

    /// <summary>How many light bricks the static world may use (see ChunkLoadSystem): what was asked for, or less if
    /// this device's max buffer size can't hold it along with the ships' allowance and headroom.</summary>
    public int WorldLightBudget { get; }

    /// <param name="worldLightBudget">How many light bricks the static world should use at most: the streaming budget
    /// (sizes the light pool, with headroom for ships and for streaming's estimates).</param>
    /// <param name="worldIndexDim">The world index's width in chunks (x and z; it wraps): wider than the span of world
    /// chunks ever loaded at once, so two loaded chunks never share a cell. In y it covers <see cref="WorldLayers"/>.</param>
    public GridStore(GpuContext ctx, int worldLightBudget, int worldIndexDim)
    {
        _ctx = ctx;
        _worldDim = worldIndexDim;
        _worldIndexBytes = ((ulong)worldIndexDim * WorldLayers * (ulong)worldIndexDim * 2 + 15) & ~15UL;
        _worldCapacity = 16384; // grows (to MaxWorldChunks) with what streaming loads

        // Pools start sized to the budget so they don't grow (a copy + rebind hitch) during normal play: the light
        // pool holds the world's budget plus a fifth for streaming's estimates running over, plus ships. Occupancy is
        // only needed by chunks mixing solid and air, measured at ~26 light bricks each.
        ulong maxBytes = System.Math.Min(ctx.AdapterLimits.MaxBufferSize, ctx.AdapterLimits.MaxStorageBufferBindingSize);
        int maxLight = (int)System.Math.Min(maxBytes / SlotBytes, int.MaxValue);
        const int shipBricks = 2048;
        _lightCapacity = (int)System.Math.Min((long)worldLightBudget + worldLightBudget / 5 + shipBricks, maxLight);
        WorldLightBudget = System.Math.Min(worldLightBudget, (int)((_lightCapacity - shipBricks) / 1.2));
        if (WorldLightBudget < worldLightBudget)
            Console.WriteLine($"[grid-store] a light budget of {worldLightBudget} bricks doesn't fit this device's max buffer size; " +
                              $"using {WorldLightBudget}.");
        _occCapacity   = WorldLightBudget / 26 + 512;
        _tableCapacity = _worldCapacity + 4096;
        Console.WriteLine($"[grid-store] light budget {WorldLightBudget} bricks: light pool {_lightCapacity} bricks " +
                          $"({(ulong)_lightCapacity * SlotBytes / (1024 * 1024)} MB), occupancy {_occCapacity} chunks " +
                          $"({(ulong)_occCapacity * WordsPerChunk * 4 / (1024 * 1024)} MB), table {_tableCapacity} entries " +
                          $"({(ulong)_tableCapacity * (ChunkEntryBytes + 256) / (1024 * 1024)} MB), world index " +
                          $"{worldIndexDim}x{WorldLayers}x{worldIndexDim} ({_worldIndexBytes / (1024 * 1024)} MB)");

        OccPool    = GpuBuffer.CreateStorage(ctx, (ulong)_occCapacity * WordsPerChunk * 4);
        ChunkTable = GpuBuffer.CreateStorage(ctx, (ulong)_tableCapacity * ChunkEntryBytes + _worldIndexBytes);
        BrickTable = GpuBuffer.CreateStorage(ctx, (ulong)_tableCapacity * 64 * 4);
        LightPool  = GpuBuffer.CreateStorage(ctx, (ulong)_lightCapacity * SlotBytes);
        SlotInfo   = GpuBuffer.CreateStorage(ctx, (ulong)_lightCapacity * 16);
        Grids      = GpuBuffer.CreateStorage(ctx, (ulong)_descs.Length * (ulong)Marshal.SizeOf<GridDesc>());
        ResizeSlotMirror(_lightCapacity);

        _emptyBrick = new uint[WordsPerSlot];
        Array.Fill(_emptyBrick, EmptyDisplayPair, 0, 256);

        ClearTableRange(0, _tableCapacity);
        _tableNext = 0;
        var none = new uint[System.Math.Min(_worldIndexBytes / 4, 1UL << 20)];
        Array.Fill(none, uint.MaxValue);
        for (ulong at = 0; at < _worldIndexBytes; at += (ulong)none.Length * 4)
            ChunkTable.Write<uint>(WorldIndexOffset + at, none.AsSpan(0, (int)System.Math.Min((ulong)none.Length, (_worldIndexBytes - at) / 4)));
    }

    /// <summary>Byte offset of the world index in <see cref="ChunkTable"/>: right after the entries.</summary>
    private ulong WorldIndexOffset => (ulong)_tableCapacity * ChunkEntryBytes;

    // ── Grid registration ─────────────────────────────────────────────────────

    internal void Register(GridHandle g, bool isWorld)
    {
        if (g.Index >= 0) return;
        int idx = Array.IndexOf(_grids, null);
        if (idx < 0)
        {
            idx = _grids.Length;
            Array.Resize(ref _grids, idx * 2);
            Array.Resize(ref _descs, idx * 2);
            Grids = Grow(Grids, (ulong)_descs.Length * (ulong)Marshal.SizeOf<GridDesc>());
            Console.WriteLine($"[grid-store] grid table grown to {_grids.Length} grids");
        }
        _grids[idx] = g;
        g.Index = idx;
        g.IsWorld = isWorld;
        if (isWorld)
        {
            g.TableBase = AllocRange(_worldCapacity);
            g.TDX = _worldDim; g.TDY = WorldLayers; g.TDZ = _worldDim;
        }
    }

    /// <summary>Releases everything a grid holds (a despawned ship).</summary>
    public void Unregister(GridHandle g)
    {
        if (g.Index < 0) return;
        foreach (var rec in g.Chunks.Values.ToList()) FreeRecord(g, rec);
        g.Chunks.Clear();
        if (g.TableBase >= 0) FreeSection(g);
        _grids[g.Index] = null;
        _descs[g.Index] = default;
        g.Index = -1;
    }

    internal GridHandle? GridAt(int index) => index >= 0 && index < _grids.Length ? _grids[index] : null;

    /// <summary>Sets a grid's transforms for this frame's descriptor upload (see <see cref="UploadGrids"/>).</summary>
    internal void SetPose(GridHandle g, in Mat4 voxelToWorld, in Mat4 worldToVoxel)
    {
        g.VoxelToWorld = voxelToWorld;
        g.WorldToVoxel = worldToVoxel;
        g.Posed = true;
    }

    /// <summary>Writes every registered grid's descriptor (transforms, table section, voxel bounds). Grids not
    /// posed since the last upload get an empty table, which hides them.</summary>
    internal void UploadGrids()
    {
        int count = 0;
        for (int i = 0; i < _grids.Length; i++)
        {
            var g = _grids[i];
            if (g == null) { _descs[i] = default; continue; }
            count = i + 1;
            UpdateBounds(g);
            ref var d = ref _descs[i];
            d.VoxelToWorld = g.VoxelToWorld;
            d.WorldToVoxel = g.WorldToVoxel;
            d.TableBase = g.TableBase; d.TDX = g.TDX; d.TDY = g.TDY; d.TDZ = g.TDZ;
            if (!g.Posed || g.TableBase < 0) d.TDX = d.TDY = d.TDZ = 0;
            d.WorldIndex = g.IsWorld ? (int)(WorldIndexOffset / 16) : 0;
            g.Posed = false;
            if (g.HasBox)
            {
                d.MinX = g.BoxMin.X * S; d.MinY = g.BoxMin.Y * S; d.MinZ = g.BoxMin.Z * S;
                d.MaxX = (g.BoxMax.X + 1) * S; d.MaxY = (g.BoxMax.Y + 1) * S; d.MaxZ = (g.BoxMax.Z + 1) * S;
            }
            else { d.MinX = d.MinY = d.MinZ = 0; d.MaxX = d.MaxY = d.MaxZ = 0; }
        }
        if (count > 0) Grids.Write<GridDesc>(0, _descs.AsSpan(0, count));
    }

    // ── Chunk upload / removal ────────────────────────────────────────────────

    /// <summary>Uploads a chunk's occupancy (packing it from block data if its cached words are stale), then
    /// updates which of its and its neighbours' bricks need light storage.</summary>
    internal void UploadChunk(GridHandle g, ChunkPosition pos, ChunkEntry entry)
    {
        if (g.Index < 0) Register(g, isWorld: false);
        var words = PackChunk(entry);

        // A chunk already uploaded that was edited changed only within its edit bounds; anything else is new.
        bool wasUploaded = g.Chunks.TryGetValue(pos, out var existing) && !existing.Virtual;
        var origin = pos.WorldOrigin;
        var changedMin = origin;
        var changedMax = origin + new Vector3D<float>(S);
        bool darkens = false;
        if (wasUploaded && entry.HasEdits)
        {
            changedMin = origin + new Vector3D<float>(entry.EditMin.X, entry.EditMin.Y, entry.EditMin.Z);
            changedMax = origin + new Vector3D<float>(entry.EditMax.X + 1, entry.EditMax.Y + 1, entry.EditMax.Z + 1);
            darkens = entry.EditsAddedSolid;
        }
        entry.ClearEdits();

        var rec = EnsureRecord(g, pos);
        if (rec == null) return;
        bool hadSolid = rec.Solid != 0;
        rec.Virtual = false;
        rec.Solid = entry.BrickSolidMask;
        rec.Air   = entry.BrickAirMask;

        if (rec.Solid == 0 || rec.Air == 0)
        {
            FreeOcc(rec);
            entry.PackedOpacityWords = null; // all one value: not worth keeping 4 KB of it per buried chunk
        }
        else
        {
            if (rec.OccSlot < 0) rec.OccSlot = AllocOcc();
            OccPool.Write<uint>((ulong)rec.OccSlot * WordsPerChunk * 4, words);
        }
        WriteChunkEntry(rec);

        // A ship's air right next to its blocks may lie in a chunk it has no data for (a hull sitting at the bottom
        // of its chunk); give those chunks records so their surface bricks get light too.
        if (!g.IsWorld && rec.Solid != 0)
            for (int f = 0; f < 6; f++)
            {
                var (dx, dy, dz) = FaceDir(f);
                var npos = pos.Offset(dx, dy, dz);
                if (!g.Chunks.ContainsKey(npos)) EnsureRecord(g, npos, isVirtual: true);
            }

        RefreshSurfaceAround(g, rec);
        g.Version++;
        ChangedChunks.Add((g, changedMin, changedMax, hadSolid || rec.Solid != 0, darkens));
    }

    /// <summary>Drops a chunk that unloaded.</summary>
    internal void RemoveChunk(GridHandle g, ChunkPosition pos)
    {
        if (!g.Chunks.TryGetValue(pos, out var rec)) return;
        bool hadSolid = rec.Solid != 0;
        FreeRecord(g, rec);
        g.Chunks.Remove(pos);
        g.BoxDirty = true;
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceDir(f);
            if (g.Chunks.TryGetValue(pos.Offset(dx, dy, dz), out var n)) RefreshSurface(g, n);
        }
        g.Version++;
        ChangedChunks.Add((g, pos.WorldOrigin, pos.WorldOrigin + new Vector3D<float>(S), hadSolid, false));
    }

    /// <summary>The light slot holding brick (bx,by,bz) of chunk <paramref name="pos"/>, or -1.</summary>
    internal int SlotOf(GridHandle g, ChunkPosition pos, int brick)
    {
        if (!g.Chunks.TryGetValue(pos, out var rec) || rec.BrickSlots == null) return -1;
        return rec.BrickSlots[brick];
    }

    private static uint[] PackChunk(ChunkEntry entry)
    {
        if (entry.PackedOpacityWords is { } cached) return cached;

        var words = entry.PackedOpacityWords = new uint[WordsPerChunk];
        entry.Emitters.Clear();
        PackOpacity(entry.Data, words, entry.Emitters);
        (entry.BrickSolidMask, entry.BrickAirMask) = BrickMasks(words);
        return words;
    }

    /// <summary>Packs a chunk's opacity into <paramref name="words"/> (lx is the in-word bit, ly + 32*lz the word),
    /// listing its light emitters if <paramref name="emitters"/> is given.</summary>
    private static void PackOpacity(ChunkData data, uint[] words, List<EmitterVoxel>? emitters)
    {
        if (data.IsUniform(out var block) && BlockRegistry.Get(block).LightEmission == 0)
        {
            Array.Fill(words, BlockRegistry.Get(block).Opacity >= 15 ? uint.MaxValue : 0u);
            return;
        }
        for (int lz = 0; lz < S; lz++)
        for (int ly = 0; ly < S; ly++)
        {
            uint bits = 0u;
            for (int lx = 0; lx < S; lx++)
            {
                var def = BlockRegistry.Get(data.Get(lx, ly, lz));
                if (def.Opacity >= 15) bits |= 1u << lx;
                if (def.LightEmission > 0)
                    emitters?.Add(new EmitterVoxel((byte)lx, (byte)ly, (byte)lz, def.LightEmission, def.Id));
            }
            words[ly + S * lz] = bits;
        }
    }

    /// <summary>A chunk's per-brick "any opaque" / "any non-opaque" bits, as <see cref="UploadChunk"/> will compute
    /// them: which of its bricks can need light storage (see <see cref="RefreshSurface"/>). For streaming to estimate
    /// a chunk's light cost before it is uploaded.</summary>
    internal static (ulong Solid, ulong Air) BrickMasksOf(ChunkData data)
    {
        if (data.IsUniform(out var block) && BlockRegistry.Get(block).LightEmission == 0)
            return BlockRegistry.Get(block).Opacity >= 15 ? (ulong.MaxValue, 0UL) : (0UL, ulong.MaxValue);
        var words = new uint[WordsPerChunk];
        PackOpacity(data, words, null);
        return BrickMasks(words);
    }

    /// <summary>Bricks that need light storage: those holding air next to solid in the same or a face-adjacent brick
    /// (see <see cref="RefreshSurface"/>), given the chunk's masks and its six neighbours' solid masks (0 where a
    /// neighbour isn't known), in <see cref="FaceDir"/> order.</summary>
    internal static ulong SurfaceBricks(ulong solid, ulong air, ReadOnlySpan<ulong> neighbourSolid)
    {
        ulong near = solid
            | ((solid >> 1) & ~BxHi) | ((solid << 1) & ~BxLo)
            | ((solid >> 4) & ~ByHi) | ((solid << 4) & ~ByLo)
            | (solid >> 16) | (solid << 16);
        near |= (neighbourSolid[0] & BxLo) << 3;
        near |= (neighbourSolid[1] & BxHi) >> 3;
        near |= (neighbourSolid[2] & ByLo) << 12;
        near |= (neighbourSolid[3] & ByHi) >> 12;
        near |= (neighbourSolid[4] & BzLo) << 48;
        near |= (neighbourSolid[5] & BzHi) >> 48;
        return air & near;
    }

    /// <summary>Per-8³-brick "any opaque" / "any non-opaque" bits from a chunk's packed words. Each word is one
    /// x-row, so a brick's x-slice of a row is one byte of it.</summary>
    private static (ulong solid, ulong air) BrickMasks(uint[] words)
    {
        ulong solid = 0, air = 0;
        for (int lz = 0; lz < S; lz++)
        for (int ly = 0; ly < S; ly++)
        {
            uint w = words[ly + S * lz];
            int rowBase = 4 * ((ly >> 3) + 4 * (lz >> 3));
            for (int bx = 0; bx < 4; bx++)
            {
                uint b = (w >> (bx * 8)) & 0xFFu;
                if (b != 0)    solid |= 1UL << (rowBase + bx);
                if (b != 0xFF) air   |= 1UL << (rowBase + bx);
            }
        }
        return (solid, air);
    }

    // ── Records and table sections ────────────────────────────────────────────

    private ChunkRecord? EnsureRecord(GridHandle g, ChunkPosition pos, bool isVirtual = false)
    {
        if (g.Chunks.TryGetValue(pos, out var rec)) return rec;

        rec = new ChunkRecord { Pos = pos, Virtual = isVirtual, Air = isVirtual ? ulong.MaxValue : 0UL };
        if (g.IsWorld)
        {
            int cell = WorldCell(pos);
            if (_worldCells.TryGetValue(cell, out var taken))
            {
                Console.WriteLine($"[grid-store] chunk {pos} wraps onto loaded chunk {taken.Pos} in the world index; not stored.");
                return null;
            }
            if (_worldFree.Count == 0 && _worldNext == _worldCapacity) GrowWorldBlock(g);
            rec.WorldSlot = _worldFree.Count > 0 ? _worldFree.Pop() : _worldNext++;
            rec.WorldCell = cell;
            rec.TableIndex = g.TableBase + rec.WorldSlot;
            _worldCells[cell] = rec;
            WriteWorldIndex(cell);
        }
        else
        {
            if (!SectionCovers(g, pos)) GrowShipSection(g, pos);
            rec.TableIndex = TableIndex(g, pos);
        }
        g.Chunks[pos] = rec;
        ExtendBox(g, pos);
        WriteChunkEntry(rec);
        return rec;
    }

    private int WorldCell(ChunkPosition p)
        => Wrap(p.X, _worldDim) + _worldDim * (Wrap(p.Y, WorldLayers) + WorldLayers * Wrap(p.Z, _worldDim));

    /// <summary>Writes the world index word holding <paramref name="cell"/> (two cells to a word).</summary>
    private void WriteWorldIndex(int cell)
    {
        int pair = cell & ~1;
        uint lo = _worldCells.TryGetValue(pair, out var a) ? (uint)a.WorldSlot : NoWorldSlot;
        uint hi = _worldCells.TryGetValue(pair + 1, out var b) ? (uint)b.WorldSlot : NoWorldSlot;
        Span<uint> word = stackalloc uint[1] { lo | hi << 16 };
        ChunkTable.Write<uint>(WorldIndexOffset + (ulong)pair * 2, word);
    }

    /// <summary>Moves the world's block of entries to one twice the size. Offsets into it stay the same, so the world
    /// index doesn't change; only the entries move.</summary>
    private void GrowWorldBlock(GridHandle g)
    {
        if (_worldCapacity >= NoWorldSlot)
            throw new InvalidOperationException($"More than {NoWorldSlot} world chunks loaded: the world index's 16-bit offsets can't address them.");
        int cap = System.Math.Min(_worldCapacity * 2, NoWorldSlot);
        Console.WriteLine($"[grid-store] world block grown to {cap} entries");
        int oldBase = g.TableBase, oldCap = _worldCapacity;
        g.TableBase = AllocRange(cap);
        _worldCapacity = cap;
        foreach (var rec in g.Chunks.Values)
        {
            rec.TableIndex = g.TableBase + rec.WorldSlot;
            WriteChunkEntry(rec);
            WriteBrickRun(rec);
        }
        FreeRange(oldBase, oldCap);
    }

    private void FreeRecord(GridHandle g, ChunkRecord rec)
    {
        FreeOcc(rec);
        if (rec.BrickSlots != null)
        {
            for (int b = 0; b < 64; b++)
                if (rec.BrickSlots[b] >= 0) FreeLight(rec.BrickSlots[b]);
            rec.BrickSlots = null;
        }
        rec.Surface = 0;
        // Tag the entry unused so a lookup of this position (or of whatever wraps onto it) reads unloaded.
        Span<int> e = stackalloc int[8] { OccUnloaded, UnusedTag, UnusedTag, UnusedTag, 0, 0, 0, 0 };
        ChunkTable.Write<int>((ulong)rec.TableIndex * ChunkEntryBytes, e);
        Array.Fill(_brickRun, NoSurface);
        BrickTable.Write<uint>((ulong)rec.TableIndex * 64 * 4, _brickRun);

        if (rec.WorldCell >= 0)
        {
            _worldCells.Remove(rec.WorldCell);
            WriteWorldIndex(rec.WorldCell);
            _worldFree.Push(rec.WorldSlot);
            rec.WorldCell = rec.WorldSlot = -1;
        }
    }

    private static int TableIndex(GridHandle g, ChunkPosition p)
        => g.TableBase + Wrap(p.X, g.TDX) + g.TDX * (Wrap(p.Y, g.TDY) + g.TDY * Wrap(p.Z, g.TDZ));

    private static int Wrap(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }

    private static bool SectionCovers(GridHandle g, ChunkPosition p)
    {
        if (g.TableBase < 0) return false;
        if (!g.HasBox) return true;
        return System.Math.Max(g.BoxMax.X, p.X) - System.Math.Min(g.BoxMin.X, p.X) < g.TDX &&
               System.Math.Max(g.BoxMax.Y, p.Y) - System.Math.Min(g.BoxMin.Y, p.Y) < g.TDY &&
               System.Math.Max(g.BoxMax.Z, p.Z) - System.Math.Min(g.BoxMin.Z, p.Z) < g.TDZ;
    }

    /// <summary>Gives a ship a section big enough for its chunk box plus <paramref name="p"/>, with slack, and
    /// moves its entries there.</summary>
    private void GrowShipSection(GridHandle g, ChunkPosition p)
    {
        var mn = g.HasBox ? Min(g.BoxMin, p) : p;
        var mx = g.HasBox ? Max(g.BoxMax, p) : p;
        const int Slack = 4;
        int dx = mx.X - mn.X + 1 + Slack, dy = mx.Y - mn.Y + 1 + Slack, dz = mx.Z - mn.Z + 1 + Slack;

        if (g.TableBase >= 0) FreeSection(g);
        AllocateSection(g, dx, dy, dz);
        foreach (var rec in g.Chunks.Values)
        {
            rec.TableIndex = TableIndex(g, rec.Pos);
            WriteChunkEntry(rec);
            WriteBrickRun(rec);
        }
    }

    private void AllocateSection(GridHandle g, int dx, int dy, int dz)
    {
        g.TableBase = AllocRange(dx * dy * dz);
        g.TDX = dx; g.TDY = dy; g.TDZ = dz;
    }

    private void FreeSection(GridHandle g)
    {
        FreeRange(g.TableBase, g.TDX * g.TDY * g.TDZ);
        g.TableBase = -1;
    }

    /// <summary>Takes <paramref name="n"/> consecutive table entries (first fit, else the end of the table, growing it),
    /// cleared to unused.</summary>
    private int AllocRange(int n)
    {
        int start = -1;
        for (int i = 0; i < _tableFree.Count; i++)
        {
            var (s, c) = _tableFree[i];
            if (c < n) continue;
            start = s;
            if (c == n) _tableFree.RemoveAt(i); else _tableFree[i] = (s + n, c - n);
            break;
        }
        if (start < 0)
        {
            if (_tableNext + n > _tableCapacity) GrowTables(System.Math.Max(_tableCapacity * 2, _tableNext + n));
            start = _tableNext;
            _tableNext += n;
        }
        ClearTableRange(start, n);
        return start;
    }

    /// <summary>Returns entries to the free list. They aren't cleared: nothing looks them up once their section is
    /// gone, and <see cref="AllocRange"/> clears them before they are used again.</summary>
    private void FreeRange(int start, int n)
    {
        // Merge with free neighbours so repeated ship/world block regrowth doesn't fragment the table.
        for (int i = 0; i < _tableFree.Count; i++)
        {
            var (s, c) = _tableFree[i];
            if (s + c == start) { start = s; n += c; _tableFree.RemoveAt(i--); }
            else if (start + n == s) { n += c; _tableFree.RemoveAt(i--); }
        }
        if (start + n == _tableNext) _tableNext = start;
        else _tableFree.Add((start, n));
    }

    private void ClearTableRange(int start, int count)
    {
        var entries = new int[count * 8];
        for (int i = 0; i < count; i++)
        {
            entries[8 * i] = OccUnloaded;
            entries[8 * i + 1] = entries[8 * i + 2] = entries[8 * i + 3] = UnusedTag;
        }
        ChunkTable.Write<int>((ulong)start * ChunkEntryBytes, entries);
        var bricks = new uint[count * 64];
        Array.Fill(bricks, NoSurface);
        BrickTable.Write<uint>((ulong)start * 64 * 4, bricks);
    }

    private void WriteChunkEntry(ChunkRecord rec)
    {
        int code = rec.OccSlot >= 0 ? rec.OccSlot
                 : rec.Solid == 0 ? OccAllAir
                 : rec.Air == 0 ? OccAllSolid
                 : OccUnloaded;
        // Second half: which 8³ bricks hold any solid, so rays can step over empty bricks whole.
        Span<int> e = stackalloc int[8] { code, rec.Pos.X, rec.Pos.Y, rec.Pos.Z,
                                          (int)(uint)rec.Solid, (int)(uint)(rec.Solid >> 32), 0, 0 };
        ChunkTable.Write<int>((ulong)rec.TableIndex * ChunkEntryBytes, e);
    }

    private void WriteBrickRun(ChunkRecord rec)
    {
        for (int b = 0; b < 64; b++)
            _brickRun[b] = rec.BrickSlots != null && rec.BrickSlots[b] >= 0 ? (uint)rec.BrickSlots[b] : NoSurface;
        BrickTable.Write<uint>((ulong)rec.TableIndex * 64 * 4, _brickRun);
    }

    private static void ExtendBox(GridHandle g, ChunkPosition p)
    {
        if (!g.HasBox) { g.BoxMin = g.BoxMax = p; g.HasBox = true; }
        else { g.BoxMin = Min(g.BoxMin, p); g.BoxMax = Max(g.BoxMax, p); }
    }

    /// <summary>Recomputes a grid's chunk box after removals (the world's shrinks as chunks unload) and the solid
    /// bounds of its bricks.</summary>
    private static void UpdateBounds(GridHandle g)
    {
        if (!g.BoxDirty) return;
        g.BoxDirty = false;
        g.HasBox = false;
        foreach (var p in g.Chunks.Keys) ExtendBox(g, p);
    }

    private static ChunkPosition Min(ChunkPosition a, ChunkPosition b) => new(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));
    private static ChunkPosition Max(ChunkPosition a, ChunkPosition b) => new(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));

    // ── Surface bricks ────────────────────────────────────────────────────────

    // Brick bit layout within a chunk: bit = bx + 4*(by + 4*bz), bricks 8³, 4 per axis.
    private const ulong BxLo = 0x1111111111111111UL, BxHi = 0x8888888888888888UL; // bricks with bx == 0 / 3
    private const ulong ByLo = 0x000F000F000F000FUL, ByHi = 0xF000F000F000F000UL; // by == 0 / 3
    private const ulong BzLo = 0x000000000000FFFFUL, BzHi = 0xFFFF000000000000UL; // bz == 0 / 3

    private static (int, int, int) FaceDir(int f) => f switch
    {
        0 => (1, 0, 0), 1 => (-1, 0, 0), 2 => (0, 1, 0), 3 => (0, -1, 0), 4 => (0, 0, 1), _ => (0, 0, -1),
    };

    private void RefreshSurfaceAround(GridHandle g, ChunkRecord rec)
    {
        RefreshSurface(g, rec);
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceDir(f);
            if (g.Chunks.TryGetValue(rec.Pos.Offset(dx, dy, dz), out var n)) RefreshSurface(g, n);
        }
        RecomputeSolidBounds(g);
    }

    /// <summary>A brick is listed if it contains air and there is solid in it or in a face-adjacent brick
    /// (including across chunk boundaries) — a conservative superset of the bricks holding a surface air voxel,
    /// since a voxel's six neighbours are all in its own brick or a face-adjacent one. Bricks that joined get a
    /// fresh light slot; bricks that left give theirs back.</summary>
    private void RefreshSurface(GridHandle g, ChunkRecord rec)
    {
        Span<ulong> neighbours = stackalloc ulong[6];
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceDir(f);
            neighbours[f] = NeighbourSolid(g, rec.Pos, dx, dy, dz);
        }
        ulong surface = SurfaceBricks(rec.Solid, rec.Air, neighbours);
        if (surface == rec.Surface) return;

        rec.BrickSlots ??= NewBrickSlots();
        ulong gone = rec.Surface & ~surface, added = surface & ~rec.Surface;
        while (gone != 0)
        {
            int b = System.Numerics.BitOperations.TrailingZeroCount(gone);
            gone &= gone - 1;
            FreeLight(rec.BrickSlots[b]);
            rec.BrickSlots[b] = -1;
        }
        while (added != 0)
        {
            int b = System.Numerics.BitOperations.TrailingZeroCount(added);
            added &= added - 1;
            rec.BrickSlots[b] = AllocLight(g, rec.Pos, b);
        }
        rec.Surface = surface;
        if (surface == 0) rec.BrickSlots = null;
        WriteBrickRun(rec);
    }

    private static ulong NeighbourSolid(GridHandle g, ChunkPosition pos, int dx, int dy, int dz)
        => g.Chunks.TryGetValue(pos.Offset(dx, dy, dz), out var n) ? n.Solid : 0UL;

    private static int[] NewBrickSlots() { var a = new int[64]; Array.Fill(a, -1); return a; }

    private static void RecomputeSolidBounds(GridHandle g)
    {
        if (g.IsWorld) return; // only ships use these (their world-space occluder bounds)
        var sMin = new Vector3D<float>(float.MaxValue);
        var sMax = new Vector3D<float>(float.MinValue);
        foreach (var rec in g.Chunks.Values)
        {
            ulong solid = rec.Solid;
            while (solid != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(solid);
                solid &= solid - 1;
                var lo = new Vector3D<float>(rec.Pos.X * S + (b & 3) * 8, rec.Pos.Y * S + ((b >> 2) & 3) * 8, rec.Pos.Z * S + (b >> 4) * 8);
                sMin = Vector3D.Min(sMin, lo);
                sMax = Vector3D.Max(sMax, lo + new Vector3D<float>(8f));
            }
        }
        g.HasSolid = sMin.X <= sMax.X;
        g.SolidMin = sMin;
        g.SolidMax = sMax;
    }

    // ── Slot allocation ───────────────────────────────────────────────────────

    private int AllocOcc()
    {
        if (_occFree.Count > 0) return _occFree.Pop();
        if (_occNext == _occCapacity)
        {
            int cap = _occCapacity * 2;
            OccPool = Grow(OccPool, (ulong)cap * WordsPerChunk * 4);
            _occCapacity = cap;
            Console.WriteLine($"[grid-store] occupancy pool grown to {cap} slots");
        }
        return _occNext++;
    }

    private void FreeOcc(ChunkRecord rec)
    {
        if (rec.OccSlot < 0) return;
        _occFree.Push(rec.OccSlot);
        rec.OccSlot = -1;
    }

    private int AllocLight(GridHandle g, ChunkPosition pos, int brick)
    {
        int slot;
        if (_lightFree.Count > 0) slot = _lightFree.Pop();
        else
        {
            if (_lightNext == _lightCapacity) GrowLight();
            slot = _lightNext++;
        }
        SlotGrid[slot] = g.Index;
        SlotChunk[slot] = pos;
        SlotBrick[slot] = (byte)brick;
        _slotListPos[slot] = g.Slots.Count;
        g.Slots.Add(slot);

        Span<int> info = stackalloc int[4] { brick | (g.Index << 6), pos.X, pos.Y, pos.Z };
        SlotInfo.Write<int>((ulong)slot * 16, info);
        LightPool.Write<uint>((ulong)slot * SlotBytes, _emptyBrick);
        NewSlots.Add(slot);
        return slot;
    }

    private void FreeLight(int slot)
    {
        if (slot < 0 || SlotGrid[slot] < 0) return;
        var g = _grids[SlotGrid[slot]]!;
        // Swap-remove from the owning grid's slot list.
        int pos = _slotListPos[slot];
        int last = g.Slots[^1];
        g.Slots[pos] = last;
        _slotListPos[last] = pos;
        g.Slots.RemoveAt(g.Slots.Count - 1);

        SlotGrid[slot] = -1;
        _lightFree.Push(slot);
    }

    private void GrowLight()
    {
        ulong maxBytes = System.Math.Min(_ctx.AdapterLimits.MaxBufferSize, _ctx.AdapterLimits.MaxStorageBufferBindingSize);
        int cap = (int)System.Math.Min((long)_lightCapacity * 2, (long)(maxBytes / SlotBytes));
        if (cap <= _lightCapacity)
            throw new InvalidOperationException($"Light pool is full at {_lightCapacity} brick slots, this device's max buffer size.");
        LightPool = Grow(LightPool, (ulong)cap * SlotBytes);
        SlotInfo  = Grow(SlotInfo, (ulong)cap * 16);
        _lightCapacity = cap;
        ResizeSlotMirror(cap);
        Console.WriteLine($"[grid-store] light pool grown to {cap} brick slots ({(ulong)cap * SlotBytes / (1024 * 1024)} MB)");
    }

    private void GrowTables(int cap)
    {
        // The world index follows the entries, so it moves to after the new ones.
        var table = GpuBuffer.CreateStorage(_ctx, (ulong)cap * ChunkEntryBytes + _worldIndexBytes);
        _ctx.CopyBufferToBuffer(ChunkTable, table, WorldIndexOffset);
        _ctx.CopyBufferToBuffer(ChunkTable, table, _worldIndexBytes, WorldIndexOffset, (ulong)cap * ChunkEntryBytes);
        ChunkTable.Dispose();
        ChunkTable = table;
        BindingVersion++;
        BrickTable = Grow(BrickTable, (ulong)cap * 64 * 4);
        int old = _tableCapacity;
        _tableCapacity = cap;
        ClearTableRange(old, cap - old);
        Console.WriteLine($"[grid-store] chunk tables grown to {cap} entries");
    }

    private void ResizeSlotMirror(int cap)
    {
        int old = SlotGrid.Length;
        Array.Resize(ref SlotGrid, cap);
        Array.Resize(ref SlotChunk, cap);
        Array.Resize(ref SlotBrick, cap);
        Array.Resize(ref _slotListPos, cap);
        for (int i = old; i < cap; i++) SlotGrid[i] = -1;
    }

    /// <summary>Replaces <paramref name="buf"/> with a bigger buffer holding its contents.</summary>
    private GpuBuffer Grow(GpuBuffer buf, ulong newSize)
    {
        var bigger = GpuBuffer.CreateStorage(_ctx, newSize);
        _ctx.CopyBufferToBuffer(buf, bigger, buf.SizeBytes);
        buf.Dispose();
        BindingVersion++;
        return bigger;
    }

    public void Dispose()
    {
        OccPool.Dispose();
        ChunkTable.Dispose();
        BrickTable.Dispose();
        LightPool.Dispose();
        SlotInfo.Dispose();
        Grids.Dispose();
    }

    /// <summary>GPU grid descriptor (176 bytes); matches WGSL <c>GridDesc</c> (bmin.w: the world index's position in
    /// the chunk table, in vec4s).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GridDesc
    {
        public Mat4 VoxelToWorld;
        public Mat4 WorldToVoxel;
        public int TableBase, TDX, TDY, TDZ;
        public int MinX, MinY, MinZ, WorldIndex;     // voxel bounds [min, max), grid space; world index (0 for ships)
        public int MaxX, MaxY, MaxZ, _unused;
    }
}
