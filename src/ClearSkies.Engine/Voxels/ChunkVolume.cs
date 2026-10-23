using System.Buffers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A set of 32³ chunks with their block data, GPU meshes, and ECS entities, plus the shared
/// bookkeeping for dirty-marking and mesh handoff. Coordinates passed to <see cref="GetBlock"/> and
/// <see cref="SetBlock"/> are in this volume's own space: world space for the static volume,
/// grid-local space for a dynamic grid.
///
/// Every volume's <see cref="Root"/> entity carries a <see cref="Transform"/> placing the volume's own space in the
/// world (identity for the static world): world = root.Position + root.Rotation·voxel. Everything that maps between
/// volume space and world space (chunk placement, lighting, raycasts) goes through that, so none of it needs to know
/// whether the volume is static or has a physics body. Volumes are rigid, and the root's scale is the volume's
/// <see cref="Scale"/>: 1 except for a low-resolution copy of the world (see <see cref="LowResolutionCopy"/>). A dynamic grid's
/// body sits at its centre of mass inside that space (<see cref="PhysicsBodyComponent.Offset"/>); edits move the
/// centre of mass, never the volume's space.
///
/// Chunk entities are <see cref="Hierarchy"/> children of <see cref="Root"/>, each at a <see cref="LocalTransform"/>
/// of its chunk origin, so <see cref="HierarchyTransformSystem"/> carries them along with the root (and destroys them
/// with it).
///
/// The volume also owns block entities (<see cref="BlockDef.Components"/>): one per entity block, created when its
/// chunk is added or <see cref="SetBlock"/> places it, destroyed when <see cref="SetBlock"/> replaces it or its chunk
/// is removed. Each is a Hierarchy child of its chunk entity, placed on its cell and turned to its orientation, so it
/// rides along with the volume like the chunk does. The voxel always wins: <see cref="SetBlock"/> is the one place
/// an entity is reconciled with its voxel. Main thread only, like every entity create/destroy.
/// </summary>
public class ChunkVolume : IReplicatedChildren
{
    private protected readonly Dictionary<ChunkPosition, ChunkEntry> _chunks = new();
    protected readonly World _world;

    public Entity Root { get; }

    private readonly HashSet<Entity> _blockEntities = new();

    /// <summary>Every block entity in this volume's loaded chunks (see <see cref="BlockDef.Components"/>), kept as
    /// they're created and destroyed, so systems reach a grid's Fans, levers and so on through its
    /// <see cref="ChunkGrid"/> instead of grouping every block entity in the world by volume each tick.</summary>
    public IReadOnlyCollection<Entity> BlockEntities => _blockEntities;

    /// <summary>This volume's registration in the shared GPU voxel storage (see <see cref="GridStore"/>), kept in
    /// sync by GpuResidencySystem.</summary>
    public GridHandle Gpu { get; } = new();

    /// <summary>The chunk layers <see cref="SetBlock(int, int, int, BlockId, BlockOrientation)"/> may change
    /// (inclusive); an edit outside them does nothing. Unlimited by default. ChunkLoadSystem limits the static world to
    /// the layers it streams, since a chunk built outside them would be unloaded and never come back.</summary>
    public (int Min, int Max) EditableLayers { get; set; } = (int.MinValue, int.MaxValue);

    /// <summary>Whether this volume's chunks are meshed on their own, as if every neighbouring chunk were air: faces at
    /// chunk borders are always drawn (hidden where the neighbour is solid), so a chunk's mesh never changes when a
    /// neighbour loads, unloads or is edited. Set for the streamed world, where remeshing each chunk as its
    /// neighbouring columns arrived cost about as many meshes again as the chunks themselves, for about 13% more
    /// vertices. Ships keep culling against their neighbours: they're small and never stream. The one exception is
    /// a <see cref="BlockDef.Transparent"/> face, which is seen through, so it is culled against its neighbour across
    /// the border (or a lake or a wall of glass would show a sheet at every chunk border in it, and flicker against the
    /// stone beside it).
    /// A chunk with a transparent block on its border remeshes when the cell beside it changes (see MeshStage).</summary>
    public bool MeshIgnoresNeighbours { get; set; }

