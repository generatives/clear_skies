using ClearSkies.Engine.ECS;
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
/// Every volume's <see cref="Root"/> entity carries a <see cref="Transform"/> placing it in the world (identity for
/// the static world, the body pose for a dynamic grid), and <see cref="Pivot"/> says which point of the volume's
/// own space sits at that Transform: world = root.Position + root.Rotation·(voxel − Pivot). Everything that maps
/// between volume space and world space (chunk placement, lighting, raycasts) goes through those two, so none of
/// it needs to know whether the volume is static or has a physics body. Volumes are rigid: root scale is ignored.
///
/// Chunk entities are <see cref="Hierarchy"/> children of <see cref="Root"/>, each at a <see cref="LocalTransform"/>
/// of its chunk origin minus the pivot, so <see cref="HierarchyTransformSystem"/> carries them along with the root
/// (and destroys them with it).
/// </summary>
public class ChunkVolume
{
    private protected readonly Dictionary<ChunkPosition, ChunkEntry> _chunks = new();
    protected readonly World _world;

    public Entity Root { get; }

    /// <summary>This volume's registration in the shared GPU voxel storage (see <see cref="GridStore"/>), kept in
    /// sync by GpuResidencySystem.</summary>
    public GridHandle Gpu { get; } = new();

    /// <summary>The point in this volume's own space that sits at <see cref="Root"/>'s <see cref="Transform"/>.
    /// Zero for the static world; a dynamic grid's centre of mass (kept in step with its body by
    /// PhysicsBodySystem), because that is where Bepu puts a compound body's origin.</summary>
    public Vector3D<float> Pivot
    {
        get => _pivot;
        internal set
        {
            if (value == _pivot) return;
            _pivot = value;
            foreach (var entry in _chunks.Values)
                if (entry.Entity.IsAlive) entry.Entity.Set(ChunkLocal(entry.Position));
        }
    }
    private Vector3D<float> _pivot;

    /// <summary>Current axis-aligned bounding box of loaded chunks (inclusive).</summary>
    internal ChunkPosition BoundsMin { get; private set; }
    internal ChunkPosition BoundsMax { get; private set; }
    private bool _boundsInitialised;

    public ChunkVolume(Entity entity, World world)
    {
        Root = entity;
        _world = world;
        if (!entity.Has<Transform>()) entity.Set(Transform.Identity);
    }

    public int  LoadedCount                => _chunks.Count;
    public bool IsLoaded(ChunkPosition pos) => _chunks.ContainsKey(pos);

    /// <summary>True if every loaded chunk is entirely air (no solid blocks anywhere in the volume).</summary>
    public bool IsEmpty()
    {
        foreach (var entry in _chunks.Values)
            if (entry.Data.HasAnySolid()) return false;
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

    public void SetBlock(int x, int y, int z, BlockId id, Facing facing = Facing.Up)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        var entry = EnsureChunk(cp);

        entry.Data.Set(lx, ly, lz, id, facing);
        entry.Entity.Set(new NeedsRemeshFlag());
        entry.Entity.Set(new NeedsRecollideFlag());
        entry.Entity.Set(new NeedsGpuUploadFlag());
        entry.PackedOpacityWords  = null; // block data actually changed -- cached opacity is stale
        entry.AddEdit(lx, ly, lz, placedSolid: BlockRegistry.Get(id).Opacity >= 15);

        // Adjacent-chunk face-cull invalidation.
        if (lx == 0)                  TryMark(cp.Offset(-1,  0,  0));
        if (lx == ChunkData.Size - 1) TryMark(cp.Offset( 1,  0,  0));
        if (ly == 0)                  TryMark(cp.Offset( 0, -1,  0));
        if (ly == ChunkData.Size - 1) TryMark(cp.Offset( 0,  1,  0));
        if (lz == 0)                  TryMark(cp.Offset( 0,  0, -1));
        if (lz == ChunkData.Size - 1) TryMark(cp.Offset( 0,  0,  1));
    }

