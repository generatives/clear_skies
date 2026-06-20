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

                // If this chunk casts a sky shadow (any bottom-face voxel < BaseSkyLevel), the
                // chunk directly below may have been initialised earlier with the wrong sky
                // assumption (BaseSkyLevel from an unloaded-above-chunk). Re-queue it so it
                // reads the now-correct sky from our bottom face.
                var belowEntry = vol.GetEntry(pos.Offset(0, -1, 0));
                if (belowEntry != null && !belowEntry.NeedsRelight && CastsSkyOcclusion(entry))
                    belowEntry.NeedsRelight = true;

                if (++inited >= InitPerFrame) break;
            }

            _engine.ProcessEdits(vol, RelightPerFrame);
        }
    }

    // Returns true if any bottom-face voxel of this chunk receives less than full outdoor sky,
    // meaning solid geometry above might be shadowing the chunk below.
    private static bool CastsSkyOcclusion(ChunkEntry entry)
    {
        int sz = ChunkData.Size;
        for (int lx = 0; lx < sz; lx++)
        for (int lz = 0; lz < sz; lz++)
            if (entry.Light.GetSky(lx, 0, lz) < LightEngine.BaseSkyLevel)
                return true;
        return false;
    }
}
