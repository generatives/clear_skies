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

    // Chunk-table section: entry index = TableBase + wrap(c.x, TDX) + TDX*(wrap(c.y, TDY) + TDY*wrap(c.z, TDZ)),
    // the entry holding the chunk's coordinate as a tag. Toroidal for the world (it follows the camera); a ship's
    // section just covers its chunk box, with the same arithmetic.
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
}

/// <summary>
/// GPU storage shared by the static world and every ship, per the ray-traced lighting design doc:
/// <list type="bullet">
/// <item>an occupancy pool of 4 KB chunk slots (1 bit per voxel, word = ly + 32*lz, bit = lx);</item>
/// <item>a chunk table of <c>(occupancy slot or sentinel, cx, cy, cz)</c> entries, one section per grid, the
/// world's section toroidal around the camera. The coordinate is a tag, so a wrapped lookup that lands on a
/// different chunk reads as unloaded instead of aliasing onto it;</item>
/// <item>a brick table (64 entries per chunk entry) giving each 8³ brick's light slot, or <see cref="NoSurface"/>;</item>
/// <item>a light pool of 512-word brick slots, allocated only for bricks that contain a surface air voxel. One
/// u32 per voxel: bits 0-7 sun visibility (0-254, 255 = no sample), 8-15 lamp level, 16-23 AO, 24-31 bounce;</item>
/// <item>per light slot, its grid, chunk and brick (so a work list is just slot numbers);</item>
/// <item>per grid, its transforms, table section and voxel bounds.</item>
/// </list>
/// Nothing here reallocates as the camera moves: loads and unloads take and return slots, and only a pool that
/// runs out grows (by copying into a buffer twice the size, which bumps <see cref="BindingVersion"/>).
/// </summary>
public sealed class GridStore : IDisposable
{
    public const int WordsPerChunk = 1024;
    public const int VoxelsPerBrick = 512;
    public const int MaxGrids = 64;

    // Chunk-table occupancy codes (entry.x). >= 0 is an occupancy slot.
    public const int OccUnloaded = -1, OccAllAir = -2, OccAllSolid = -3;
    public const uint NoSurface = 0xFFFFFFFFu;

    /// <summary>A fresh light word: no sun sample, no lamp, no AO, no bounce — reads as ambient only.</summary>
    public const uint EmptyLightWord = 0x000000FFu;

    private const int S = ChunkData.Size;
    private const int UnusedTag = int.MinValue;

    private readonly GpuContext _ctx;

    internal GpuBuffer OccPool { get; private set; }
    internal GpuBuffer ChunkTable { get; private set; }
    internal GpuBuffer BrickTable { get; private set; }
    internal GpuBuffer LightPool { get; private set; }
    internal GpuBuffer SlotInfo { get; private set; }
    internal GpuBuffer Grids { get; }

    /// <summary>Bumped whenever a buffer above is replaced (pool growth). Bind groups over them are stale.</summary>
    public int BindingVersion { get; private set; }

    private int _occCapacity, _occNext;
    private readonly Stack<int> _occFree = new();
    private int _tableCapacity;
    private readonly List<(int start, int count)> _tableFree = new();
    private int _tableNext;
    private int _lightCapacity, _lightNext;
    private readonly Stack<int> _lightFree = new();

    // CPU mirror of each light slot's owner; -1 grid = free.
    internal int[] SlotGrid = Array.Empty<int>();
    internal ChunkPosition[] SlotChunk = Array.Empty<ChunkPosition>();
    internal byte[] SlotBrick = Array.Empty<byte>();
    private int[] _slotListPos = Array.Empty<int>();

    private readonly GridHandle?[] _grids = new GridHandle?[MaxGrids];
    private readonly GridDesc[] _descs = new GridDesc[MaxGrids];
    private readonly Vector3D<int> _worldTableDims;