    // ── Chunk lifecycle ────────────────────────────────────────────────────

    internal ChunkEntry AddChunk(ChunkPosition pos, ChunkData data)
    {
        var entity = _world.CreateEntity();
        var entry = new ChunkEntry(data, entity, this, pos);
        Hierarchy.SetParent(entity, Root, ChunkLocal(pos));
        entity.Set(new Chunk() { Entry = entry });
        entry.Entity.Set(new NeedsRemeshFlag());
        entry.Entity.Set(new NeedsRecollideFlag());
        entry.Entity.Set(new NeedsGpuUploadFlag());
        
        _chunks[pos] = entry;
        UpdateBounds(pos);
        MarkNeighboursDirty(pos, data);
        return entry;
    }

    public void RemoveChunk(ChunkPosition pos)
    {
        var entry = GetEntry(pos);
        if (entry is null) return;

        if (entry.Entity.IsAlive)
            entry.Entity.Dispose();

        _chunks.Remove(pos);
        MarkNeighboursDirty(pos, entry.Data);
    }

    private protected ChunkEntry EnsureChunk(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e : AddChunk(pos, new ChunkData());

    // ── Placement ──────────────────────────────────────────────────────────

    /// <summary>Chunk <paramref name="pos"/>'s entity relative to <see cref="Root"/>: its origin (where
    /// GreedyMesher's local space starts) at the chunk's minimum corner, relative to the pivot.</summary>
    private LocalTransform ChunkLocal(ChunkPosition pos)
    {
        var local = LocalTransform.Identity;
        local.Position = pos.WorldOrigin - Pivot;
        return local;
    }

    /// <summary>Maps a point in this volume's space to world space for a root at <paramref name="root"/>.</summary>
    public Vector3D<float> VoxelToWorld(in Transform root, Vector3D<float> voxel)
        => root.Position + Vec.Rotate(root.Rotation, voxel - Pivot);

    /// <summary>Inverse of <see cref="VoxelToWorld"/>. Directions map with just the inverse rotation.</summary>
    public Vector3D<float> WorldToVoxel(in Transform root, Vector3D<float> world)
        => Pivot + Vec.Rotate(Vec.Conjugate(root.Rotation), world - root.Position);

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

    /// <summary>Remesh the neighbours whose face culling changes because <paramref name="data"/> (at
    /// <paramref name="pos"/>) was just loaded or unloaded. The mesher treats a missing neighbour as open air, so a
    /// neighbour's mesh only changes where this chunk's touching face holds a solid block — most streamed chunks in
    /// a sky world are air (or air at that face), and marking all six unconditionally remeshed each chunk several
    /// times over during load-in.</summary>
    protected void MarkNeighboursDirty(ChunkPosition pos, ChunkData data)
    {
        if (FaceHasSolid(data, 0)) TryMark(pos.Offset(-1,  0,  0));
        if (FaceHasSolid(data, 1)) TryMark(pos.Offset( 1,  0,  0));
        if (FaceHasSolid(data, 2)) TryMark(pos.Offset( 0, -1,  0));
        if (FaceHasSolid(data, 3)) TryMark(pos.Offset( 0,  1,  0));
        if (FaceHasSolid(data, 4)) TryMark(pos.Offset( 0,  0, -1));
        if (FaceHasSolid(data, 5)) TryMark(pos.Offset( 0,  0,  1));
    }

    /// <summary>True if the chunk's boundary layer on side <paramref name="face"/> (0=-X, 1=+X, 2=-Y, 3=+Y, 4=-Z,
    /// 5=+Z) contains any solid block, in the mesher's sense (<see cref="BlockDef.IsFullCube"/>).</summary>
    public static bool FaceHasSolid(ChunkData data, int face)
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
            if (BlockRegistry.Get(id).IsFullCube) return true;
        }
        return false;
    }

    protected void TryMark(ChunkPosition pos)
    {
        if (_chunks.TryGetValue(pos, out var e)) {
            e.Entity.Set(new NeedsRemeshFlag());
        }
    }
}
