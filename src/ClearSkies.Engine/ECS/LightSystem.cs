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
    // Higher budget reduces the "dark chunks during loading" window. CPU init is cheap (bounded
    // 32³ BFS, no unloaded fan-out), so 16 per frame is safe even on slow machines.
    private const int InitPerFrame    = 16;
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
            // Process high-Y chunks first: a chunk above must be initialised before the chunk below
            // reads sky from it. AllByDescendingY gives us the right order; the "wait for above"
            // guard ensures correctness even when chunks at the same height are interleaved.
            int inited = 0;
            foreach (var (pos, entry) in vol.AllByDescendingY)
            {
                if (!entry.NeedsRelight) continue;

                // If the chunk above us is loaded but not yet initialised, skip this chunk for
                // now — it will be attempted again next frame once the above-chunk is done.
                // (If above is absent the column is open sky; BaseSkyLevel is the correct seed.)
                var aboveEntry = vol.GetEntry(pos.Offset(0, 1, 0));
                if (aboveEntry != null && aboveEntry.NeedsRelight) continue;

                _engine.InitializeChunk(vol, pos);
                entry.NeedsRelight = false;

                // Cross-boundary sky convergence is handled by the GPU flood (it max-relaxes sky over
                // the whole volume each cycle), so we deliberately do NOT re-queue neighbours here —
                // that only churned the mesh budget without fixing the seams.

                if (++inited >= InitPerFrame) break;
            }

            _engine.ProcessEdits(vol, RelightPerFrame);
        }
    }
}
