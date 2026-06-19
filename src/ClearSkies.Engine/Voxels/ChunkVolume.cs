using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A set of 32³ chunks with their block data, GPU meshes, and ECS entities, plus the shared
/// bookkeeping for dirty-marking and mesh handoff. Coordinates passed to <see cref="GetBlock"/> and
/// <see cref="SetBlock"/> are in this volume's own space: world space for <see cref="StaticWorld"/>,
/// grid-local space for a dynamic grid. Subclasses specialise chunk placement and (for dynamic grids)
/// grow chunks on demand.
/// </summary>
public class ChunkVolume
{
    // private protected: ChunkEntry is internal, so these can only be exposed to derived types
    // within this assembly (StaticWorld, DynamicGrid), not to subclasses in other assemblies.
    private protected readonly Dictionary<ChunkPosition, ChunkEntry> _chunks = new();
    protected readonly World _world;

    /// <summary>
    /// Block coordinates (volume-local) whose light must be recomputed. Populated by
    /// <see cref="SetBlock"/>; drained by <c>LightSystem</c> each frame.
    /// </summary>
    internal Queue<(int x, int y, int z)> RelightQueue { get; } = new();

    public ChunkVolume(World world) => _world = world;

    public int  LoadedCount               => _chunks.Count;
    public bool IsLoaded(ChunkPosition pos) => _chunks.ContainsKey(pos);

    public ChunkData? GetData(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e.Data : null;

    internal ChunkEntry? GetEntry(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e : null;

    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> All => _chunks;

    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> AllByDescendingY =>
        _chunks.OrderByDescending(kv => kv.Key.Y);

    // ── Volume-local light access (used by LightEngine and GreedyMesher sampler) ──

    /// <summary>Returns (sky, block) light at volume-local coords. Unloaded chunks → (15, 0).</summary>
    internal (byte sky, byte block) GetLight(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var e)) return (15, 0);
        return (e.Light.GetSky(lx, ly, lz), e.Light.GetBlock(lx, ly, lz));
    }

    /// <summary>Returns sky light at volume-local coords. Unloaded chunks → 15 (open sky).</summary>
    internal byte GetSkyLight(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return _chunks.TryGetValue(cp, out var e) ? e.Light.GetSky(lx, ly, lz) : (byte)15;
    }

    /// <summary>Returns block (emitter) light at volume-local coords. Unloaded chunks → 0.</summary>
    internal byte GetBlockLight(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return _chunks.TryGetValue(cp, out var e) ? e.Light.GetBlock(lx, ly, lz) : (byte)0;
    }

