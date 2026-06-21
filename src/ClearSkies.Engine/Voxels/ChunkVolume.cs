using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A set of 32³ chunks with their block data, GPU meshes, and ECS entities, plus the shared
/// bookkeeping for dirty-marking and mesh handoff. Coordinates passed to <see cref="GetBlock"/> and
/// <see cref="SetBlock"/> are in this volume's own space: world space for <see cref="StaticWorld"/>,
/// grid-local space for a dynamic grid.
/// </summary>
public class ChunkVolume
{
    private protected readonly Dictionary<ChunkPosition, ChunkEntry> _chunks = new();
    protected readonly World _world;

    /// <summary>GPU-resident buffers for the entire volume. Created/resized by GpuResidencySystem.</summary>
    internal VolumeGpuResources? VolumeGpu { get; set; }

    /// <summary>Current axis-aligned bounding box of loaded chunks (inclusive).</summary>
    internal ChunkPosition BoundsMin { get; private set; }
    internal ChunkPosition BoundsMax { get; private set; }
    private bool _boundsInitialised;

    /// <summary>Block edits whose light must be recomputed. Drained by LightSystem each frame.</summary>
    internal Queue<(int x, int y, int z)> RelightQueue { get; } = new();

    public ChunkVolume(World world) => _world = world;

    public int  LoadedCount                => _chunks.Count;
    public bool IsLoaded(ChunkPosition pos) => _chunks.ContainsKey(pos);

    public ChunkData?    GetData(ChunkPosition pos) => _chunks.TryGetValue(pos, out var e) ? e.Data : null;
    internal ChunkEntry? GetEntry(ChunkPosition pos) => _chunks.TryGetValue(pos, out var e) ? e : null;
    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> All => _chunks;
    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> AllByDescendingY =>
        _chunks.OrderByDescending(kv => kv.Key.Y);

    // ── CPU light access (used by LightEngine) ─────────────────────────────

    internal (byte sky, byte block) GetLight(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var e)) return (15, 0);
        return (e.Light.GetSky(lx, ly, lz), e.Light.GetBlock(lx, ly, lz));
    }

    internal byte GetSkyLight(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return _chunks.TryGetValue(cp, out var e) ? e.Light.GetSky(lx, ly, lz) : LightEngine.BaseSkyLevel;
    }

    internal byte GetBlockLight(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return _chunks.TryGetValue(cp, out var e) ? e.Light.GetBlock(lx, ly, lz) : (byte)0;
    }

    /// <summary>Sets CPU sky light; flags the volume for GPU re-flood. No re-mesh (GPU samples buffers).</summary>
    internal bool SetSkyLight(int x, int y, int z, byte value)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var e)) return false;
        if (e.Light.GetSky(lx, ly, lz) == value) return false;
        e.Light.SetSky(lx, ly, lz, value);
        e.NeedsFlood = true;
        return true;
    }

    /// <summary>Sets CPU block light; flags the volume for GPU re-flood. No re-mesh.</summary>
    internal bool SetBlockLight(int x, int y, int z, byte value)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var e)) return false;
        if (e.Light.GetBlock(lx, ly, lz) == value) return false;
        e.Light.SetBlock(lx, ly, lz, value);
        e.NeedsFlood = true;
        return true;
    }

    // ── Block access ───────────────────────────────────────────────────────

    public BlockId GetBlock(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return GetData(cp)?.Get(lx, ly, lz) ?? BlockId.Air;
    }

    public virtual void SetBlock(int x, int y, int z, BlockId id)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var entry)) return;

        entry.Data.Set(lx, ly, lz, id);
        entry.NeedsRemesh    = true;
        entry.NeedsRecollide = true;
        entry.NeedsGpuUpload = true;
        entry.NeedsFlood     = true;
        RelightQueue.Enqueue((x, y, z));

        // Adjacent-chunk face-cull + flood invalidation.
        if (lx == 0)                  TryMarkBoth(cp.Offset(-1,  0,  0));
        if (lx == ChunkData.Size - 1) TryMarkBoth(cp.Offset( 1,  0,  0));
        if (ly == 0)                  TryMarkBoth(cp.Offset( 0, -1,  0));
        if (ly == ChunkData.Size - 1) TryMarkBoth(cp.Offset( 0,  1,  0));
        if (lz == 0)                  TryMarkBoth(cp.Offset( 0,  0, -1));
        if (lz == ChunkData.Size - 1) TryMarkBoth(cp.Offset( 0,  0,  1));
    }

    public void SetMesh(ChunkPosition pos, GpuMesh mesh)
    {
        if (!_chunks.TryGetValue(pos, out var entry)) return;
        entry.Mesh?.Dispose();
        entry.Mesh        = mesh;
        entry.NeedsRemesh = false;
        entry.Entity.Set(new MeshRenderer
        {
            Mesh      = mesh,
            VolumeGpu = VolumeGpu,
            ChunkPos  = pos,
        });
    }

    // ── Chunk lifecycle ────────────────────────────────────────────────────

    private protected ChunkEntry AddChunk(ChunkPosition pos, ChunkData data)
    {
        var entity = _world.CreateEntity();
        PlaceChunkEntity(entity, pos);
        var entry = new ChunkEntry(data, entity);
        _chunks[pos] = entry;
        UpdateBounds(pos);
        MarkNeighboursDirty(pos);
        return entry;
    }

    private protected ChunkEntry EnsureChunk(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e : AddChunk(pos, new ChunkData());

    protected virtual void PlaceChunkEntity(Entity entity, ChunkPosition pos)
    {
        var t = Transform.Identity;
        t.Position = pos.WorldOrigin;
        entity.Set(t);
    }

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

    protected void MarkNeighboursDirty(ChunkPosition pos)
    {
        TryMark(pos.Offset( 1,  0,  0)); TryMark(pos.Offset(-1,  0,  0));
        TryMark(pos.Offset( 0,  1,  0)); TryMark(pos.Offset( 0, -1,  0));
        TryMark(pos.Offset( 0,  0,  1)); TryMark(pos.Offset( 0,  0, -1));
    }

    // Re-mesh + re-flood the neighbour chunk (face-cull and light both need it).
    private void TryMarkBoth(ChunkPosition pos)
    {
        if (_chunks.TryGetValue(pos, out var e)) { e.NeedsRemesh = true; e.NeedsFlood = true; }
    }

    protected void TryMark(ChunkPosition pos)
    {
        if (_chunks.TryGetValue(pos, out var e)) e.NeedsRemesh = true;
    }

    /// <summary>Sets NeedsRemesh on all loaded chunks (used after volume resize).</summary>
    internal void MarkAllRemesh()
    {
        foreach (var (_, e) in _chunks) e.NeedsRemesh = true;
    }
}
