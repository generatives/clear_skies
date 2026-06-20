namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Volume-agnostic BFS light engine. Computes and incrementally updates two light channels:
///   sky   (0-15) seeded top-down; no attenuation descending through air under open sky.
///   block (0-15) seeded from emitter blocks; attenuates −1 per step in all directions.
/// Works entirely in the volume's own coordinate space.
/// </summary>
public sealed class LightEngine
{
    /// <summary>Maximum sky-light level. 15 = full sun; lower values dim the sun globally without
    /// changing attenuation behaviour (light still propagates the same way, just from a lower peak).</summary>
    public const byte BaseSkyLevel = 10;

    private static readonly (int dx, int dy, int dz)[] Dirs6 =
    [
        ( 1, 0, 0), (-1, 0, 0),
        ( 0, 1, 0), ( 0,-1, 0),
        ( 0, 0, 1), ( 0, 0,-1),
    ];

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Full sky + block light initialisation for a newly loaded chunk. Reads sky from the chunk
    /// above (or assumes 15 = open sky if unloaded) and propagates block light from any emitters.
    /// </summary>
    public void InitializeChunk(ChunkVolume vol, ChunkPosition pos)
    {
        var entry = vol.GetEntry(pos);
        if (entry == null) return;

        int sz = ChunkData.Size;
        int ox = pos.X * sz, oy = pos.Y * sz, oz = pos.Z * sz;

        // ── Sky: top-down column pass then BFS for sideways attenuation ────────
        var skyQ = new Queue<(int, int, int)>();

        // Read the chunk directly above once. If it doesn't exist or hasn't had its BFS run yet
        // (NeedsRelight=true), treat every column as open sky (level 15). This is the ONLY place
        // where an uninitialised neighbour should be assumed bright; all other boundary reads
        // (TryEnqueueSky) use the real LightData so they don't seed false sky into the BFS.
        var above = vol.GetEntry(pos.Offset(0, 1, 0));

        for (int lx = 0; lx < sz; lx++)
        for (int lz = 0; lz < sz; lz++)
        {
            int wx = ox + lx, wz = oz + lz;
            byte skyLevel = (above == null || above.NeedsRelight)
                ? BaseSkyLevel
                : above.Light.GetSky(lx, 0, lz);

            for (int ly = sz - 1; ly >= 0; ly--)
            {
                int wy = oy + ly;
                var def = BlockRegistry.Get(vol.GetBlock(wx, wy, wz));
                if (def.Opacity >= 15)
                {
                    skyLevel = 0;
                    // already 0 in freshly created LightData
                }
                else
                {
                    byte newSky = skyLevel == BaseSkyLevel ? BaseSkyLevel
                                                           : (byte)System.Math.Max(0, skyLevel - 1);
                    if (newSky > 0)
                    {
                        vol.SetSkyLight(wx, wy, wz, newSky);
                        skyQ.Enqueue((wx, wy, wz));
                    }
                    skyLevel = newSky;
                }
            }
        }

        // Seed sky from the 4 loaded horizontal neighbours only. GetSkyLight returns 15 for
        // any unloaded chunk (open-sky assumption), so seeding from unloaded sides would inject
        // sky=15 into shadow zones whose actual neighbours are occluded — producing a
        // "bright perimeter, dim centre" gradient that repeats at every chunk boundary.
        // When an unloaded neighbour eventually loads and runs its BFS, PropagateSky crosses
        // chunk boundaries automatically via SetSkyLight → MarkBorderNeighbors.
        // Bottom seeds are omitted: sunlight is top-down and must not propagate upward.
        if (vol.IsLoaded(pos.Offset(-1, 0, 0)))
            for (int ly = 0; ly < sz; ly++)
            for (int lz = 0; lz < sz; lz++)
                TryEnqueueSky(vol, ox - 1,  oy + ly, oz + lz, skyQ);

        if (vol.IsLoaded(pos.Offset(1, 0, 0)))
            for (int ly = 0; ly < sz; ly++)
            for (int lz = 0; lz < sz; lz++)
                TryEnqueueSky(vol, ox + sz, oy + ly, oz + lz, skyQ);

        if (vol.IsLoaded(pos.Offset(0, 0, -1)))
            for (int lx = 0; lx < sz; lx++)
            for (int ly = 0; ly < sz; ly++)
                TryEnqueueSky(vol, ox + lx, oy + ly, oz - 1,  skyQ);

        if (vol.IsLoaded(pos.Offset(0, 0, 1)))
            for (int lx = 0; lx < sz; lx++)
            for (int ly = 0; ly < sz; ly++)
                TryEnqueueSky(vol, ox + lx, oy + ly, oz + sz, skyQ);

        PropagateSky(vol, skyQ);

        // ── Block light: emitters in this chunk + bleed from neighbours ────────
        var blkQ = new Queue<(int, int, int, byte)>();

        for (int lx = 0; lx < sz; lx++)
        for (int ly = 0; ly < sz; ly++)
        for (int lz = 0; lz < sz; lz++)
        {
            var def = BlockRegistry.Get(entry.Data.Get(lx, ly, lz));
            if (def.LightEmission > 0)
            {
                int wx = ox + lx, wy = oy + ly, wz = oz + lz;
                vol.SetBlockLight(wx, wy, wz, def.LightEmission);
                blkQ.Enqueue((wx, wy, wz, def.LightEmission));
            }
        }

        AddBlockBoundarySeeds(vol, ox, oy, oz, sz, blkQ);
        PropagateBlock(vol, blkQ);

        entry.NeedsRemesh = true;
    }