    /// <summary>Sets sky light; marks chunk dirty. Returns true if value changed.</summary>
    internal bool SetSkyLight(int x, int y, int z, byte value)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var e)) return false;
        if (e.Light.GetSky(lx, ly, lz) == value) return false;
        e.Light.SetSky(lx, ly, lz, value);
        e.NeedsRemesh = true;
        MarkBorderNeighbors(cp, lx, ly, lz);
        return true;
    }

    /// <summary>Sets block light; marks chunk dirty. Returns true if value changed.</summary>
    internal bool SetBlockLight(int x, int y, int z, byte value)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var e)) return false;
        if (e.Light.GetBlock(lx, ly, lz) == value) return false;
        e.Light.SetBlock(lx, ly, lz, value);
        e.NeedsRemesh = true;
        MarkBorderNeighbors(cp, lx, ly, lz);
        return true;
    }

    // When a border cell's light changes, the adjacent chunk's face cells that reference it
    // need to be remeshed so they pick up the new value.
    private void MarkBorderNeighbors(ChunkPosition cp, int lx, int ly, int lz)
    {
        int sz = ChunkData.Size;
        if (lx == 0)      MarkRemesh(cp.Offset(-1,  0,  0));
        if (lx == sz - 1) MarkRemesh(cp.Offset( 1,  0,  0));
        if (ly == 0)      MarkRemesh(cp.Offset( 0, -1,  0));
        if (ly == sz - 1) MarkRemesh(cp.Offset( 0,  1,  0));
        if (lz == 0)      MarkRemesh(cp.Offset( 0,  0, -1));
        if (lz == sz - 1) MarkRemesh(cp.Offset( 0,  0,  1));
    }

    private void MarkRemesh(ChunkPosition pos)
    {
        if (_chunks.TryGetValue(pos, out var e)) e.NeedsRemesh = true;
    }

    // ── Volume-local block access ──────────────────────────────────────────────

    public BlockId GetBlock(int x, int y, int z)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        return GetData(cp)?.Get(lx, ly, lz) ?? BlockId.Air;
    }

    /// <summary>
    /// Sets a block within an already-loaded chunk. Edits to coordinates outside any loaded chunk are
    /// ignored; dynamic grids override this to grow new chunks on demand before delegating to base.
    /// </summary>
    public virtual void SetBlock(int x, int y, int z, BlockId id)
    {
        var (cp, lx, ly, lz) = Decompose(x, y, z);
        if (!_chunks.TryGetValue(cp, out var entry)) return;

        entry.Data.Set(lx, ly, lz, id);
        entry.NeedsRemesh    = true;
        entry.NeedsRecollide = true; // collider depends only on this chunk — no neighbour propagation
        entry.NeedsGpuUpload = true; // opacity bitset must be re-uploaded for the GPU flood
        entry.NeedsFlood     = true; // block-light flood must re-run (lamp/wall changed)
        RelightQueue.Enqueue((x, y, z));

        // If the block sits on a chunk face, the adjacent chunk's border visibility changes.
        if (lx == 0)                  TryMark(cp.Offset(-1, 0, 0));
        if (lx == ChunkData.Size - 1) TryMark(cp.Offset( 1, 0, 0));
        if (ly == 0)                  TryMark(cp.Offset( 0,-1, 0));
        if (ly == ChunkData.Size - 1) TryMark(cp.Offset( 0, 1, 0));
        if (lz == 0)                  TryMark(cp.Offset( 0, 0,-1));
        if (lz == ChunkData.Size - 1) TryMark(cp.Offset( 0, 0, 1));
    }

    public void SetMesh(ChunkPosition pos, GpuMesh mesh, nint lightBindGroup)
    {
        if (!_chunks.TryGetValue(pos, out var entry)) return;

        entry.Mesh?.Dispose();
        entry.Mesh        = mesh;
        entry.NeedsRemesh = false;
        entry.Entity.Set(new MeshRenderer { Mesh = mesh, LightBindGroup = lightBindGroup });
    }

    // ── Chunk lifecycle helpers (shared) ───────────────────────────────────────

    /// <summary>Creates an entity for the chunk, positions it via <see cref="PlaceChunkEntity"/>, registers it, and dirties neighbours.</summary>
    private protected ChunkEntry AddChunk(ChunkPosition pos, ChunkData data)
    {
        var entity = _world.CreateEntity();
        PlaceChunkEntity(entity, pos);
        var entry = new ChunkEntry(data, entity);
        _chunks[pos] = entry;
        MarkNeighboursDirty(pos);
        return entry;
    }

    /// <summary>Returns the chunk at <paramref name="pos"/>, creating an empty one if absent (grow on demand).</summary>
    private protected ChunkEntry EnsureChunk(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e : AddChunk(pos, new ChunkData());

    /// <summary>Positions a freshly created chunk entity. Default places it at the world origin (no rotation); dynamic grids override.</summary>
    protected virtual void PlaceChunkEntity(Entity entity, ChunkPosition pos)
    {
        var t = Transform.Identity;
        t.Position = pos.WorldOrigin;
        entity.Set(t);
    }

    protected static (ChunkPosition cp, int lx, int ly, int lz) Decompose(int x, int y, int z)
    {
        int cx = (int)MathF.Floor((float)x / ChunkData.Size);
        int cy = (int)MathF.Floor((float)y / ChunkData.Size);
        int cz = (int)MathF.Floor((float)z / ChunkData.Size);
        return (new ChunkPosition(cx, cy, cz),
                x - cx * ChunkData.Size,
                y - cy * ChunkData.Size,
                z - cz * ChunkData.Size);
    }

    protected void MarkNeighboursDirty(ChunkPosition pos)
    {
        TryMark(pos.Offset( 1, 0, 0));
        TryMark(pos.Offset(-1, 0, 0));
        TryMark(pos.Offset( 0, 1, 0));
        TryMark(pos.Offset( 0,-1, 0));
        TryMark(pos.Offset( 0, 0, 1));
        TryMark(pos.Offset( 0, 0,-1));
    }

    protected void TryMark(ChunkPosition pos)
    {
        if (_chunks.TryGetValue(pos, out var e))
            e.NeedsRemesh = true;
    }
}