    /// <summary>Light slots allocated since the lighting system last drained this. They hold
    /// <see cref="EmptyLightWord"/> and need lighting.</summary>
    internal List<int> NewSlots { get; } = new();

    /// <summary>Chunks whose occupancy changed (upload, edit or unload) since the lighting system last drained
    /// this, with whether they had or have any solid (an all-air change can't have changed any shadow).</summary>
    internal List<(GridHandle grid, ChunkPosition pos, bool solid)> ChangedChunks { get; } = new();

    public int LightSlotsInUse => _lightNext - _lightFree.Count;
    public int LightSlotCapacity => _lightCapacity;
    public int OccSlotsInUse => _occNext - _occFree.Count;
    public int OccSlotCapacity => _occCapacity;
    public int LightSlotHighWater => _lightNext;

    // Scratch.
    private readonly uint[] _brickRun = new uint[64];
    private readonly uint[] _emptyBrick;

    /// <param name="worldTableChunks">The world's toroidal table size in chunks per axis. Must be at least the
    /// span of chunks ever loaded at once, so wrapping never maps two loaded chunks to one entry.</param>
    public GridStore(GpuContext ctx, Vector3D<int> worldTableChunks)
    {
        _ctx = ctx;
        _worldTableDims = worldTableChunks;
        int worldEntries = worldTableChunks.X * worldTableChunks.Y * worldTableChunks.Z;

        _occCapacity   = worldEntries + 512;
        _tableCapacity = worldEntries + 4096;
        _lightCapacity = 32768;  // 64 MB; the 8/3 view radius settles around 17-20k surface bricks

        OccPool    = GpuBuffer.CreateStorage(ctx, (ulong)_occCapacity * WordsPerChunk * 4);
        ChunkTable = GpuBuffer.CreateStorage(ctx, (ulong)_tableCapacity * 16);
        BrickTable = GpuBuffer.CreateStorage(ctx, (ulong)_tableCapacity * 64 * 4);
        LightPool  = GpuBuffer.CreateStorage(ctx, (ulong)_lightCapacity * VoxelsPerBrick * 4);
        SlotInfo   = GpuBuffer.CreateStorage(ctx, (ulong)_lightCapacity * 16);
        Grids      = GpuBuffer.CreateStorage(ctx, (ulong)MaxGrids * (ulong)Marshal.SizeOf<GridDesc>());
        ResizeSlotMirror(_lightCapacity);

        _emptyBrick = new uint[VoxelsPerBrick];
        Array.Fill(_emptyBrick, EmptyLightWord);

        ClearTableRange(0, _tableCapacity);
        _tableNext = 0;
    }

    // ── Grid registration ─────────────────────────────────────────────────────

    internal void Register(GridHandle g, bool isWorld)
    {
        if (g.Index >= 0) return;
        int idx = Array.IndexOf(_grids, null);
        if (idx < 0) throw new InvalidOperationException($"More than {MaxGrids} voxel grids loaded.");
        _grids[idx] = g;
        g.Index = idx;
        g.IsWorld = isWorld;
        if (isWorld)
            AllocateSection(g, _worldTableDims.X, _worldTableDims.Y, _worldTableDims.Z);
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

    internal GridHandle? GridAt(int index) => index >= 0 && index < MaxGrids ? _grids[index] : null;

    /// <summary>Sets a grid's transforms for this frame's descriptor upload (see <see cref="UploadGrids"/>).</summary>
    internal void SetPose(GridHandle g, in Mat4 voxelToWorld, in Mat4 worldToVoxel)
    {
        g.VoxelToWorld = voxelToWorld;
        g.WorldToVoxel = worldToVoxel;
        g.Posed = true;
    }

    /// <summary>Descriptor count the shaders loop over (highest registered index + 1), as of the last
    /// <see cref="UploadGrids"/>.</summary>
    internal int GridCount { get; private set; }

    /// <summary>Writes every registered grid's descriptor (transforms, table section, voxel bounds). Grids not
    /// posed since the last upload get an empty table, which hides them.</summary>
    internal void UploadGrids()
    {
        int count = 0;
        for (int i = 0; i < MaxGrids; i++)
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
            g.Posed = false;
            if (g.HasBox)
            {
                d.MinX = g.BoxMin.X * S; d.MinY = g.BoxMin.Y * S; d.MinZ = g.BoxMin.Z * S;
                d.MaxX = (g.BoxMax.X + 1) * S; d.MaxY = (g.BoxMax.Y + 1) * S; d.MaxZ = (g.BoxMax.Z + 1) * S;
            }
            else { d.MinX = d.MinY = d.MinZ = 0; d.MaxX = d.MaxY = d.MaxZ = 0; }
        }
        if (count > 0) Grids.Write<GridDesc>(0, _descs.AsSpan(0, count));
        GridCount = count;
    }