    /// <summary>Current axis-aligned bounding box of loaded chunks (inclusive).</summary>
    internal ChunkPosition BoundsMin { get; private set; }
    internal ChunkPosition BoundsMax { get; private set; }
    private bool _boundsInitialised;

    /// <summary>How many blocks a side each of this volume's voxels is: 1 for the world and ships; a power of two for a
    /// low-resolution copy of the world, whose root's Transform scale is this.</summary>
    public int Scale { get; }

    /// <summary>A low-resolution copy of the static world (see <see cref="CreateLowResolutionCopy"/>), not the world itself: its chunks
    /// hold no block entities, and it is lit as a static grid at its scale (see GridStore.RegisterStatic).</summary>
    public bool IsLowResolutionCopy { get; }

    /// <summary>For a low-resolution copy: the nearest (blocks, horizontally) any of its chunks counts as for meshing and
    /// upload order (see NearestChunks), where it starts being drawn, so the full-detail chunks nearer the view go
    /// first.</summary>
    public float RankedFrom { get; set; }

    public ChunkVolume(Entity entity, World world) : this(entity, world, 1, false) { }

    private ChunkVolume(Entity entity, World world, int scale, bool scaledCopy)
    {
        Root = entity;
        _world = world;
        Scale = scale;
        IsLowResolutionCopy = scaledCopy;
        if (!entity.Has<Transform>()) entity.Set(Transform.Identity);
    }

    /// <summary>Makes <paramref name="root"/> a low-resolution copy of the static world, its voxels <paramref name="scale"/>
    /// blocks a side: a <see cref="ChunkGrid"/> flagged <see cref="LowResolutionCopy"/>, so systems that act on blocks leave
    /// it out.</summary>
    public static ChunkVolume CreateLowResolutionCopy(Entity root, World world, int scale)
    {
        var volume = new ChunkVolume(root, world, scale, true) { MeshIgnoresNeighbours = true };
        root.Get<Transform>().Scale = new Vector3D<float>(scale);
        root.Set(new ChunkGrid { Volume = volume });
        root.Set<LowResolutionCopy>();
        return volume;
    }

    public int  LoadedCount                => _chunks.Count;

    /// <summary>How many loaded chunks hold a full block array (32 KB) rather than one uniform block.</summary>
    public int DenseCount()
    {
        int n = 0;
        foreach (var entry in _chunks.Values)
            if (!entry.Data.IsUniform(out _)) n++;
        return n;
    }
    public bool IsLoaded(ChunkPosition pos) => _chunks.ContainsKey(pos);

    /// <summary>True if every loaded chunk is entirely air (no solid blocks anywhere in the volume).</summary>
    public bool IsEmpty()
    {
        foreach (var entry in _chunks.Values)
            if (entry.Data.HasAnyNonAir()) return false;
        return true;
    }

    /// <summary>
    /// Computes the AABB (inclusive, chunk coords) of the <b>currently loaded</b> chunks. Unlike
    /// <see cref="BoundsMin"/>/<see cref="BoundsMax"/> (which only ever grow), this shrinks as chunks
    /// unload, so the GPU volume can be windowed around the camera instead of growing without bound.
    /// </summary>
    internal bool TryGetLoadedBounds(out ChunkPosition min, out ChunkPosition max)
    {
        if (_chunks.Count == 0) { min = max = default; return false; }
        int nx = int.MaxValue, ny = int.MaxValue, nz = int.MaxValue;
        int xx = int.MinValue, xy = int.MinValue, xz = int.MinValue;
        foreach (var pos in _chunks.Keys)
        {
            if (pos.X < nx) nx = pos.X; if (pos.X > xx) xx = pos.X;
            if (pos.Y < ny) ny = pos.Y; if (pos.Y > xy) xy = pos.Y;
            if (pos.Z < nz) nz = pos.Z; if (pos.Z > xz) xz = pos.Z;
        }
        min = new ChunkPosition(nx, ny, nz);
        max = new ChunkPosition(xx, xy, xz);
        return true;
    }

