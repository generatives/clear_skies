using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Runs before <see cref="ChunkMeshSystem"/>. Each frame it:
///   1. Initialises light for up to <c>InitPerFrame</c> newly loaded chunks (NeedsRelight).
///   2. Drains up to <c>RelightPerFrame</c> incremental edits from each volume's RelightQueue.
/// </summary>
public sealed class LightSystem : ISystem
{
    private const int InitPerFrame    = 4;
    private const int RelightPerFrame = 16;

    private readonly List<ChunkVolume> _volumes = new();
    private readonly LightEngine       _engine  = new();

    public LightSystem(ChunkVolume initial) => _volumes.Add(initial);

    public void RegisterVolume(ChunkVolume volume)
    {
        if (!_volumes.Contains(volume)) _volumes.Add(volume);
    }

    public void UnregisterVolume(ChunkVolume volume) => _volumes.Remove(volume);

    public void Update(float dt)
    {
        foreach (var vol in _volumes)
        {
            // Process high-Y chunks first: air above initialises before the islands it overlies,
            // so island top faces already see correct sky=15 on their first mesh.
            int inited = 0;
            foreach (var (pos, entry) in vol.AllByDescendingY)
            {
                if (!entry.NeedsRelight) continue;
                _engine.InitializeChunk(vol, pos);
                entry.NeedsRelight = false;
                if (++inited >= InitPerFrame) break;
            }

            _engine.ProcessEdits(vol, RelightPerFrame);
        }
    }
}