    // ── Chunk upload / removal ────────────────────────────────────────────────

    /// <summary>Uploads a chunk's occupancy (packing it from block data if its cached words are stale), then
    /// updates which of its and its neighbours' bricks need light storage.</summary>
    internal void UploadChunk(GridHandle g, ChunkPosition pos, ChunkEntry entry)
    {
        if (g.Index < 0) Register(g, isWorld: false);
        var words = PackChunk(entry);

        var rec = EnsureRecord(g, pos);
        if (rec == null) return;
        bool hadSolid = rec.Solid != 0;
        rec.Virtual = false;
        rec.Solid = entry.BrickSolidMask;
        rec.Air   = entry.BrickAirMask;

        if (rec.Solid == 0 || rec.Air == 0)
        {
            FreeOcc(rec);
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
        ChangedChunks.Add((g, pos, hadSolid || rec.Solid != 0));
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
        ChangedChunks.Add((g, pos, hadSolid));
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
        var data = entry.Data;
        entry.Emitters.Clear();
        for (int lz = 0; lz < S; lz++)
        for (int ly = 0; ly < S; ly++)
        {
            uint bits = 0u;
            for (int lx = 0; lx < S; lx++)
            {
                var def = BlockRegistry.Get(data.Get(lx, ly, lz));
                if (def.Opacity >= 15) bits |= 1u << lx;
                if (def.LightEmission > 0)
                    entry.Emitters.Add(new EmitterVoxel((byte)lx, (byte)ly, (byte)lz, def.LightEmission));
            }
            words[ly + S * lz] = bits; // lx is the in-word bit, (ly + 32*lz) is the word
        }
        (entry.BrickSolidMask, entry.BrickAirMask) = BrickMasks(words);
        return words;
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

        if (!g.IsWorld && !SectionCovers(g, pos)) GrowShipSection(g, pos);
        if (g.IsWorld)
        {
            // Two loaded chunks wrapping onto one entry would corrupt each other. ChunkLoadSystem's unload radius
            // keeps the loaded span within the table, so this only fires if that invariant is ever broken.
            UpdateBounds(g);
            if (!SectionCovers(g, pos))
            {
                Console.WriteLine($"[grid-store] world chunk {pos} is outside the toroidal table's span; not stored.");
                return null;
            }
        }

        rec = new ChunkRecord { Pos = pos, Virtual = isVirtual, Air = isVirtual ? ulong.MaxValue : 0UL };
        rec.TableIndex = TableIndex(g, pos);
        g.Chunks[pos] = rec;
        ExtendBox(g, pos);
        WriteChunkEntry(rec);
        return rec;
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
        Span<int> e = stackalloc int[4] { OccUnloaded, UnusedTag, UnusedTag, UnusedTag };
        ChunkTable.Write<int>((ulong)rec.TableIndex * 16, e);
        Array.Fill(_brickRun, NoSurface);
        BrickTable.Write<uint>((ulong)rec.TableIndex * 64 * 4, _brickRun);
    }

    private int TableIndex(GridHandle g, ChunkPosition p)
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
        int n = dx * dy * dz;
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
        g.TableBase = start; g.TDX = dx; g.TDY = dy; g.TDZ = dz;
    }