    public ChunkData?    GetData(ChunkPosition pos) => _chunks.TryGetValue(pos, out var e) ? e.Data : null;
    internal ChunkEntry? GetEntry(ChunkPosition pos) => _chunks.TryGetValue(pos, out var e) ? e : null;
    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> All => _chunks;

    // ── Block access ───────────────────────────────────────────────────────

    public BlockId GetBlock(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return GetData(cp)?.Get(lx, ly, lz) ?? BlockId.Air;
    }

    /// <summary>The entity of the entity block at a cell (see <see cref="BlockDef.Components"/>), if there is one and
    /// its chunk is loaded.</summary>
    public bool TryGetBlockEntity(int x, int y, int z, out Entity entity)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (GetEntry(cp)?.BlockEntities is { } entities && entities.TryGetValue(new Vector3D<int>(lx, ly, lz), out entity)
            && entity.IsAlive)
            return true;
        entity = default;
        return false;
    }

    /// <summary>Block entities replicate with the volume's root entity, each keyed by its cell (see
    /// <see cref="ReplicatedComponents"/>).</summary>
    public IEnumerable<(ulong Key, Entity Entity)> ReplicatedChildren
    {
        get
        {
            foreach (var e in _blockEntities)
                if (e.IsAlive) yield return (CellKey(e.Get<BlockRef>().Position), e);
        }
    }

    public bool TryGetReplicatedChild(ulong key, out Entity entity)
    {
        var cell = KeyCell(key);
        return TryGetBlockEntity(cell.X, cell.Y, cell.Z, out entity);
    }

    private const int KeyBits = 21; // per axis: cells within ±a million

    /// <summary>A cell as a replication key: each axis in 21 bits, with the top bit set so it's never 0 (the root).</summary>
    public static ulong CellKey(Vector3D<int> cell)
    {
        const ulong mask = (1UL << KeyBits) - 1;
        return 1UL << 63 | ((ulong)cell.X & mask) | ((ulong)cell.Y & mask) << KeyBits | ((ulong)cell.Z & mask) << (2 * KeyBits);
    }

    public static Vector3D<int> KeyCell(ulong key)
    {
        static int Axis(ulong key, int shift) => (int)((long)(key << (64 - KeyBits - shift)) >> (64 - KeyBits)); // sign-extended
        return new Vector3D<int>(Axis(key, 0), Axis(key, KeyBits), Axis(key, 2 * KeyBits));
    }

    public void SetBlock(int x, int y, int z, BlockId id) => SetBlock(x, y, z, id, BlockOrientation.Upright);

    /// <summary>Sets one block. For generation and loading only: during gameplay, blocks change through the EditVoxels
    /// command (see EditVoxelsHandler), which calls this and <see cref="FillBox"/>.</summary>
    public void SetBlock(int x, int y, int z, BlockId id, BlockOrientation orientation)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (cp.Y < EditableLayers.Min || cp.Y > EditableLayers.Max) return;
        var entry = EnsureChunk(cp);

        bool wasOpaque = entry.Data.IsAllOpaque;
        entry.Data.Set(lx, ly, lz, id, orientation);
        bool opacityChanged = entry.Data.IsAllOpaque != wasOpaque;
        SyncBlockEntity(entry, new Vector3D<int>(lx, ly, lz), id, orientation);
        entry.Entity.Set(new NeedsRecollideFlag());
        entry.Entity.Set(new NeedsGpuUploadFlag());
        entry.PackedOpacityWords  = null; // block data actually changed -- cached opacity is stale
        entry.AddEdit(lx, ly, lz, placedSolid: BlockRegistry.Get(id).BlocksLight);

        // Which of its faces it changed on, for what's meshed against its neighbours (see ChunkPipelineSystem).
        const int L = ChunkData.Size - 1;
        int faces = (lx == 0 ? 1 : 0) | (lx == L ? 2 : 0) | (ly == 0 ? 4 : 0) | (ly == L ? 8 : 0) | (lz == 0 ? 16 : 0) | (lz == L ? 32 : 0);
        _world.Publish(new ChunkBlocksChanged(entry.Entity, faces, opacityChanged));
    }

    /// <summary>The orientation of the block at a cell (<see cref="BlockOrientation.Upright"/> where nothing is loaded).</summary>
    public BlockOrientation GetOrientation(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return GetData(cp)?.GetOrientation(lx, ly, lz) ?? BlockOrientation.Upright;
    }

    /// <summary>Sets every cell in the box from <paramref name="min"/> to <paramref name="max"/> (inclusive) to one
    /// block: the fast path for a box edit, touching each chunk's flags once instead of once per block. Cells that
    /// already hold that block (and orientation) are left alone, so their block entities keep their state. Clearing
    /// to air doesn't create chunks that aren't there.</summary>
    public void FillBox(Vector3D<int> min, Vector3D<int> max, BlockId id, BlockOrientation orientation)
    {
        const int S = ChunkData.Size;
        var (lo, _, _, _) = Decompose(min.X, min.Y, min.Z);
        var (hi, _, _, _) = Decompose(max.X, max.Y, max.Z);
        bool placedSolid = BlockRegistry.Get(id).BlocksLight;
        for (int cz = lo.Z; cz <= hi.Z; cz++)
        for (int cy = lo.Y; cy <= hi.Y; cy++)
        for (int cx = lo.X; cx <= hi.X; cx++)
        {
            var cp = new ChunkPosition(cx, cy, cz);
            if (cp.Y < EditableLayers.Min || cp.Y > EditableLayers.Max) continue;
            var entry = id == BlockId.Air ? GetEntry(cp) : EnsureChunk(cp);
            if (entry is null) continue;

            int x0 = System.Math.Max(min.X - cx * S, 0), x1 = System.Math.Min(max.X - cx * S, S - 1);
            int y0 = System.Math.Max(min.Y - cy * S, 0), y1 = System.Math.Min(max.Y - cy * S, S - 1);
            int z0 = System.Math.Max(min.Z - cz * S, 0), z1 = System.Math.Min(max.Z - cz * S, S - 1);
            bool changed = false, wasOpaque = entry.Data.IsAllOpaque;
            for (int lz = z0; lz <= z1; lz++)
            for (int ly = y0; ly <= y1; ly++)
            for (int lx = x0; lx <= x1; lx++)
            {
                if (entry.Data.Get(lx, ly, lz) == id && entry.Data.GetOrientation(lx, ly, lz) == orientation) continue;
                entry.Data.Set(lx, ly, lz, id, orientation);
                SyncBlockEntity(entry, new Vector3D<int>(lx, ly, lz), id, orientation);
                entry.AddEdit(lx, ly, lz, placedSolid);
                changed = true;
            }
            if (!changed) continue;

            bool opacityChanged = entry.Data.IsAllOpaque != wasOpaque;
            entry.Entity.Set(new NeedsRecollideFlag());
            entry.Entity.Set(new NeedsGpuUploadFlag());
            entry.PackedOpacityWords = null;
            int faces = (x0 == 0 ? 1 : 0) | (x1 == S - 1 ? 2 : 0) | (y0 == 0 ? 4 : 0) | (y1 == S - 1 ? 8 : 0)
                      | (z0 == 0 ? 16 : 0) | (z1 == S - 1 ? 32 : 0);
            _world.Publish(new ChunkBlocksChanged(entry.Entity, faces, opacityChanged));
        }
    }

    // ── Chunk lifecycle ────────────────────────────────────────────────────

    /// <summary>True for the static world: each of its chunks gets <see cref="OwnPresence"/>.</summary>
    public bool ChunksOwnPresence { get; init; }

    /// <param name="prepared">What was made of the chunk's data off the main thread as it loaded (its opacity packed
    /// for the GPU store, say), if anything; otherwise whatever needs it makes it later.</param>
    internal ChunkEntry AddChunk(ChunkPosition pos, ChunkData data, ChunkPreparation? prepared = null)
    {
        var entity = _world.CreateEntity();
        var entry = new ChunkEntry(data, entity, this, pos);
        prepared?.ApplyTo(entry);
        // The world's chunks spread across the whole world, so each decides its own presence layers (see
        // EntityPresenceSystem); a grid's chunks inherit the grid's.
        if (ChunksOwnPresence) entity.Set<OwnPresence>();
        Hierarchy.SetParent(entity, Root, ChunkLocal(pos));
        entity.Set(new Chunk() { Entry = entry });
        entry.Entity.Set(new NeedsRecollideFlag());
        entry.Entity.Set(new NeedsGpuUploadFlag());
        if (!IsLowResolutionCopy) CreateBlockEntities(entry);

        _chunks[pos] = entry;
        UpdateBounds(pos);
        return entry;
    }

    public void RemoveChunk(ChunkPosition pos)
    {
        var entry = GetEntry(pos);
        if (entry is null) return;

        // Takes the chunk's block entities with it, immediately.
        if (entry.BlockEntities is { } entities)
            foreach (var e in entities.Values) _blockEntities.Remove(e);
        Hierarchy.DestroyRecursive(entry.Entity);
        entry.BlockEntities = null;

        _chunks.Remove(pos);
    }

    private protected ChunkEntry EnsureChunk(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e : AddChunk(pos, new ChunkData());

    // ── Block entities ─────────────────────────────────────────────────────

    // Entity block ids as raw bytes, for a vectorized search of a freshly added chunk's block array.
    private static readonly SearchValues<byte> EntityBlockBytes = SearchValues.Create(
        Enumerable.Range(0, 256).Where(i => BlockRegistry.Get((BlockId)i).IsEntityBlock).Select(i => (byte)i).ToArray());

    /// <summary>Creates an entity for every entity block in a chunk that was just added.</summary>
    private void CreateBlockEntities(ChunkEntry entry)
    {
        var blocks = entry.Data.BlocksAsBytes();
        int i = 0;
        while (true)
        {
            int found = blocks[i..].IndexOfAny(EntityBlockBytes);
            if (found < 0) return;
            i += found;
            int x = i % ChunkData.Size, y = i / ChunkData.Size % ChunkData.Size, z = i / (ChunkData.Size * ChunkData.Size);
            CreateBlockEntity(entry, new Vector3D<int>(x, y, z), (BlockId)blocks[i], entry.Data.GetOrientation(x, y, z));
            i++;
        }
    }

    /// <summary>Makes the block entity at <paramref name="cell"/> agree with the voxel just set there: keeps it if
    /// the same block type and orientation was set again (so its state survives), otherwise destroys it and creates a
    /// fresh one if the new block is an entity block.</summary>
    private void SyncBlockEntity(ChunkEntry entry, Vector3D<int> cell, BlockId id, BlockOrientation orientation)
    {
        if (entry.BlockEntities is { } entities && entities.Remove(cell, out var existing))
        {
            _blockEntities.Remove(existing);
            if (existing.IsAlive)
            {
                ref readonly var r = ref existing.Get<BlockRef>();
                if (r.Id == id && r.Orientation == orientation) { entities[cell] = existing; _blockEntities.Add(existing); return; }
                Hierarchy.DestroyRecursive(existing);
            }
        }

        if (BlockRegistry.Get(id).IsEntityBlock)
            CreateBlockEntity(entry, cell, id, orientation);
    }

    private void CreateBlockEntity(ChunkEntry entry, Vector3D<int> cell, BlockId id, BlockOrientation orientation)
    {
        var e = _world.CreateEntity();
        e.Set(new BlockRef
        {
            Volume      = this,
            Position    = new Vector3D<int>(entry.Position.X, entry.Position.Y, entry.Position.Z) * ChunkData.Size + cell,
            Orientation = orientation,
            Id          = id,
        });
        Hierarchy.SetParent(e, entry.Entity, CellLocal(cell, orientation));
        BlockRegistry.Get(id).Components!(e);
        (entry.BlockEntities ??= new())[cell] = e;
        _blockEntities.Add(e);
    }

    /// <summary>A block entity relative to its chunk entity: the same placement ChunkRenderSystem gives a static
    /// model block — the model turned to the orientation about the cell centre, standing on the cell face opposite
    /// its top.</summary>
    private static LocalTransform CellLocal(Vector3D<int> cell, BlockOrientation orientation)
    {
        var rotation = orientation.Rotation;
        var local = LocalTransform.Identity;
        local.Rotation = rotation;
        local.Position = new Vector3D<float>(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f)
                       + Vec.Rotate(rotation, new Vector3D<float>(0f, -0.5f, 0f));
        return local;
    }

    // ── Placement ──────────────────────────────────────────────────────────

    /// <summary>Chunk <paramref name="pos"/>'s entity relative to <see cref="Root"/>: its origin (where
    /// GreedyMesher's local space starts) at the chunk's minimum corner.</summary>
    private static LocalTransform ChunkLocal(ChunkPosition pos)
    {
        var local = LocalTransform.Identity;
        local.Position = pos.WorldOrigin;
        return local;
    }

    /// <summary>Maps a point in this volume's space to world space for a root at <paramref name="root"/>.</summary>
    public Vector3D<float> VoxelToWorld(in Transform root, Vector3D<float> voxel)
        => root.Position + Vec.Rotate(root.Rotation, voxel * (float)Scale);

    /// <summary>Inverse of <see cref="VoxelToWorld"/>. Directions map with just the inverse rotation.</summary>
    public Vector3D<float> WorldToVoxel(in Transform root, Vector3D<float> world)
        => Vec.Rotate(Vec.Conjugate(root.Rotation), world - root.Position) / (float)Scale;

    // ── Helpers ────────────────────────────────────────────────────────────

    protected static (ChunkPosition cp, int lx, int ly, int lz) Decompose(int x, int y, int z)
    {
        int cx = (int)MathF.Floor((float)x / ChunkData.Size);
        int cy = (int)MathF.Floor((float)y / ChunkData.Size);
        int cz = (int)MathF.Floor((float)z / ChunkData.Size);
        return (new ChunkPosition(cx, cy, cz),
                x - cx * ChunkData.Size, y - cy * ChunkData.Size, z - cz * ChunkData.Size);
    }

    private void UpdateBounds(ChunkPosition pos)
    {
        if (!_boundsInitialised)
        {
            BoundsMin = BoundsMax = pos;
            _boundsInitialised = true;
        }
        else
        {
            BoundsMin = new ChunkPosition(
                System.Math.Min(BoundsMin.X, pos.X),
                System.Math.Min(BoundsMin.Y, pos.Y),
                System.Math.Min(BoundsMin.Z, pos.Z));
            BoundsMax = new ChunkPosition(
                System.Math.Max(BoundsMax.X, pos.X),
                System.Math.Max(BoundsMax.Y, pos.Y),
                System.Math.Max(BoundsMax.Z, pos.Z));
        }
    }

    /// <summary>True if the chunk's boundary layer on side <paramref name="face"/> (0=-X, 1=+X, 2=-Y, 3=+Y, 4=-Z,
    /// 5=+Z) contains any block that can hide a neighbour's face (<see cref="BlockDef.HidesFaces"/>).</summary>
    public static bool FaceHasSolid(ChunkData data, int face) => FaceHasAny(data, face, transparentOnly: false);

    /// <summary>True if the chunk's boundary layer on side <paramref name="face"/> (as <see cref="FaceHasSolid"/>)
    /// contains any <see cref="BlockDef.Transparent"/> block.</summary>
    public static bool FaceHasTransparent(ChunkData data, int face) => FaceHasAny(data, face, transparentOnly: true);

    private static bool FaceHasAny(ChunkData data, int face, bool transparentOnly)
    {
        int s = ChunkData.Size, layer = (face & 1) == 0 ? 0 : s - 1;
        for (int a = 0; a < s; a++)
        for (int b = 0; b < s; b++)
        {
            var id = (face >> 1) switch
            {
                0 => data.Get(layer, a, b),
                1 => data.Get(a, layer, b),
                _ => data.Get(a, b, layer),
            };
            ref readonly var def = ref BlockRegistry.Get(id);
            if (transparentOnly ? def.IsFullCube && def.Transparent : def.HidesFaces) return true;
        }
        return false;
    }
}