    /// <summary>
    /// Drains up to <paramref name="budget"/> entries from <see cref="ChunkVolume.RelightQueue"/>
    /// and performs incremental remove/relight around each changed position.
    /// </summary>
    public void ProcessEdits(ChunkVolume vol, int budget)
    {
        var q = vol.RelightQueue;
        int done = 0;
        while (q.Count > 0 && done < budget)
        {
            var (x, y, z) = q.Dequeue();
            RelightRegion(vol, x, y, z);
            done++;
        }
    }

    // ── Core relight ───────────────────────────────────────────────────────────

    private const int R = 15; // max light propagation radius

    private void RelightRegion(ChunkVolume vol, int cx, int cy, int cz)
    {
        // Clear both light channels in the (2R+1)³ region.
        for (int x = cx - R; x <= cx + R; x++)
        for (int y = cy - R; y <= cy + R; y++)
        for (int z = cz - R; z <= cz + R; z++)
        {
            vol.SetSkyLight(x, y, z, 0);
            vol.SetBlockLight(x, y, z, 0);
        }

        var skyQ = new Queue<(int, int, int)>();
        var blkQ = new Queue<(int, int, int, byte)>();

        // Sky: re-bake each column inside the region from the top.
        for (int x = cx - R; x <= cx + R; x++)
        for (int z = cz - R; z <= cz + R; z++)
        {
            byte skyLevel = vol.GetSkyLight(x, cy + R + 1, z); // from above the region
            // GetSkyLight returns 15 for unloaded chunks; clamp to BaseSkyLevel for consistency.
            if (skyLevel > BaseSkyLevel) skyLevel = BaseSkyLevel;
            for (int y = cy + R; y >= cy - R; y--)
            {
                var def = BlockRegistry.Get(vol.GetBlock(x, y, z));
                if (def.Opacity >= 15)
                {
                    skyLevel = 0;
                }
                else
                {
                    byte newSky = skyLevel == BaseSkyLevel ? BaseSkyLevel
                                                           : (byte)System.Math.Max(0, skyLevel - 1);
                    if (newSky > 0 && vol.SetSkyLight(x, y, z, newSky))
                        skyQ.Enqueue((x, y, z));
                    skyLevel = newSky;
                }
            }
        }

        // Boundary sky seeds: light that bleeds in through the 4 vertical walls.
        for (int y = cy - R; y <= cy + R; y++)
        for (int z = cz - R; z <= cz + R; z++)
        {
            TryEnqueueSky(vol, cx - R - 1, y, z, skyQ);
            TryEnqueueSky(vol, cx + R + 1, y, z, skyQ);
        }
        for (int x = cx - R; x <= cx + R; x++)
        for (int y = cy - R; y <= cy + R; y++)
        {
            TryEnqueueSky(vol, x, y, cz - R - 1, skyQ);
            TryEnqueueSky(vol, x, y, cz + R + 1, skyQ);
        }

        PropagateSky(vol, skyQ);

        // Block light: re-seed from every emitter inside the region.
        for (int x = cx - R; x <= cx + R; x++)
        for (int y = cy - R; y <= cy + R; y++)
        for (int z = cz - R; z <= cz + R; z++)
        {
            var def = BlockRegistry.Get(vol.GetBlock(x, y, z));
            if (def.LightEmission > 0)
            {
                vol.SetBlockLight(x, y, z, def.LightEmission);
                blkQ.Enqueue((x, y, z, def.LightEmission));
            }
        }

        // Boundary block-light seeds: emitters outside the region whose light bleeds in.
        for (int y = cy - R; y <= cy + R; y++)
        for (int z = cz - R; z <= cz + R; z++)
        {
            TryEnqueueBlock(vol, cx - R - 1, y, z, blkQ);
            TryEnqueueBlock(vol, cx + R + 1, y, z, blkQ);
        }
        for (int x = cx - R; x <= cx + R; x++)
        for (int y = cy - R; y <= cy + R; y++)
        {
            TryEnqueueBlock(vol, x, y, cz - R - 1, blkQ);
            TryEnqueueBlock(vol, x, y, cz + R + 1, blkQ);
        }
        for (int x = cx - R; x <= cx + R; x++)
        for (int z = cz - R; z <= cz + R; z++)
        {
            TryEnqueueBlock(vol, x, cy - R - 1, z, blkQ);
            TryEnqueueBlock(vol, x, cy + R + 1, z, blkQ);
        }

        PropagateBlock(vol, blkQ);
    }

