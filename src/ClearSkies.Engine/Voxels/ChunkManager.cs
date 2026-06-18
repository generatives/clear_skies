using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Generation;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Owns all loaded chunks: their data, GPU meshes, and ECS entities.
/// Systems call Load/Unload to drive the lifecycle; SetMesh is called by the mesh system
/// once a greedy mesh has been built and uploaded.
/// </summary>
public sealed class ChunkManager
{
    private readonly Dictionary<ChunkPosition, ChunkEntry> _chunks = new();
    private readonly World _world;

    public ChunkManager(World world) => _world = world;

    public int  LoadedCount                  => _chunks.Count;
    public bool IsLoaded(ChunkPosition pos) => _chunks.ContainsKey(pos);

    public ChunkData? GetData(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e.Data : null;

    internal ChunkEntry? GetEntry(ChunkPosition pos) =>
        _chunks.TryGetValue(pos, out var e) ? e : null;

    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> All => _chunks;

    // ── World-space block access ──────────────────────────────────────────────

    public BlockId GetBlockWorld(int wx, int wy, int wz)
    {
        var (cp, lx, ly, lz) = Decompose(wx, wy, wz);
        return GetData(cp)?.Get(lx, ly, lz) ?? BlockId.Air;
    }

    public void SetBlockWorld(int wx, int wy, int wz, BlockId id)
    {
        var (cp, lx, ly, lz) = Decompose(wx, wy, wz);
        if (!_chunks.TryGetValue(cp, out var entry)) return;

        entry.Data.Set(lx, ly, lz, id);
        entry.NeedsRemesh = true;

        // If the block sits on a chunk face, the adjacent chunk's border visibility changes.
        if (lx == 0)                    TryMark(cp.Offset(-1, 0, 0));
        if (lx == ChunkData.Size - 1)   TryMark(cp.Offset( 1, 0, 0));
        if (ly == 0)                    TryMark(cp.Offset( 0,-1, 0));
        if (ly == ChunkData.Size - 1)   TryMark(cp.Offset( 0, 1, 0));
        if (lz == 0)                    TryMark(cp.Offset( 0, 0,-1));
        if (lz == ChunkData.Size - 1)   TryMark(cp.Offset( 0, 0, 1));
    }

    private static (ChunkPosition cp, int lx, int ly, int lz) Decompose(int wx, int wy, int wz)
    {
        int cx = (int)MathF.Floor((float)wx / ChunkData.Size);
        int cy = (int)MathF.Floor((float)wy / ChunkData.Size);
        int cz = (int)MathF.Floor((float)wz / ChunkData.Size);
        return (new ChunkPosition(cx, cy, cz),
                wx - cx * ChunkData.Size,
                wy - cy * ChunkData.Size,
                wz - cz * ChunkData.Size);
    }

    public void Load(ChunkPosition pos, IWorldGenerator generator)
    {
        if (_chunks.ContainsKey(pos)) return;

        var data = new ChunkData();
        generator.Generate(data, pos);
        data.IsDirty = false;

        var origin = pos.WorldOrigin;
        var entity = _world.CreateEntity();
        var t = Transform.Identity;
        t.Position = new Vector3D<float>(origin.X, origin.Y, origin.Z);
        entity.Set(t);

        _chunks[pos] = new ChunkEntry(data, entity);

        // Neighbours need remeshing: the chunk border was previously unloaded (Air)
        // and is now filled, so their exposed face set changed.
        MarkNeighboursDirty(pos);
    }

    public void Unload(ChunkPosition pos)
    {
        if (!_chunks.TryGetValue(pos, out var entry)) return;

        if (entry.Mesh is not null)
        {
            if (entry.Entity.Has<MeshRenderer>())
                entry.Entity.Remove<MeshRenderer>();
            entry.Mesh.Dispose();
        }
        if (entry.Entity.IsAlive)
            entry.Entity.Dispose();

        _chunks.Remove(pos);
        MarkNeighboursDirty(pos);
    }

    public void SetMesh(ChunkPosition pos, GpuMesh mesh)
    {
        if (!_chunks.TryGetValue(pos, out var entry)) return;

        entry.Mesh?.Dispose();
        entry.Mesh        = mesh;
        entry.NeedsRemesh = false;
        entry.Entity.Set(new MeshRenderer { Mesh = mesh });
    }

    private void MarkNeighboursDirty(ChunkPosition pos)
    {
        TryMark(pos.Offset( 1, 0, 0));
        TryMark(pos.Offset(-1, 0, 0));
        TryMark(pos.Offset( 0, 1, 0));
        TryMark(pos.Offset( 0,-1, 0));
        TryMark(pos.Offset( 0, 0, 1));
        TryMark(pos.Offset( 0, 0,-1));
    }

    private void TryMark(ChunkPosition pos)
    {
        if (_chunks.TryGetValue(pos, out var e))
            e.NeedsRemesh = true;
    }
}