    private void FreeSection(GridHandle g)
    {
        int n = g.TDX * g.TDY * g.TDZ;
        ClearTableRange(g.TableBase, n);
        _tableFree.Add((g.TableBase, n));
        g.TableBase = -1;
    }

    private void ClearTableRange(int start, int count)
    {
        var entries = new int[count * 4];
        for (int i = 0; i < count; i++)
        {
            entries[4 * i] = OccUnloaded;
            entries[4 * i + 1] = entries[4 * i + 2] = entries[4 * i + 3] = UnusedTag;
        }
        ChunkTable.Write<int>((ulong)start * 16, entries);
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
        Span<int> e = stackalloc int[4] { code, rec.Pos.X, rec.Pos.Y, rec.Pos.Z };
        ChunkTable.Write<int>((ulong)rec.TableIndex * 16, e);
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
        ulong s = rec.Solid;
        ulong near = s
            | ((s >> 1) & ~BxHi) | ((s << 1) & ~BxLo)   // solid at bx+1 / bx-1 within the chunk
            | ((s >> 4) & ~ByHi) | ((s << 4) & ~ByLo)   // by±1
            | (s >> 16) | (s << 16);                    // bz±1 (out-of-chunk bits shift out on their own)
        near |= (NeighbourSolid(g, rec.Pos,  1, 0, 0) & BxLo) << 3;   // neighbour's bx=0 borders our bx=3
        near |= (NeighbourSolid(g, rec.Pos, -1, 0, 0) & BxHi) >> 3;
        near |= (NeighbourSolid(g, rec.Pos, 0,  1, 0) & ByLo) << 12;
        near |= (NeighbourSolid(g, rec.Pos, 0, -1, 0) & ByHi) >> 12;
        near |= (NeighbourSolid(g, rec.Pos, 0, 0,  1) & BzLo) << 48;
        near |= (NeighbourSolid(g, rec.Pos, 0, 0, -1) & BzHi) >> 48;
        ulong surface = rec.Air & near;
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

        Span<int> info = stackalloc int[4] { g.Index | (brick << 8), pos.X, pos.Y, pos.Z };
        SlotInfo.Write<int>((ulong)slot * 16, info);
        LightPool.Write<uint>((ulong)slot * VoxelsPerBrick * 4, _emptyBrick);
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
        int cap = _lightCapacity * 2;
        ulong maxBytes = System.Math.Min(_ctx.AdapterLimits.MaxBufferSize, _ctx.AdapterLimits.MaxStorageBufferBindingSize);
        if ((ulong)cap * VoxelsPerBrick * 4 > maxBytes)
            throw new InvalidOperationException($"Light pool would need {cap} brick slots, over this device's max buffer size.");
        LightPool = Grow(LightPool, (ulong)cap * VoxelsPerBrick * 4);
        SlotInfo  = Grow(SlotInfo, (ulong)cap * 16);
        _lightCapacity = cap;
        ResizeSlotMirror(cap);
        Console.WriteLine($"[grid-store] light pool grown to {cap} brick slots ({(ulong)cap * VoxelsPerBrick * 4 / (1024 * 1024)} MB)");
    }

    private void GrowTables(int cap)
    {
        ChunkTable = Grow(ChunkTable, (ulong)cap * 16);
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

    /// <summary>GPU grid descriptor (176 bytes); matches WGSL <c>GridDesc</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GridDesc
    {
        public Mat4 VoxelToWorld;
        public Mat4 WorldToVoxel;
        public int TableBase, TDX, TDY, TDZ;
        public int MinX, MinY, MinZ, Pad0;   // voxel bounds [min, max), grid space
        public int MaxX, MaxY, MaxZ, Pad1;
    }
}