    // ── BFS propagators ────────────────────────────────────────────────────────

    private static void PropagateSky(ChunkVolume vol, Queue<(int x, int y, int z)> q)
    {
        while (q.Count > 0)
        {
            var (x, y, z) = q.Dequeue();
            byte current = vol.GetSkyLight(x, y, z);
            if (current == 0) continue;

            foreach (var (dx, dy, dz) in Dirs6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (BlockRegistry.Get(vol.GetBlock(nx, ny, nz)).Opacity >= 15) continue;

                // Full outdoor sky (BaseSkyLevel) doesn't attenuate going straight down.
                byte newLevel = (current == BaseSkyLevel && dy == -1)
                    ? BaseSkyLevel
                    : (byte)System.Math.Max(0, current - 1);

                if (newLevel > vol.GetSkyLight(nx, ny, nz))
                {
                    // Only enqueue if the chunk is loaded (SetSkyLight returns false for unloaded).
                    if (vol.SetSkyLight(nx, ny, nz, newLevel) && newLevel > 0)
                        q.Enqueue((nx, ny, nz));
                }
            }
        }
    }

    private static void PropagateBlock(ChunkVolume vol, Queue<(int x, int y, int z, byte level)> q)
    {
        while (q.Count > 0)
        {
            var (x, y, z, level) = q.Dequeue();
            if (level <= 1) continue;

            byte nextLevel = (byte)(level - 1);
            foreach (var (dx, dy, dz) in Dirs6)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (BlockRegistry.Get(vol.GetBlock(nx, ny, nz)).Opacity >= 15) continue;

                // Guard: only propagate if the neighbour has less light, and only into loaded chunks.
                // Without the SetBlockLight-returns-true guard, the BFS fans out exponentially into
                // unloaded space because GetBlockLight returns 0 for every unloaded position.
                if (nextLevel > vol.GetBlockLight(nx, ny, nz) &&
                    vol.SetBlockLight(nx, ny, nz, nextLevel))
                    q.Enqueue((nx, ny, nz, nextLevel));
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void TryEnqueueSky(
        ChunkVolume vol, int x, int y, int z,
        Queue<(int, int, int)> q)
    {
        byte level = vol.GetSkyLight(x, y, z);
        if (level > 0) q.Enqueue((x, y, z));
    }

    private static void TryEnqueueBlock(
        ChunkVolume vol, int x, int y, int z,
        Queue<(int, int, int, byte)> q)
    {
        byte level = vol.GetBlockLight(x, y, z);
        if (level > 0) q.Enqueue((x, y, z, level));
    }

    private static void AddBlockBoundarySeeds(
        ChunkVolume vol, int ox, int oy, int oz, int sz,
        Queue<(int, int, int, byte)> q)
    {
        for (int ly = 0; ly < sz; ly++)
        for (int lz = 0; lz < sz; lz++)
        {
            TryEnqueueBlock(vol, ox - 1,    oy + ly, oz + lz, q);
            TryEnqueueBlock(vol, ox + sz,   oy + ly, oz + lz, q);
        }
        for (int lx = 0; lx < sz; lx++)
        for (int ly = 0; ly < sz; ly++)
        {
            TryEnqueueBlock(vol, ox + lx, oy + ly, oz - 1,  q);
            TryEnqueueBlock(vol, ox + lx, oy + ly, oz + sz, q);
        }
        for (int lx = 0; lx < sz; lx++)
        for (int lz = 0; lz < sz; lz++)
        {
            TryEnqueueBlock(vol, ox + lx, oy - 1,  oz + lz, q);
            TryEnqueueBlock(vol, ox + lx, oy + sz, oz + lz, q);
        }
    }
}
