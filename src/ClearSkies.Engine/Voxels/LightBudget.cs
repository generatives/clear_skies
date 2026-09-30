namespace ClearSkies.Engine.Voxels;

/// <summary>
/// The drawn world's budget: GPU light storage (<see cref="GridStore.WorldLightBudget"/>). What's loaded costs the
/// light bricks its surfaces hold in the store, plus a surface chunk's average for each chunk loaded but not uploaded
/// yet or still loading; and the store's world index holds at most <see cref="MaxChunks"/> chunks.
/// </summary>
public sealed class LightBudget(GridStore store) : IChunkBudget
{
    /// <summary>Light bricks counted for a chunk whose real cost the store doesn't know yet (loaded but not uploaded, or
    /// loading): a surface chunk's measured average. Buried stone and sky cost less, so this errs towards waiting for
    /// uploads to catch up rather than overshooting.</summary>
    private const int PendingBricks = 26;

    /// <summary>World chunks loaded at most: the world index's limit, less room for chunks leaving range whose storage
    /// is released a frame later.</summary>
    private const int MaxChunks = GridStore.MaxWorldChunks - 4096;

    /// <summary>Light bricks freed beyond what's needed, so the columns after the next don't each wait a frame for their
    /// own eviction.</summary>
    private const int EvictSlack = 4096;

    public bool HasRoomFor(ChunkVolume volume, int adding) =>
        volume.LoadedCount + adding <= MaxChunks &&
        Bricks(volume) + (Pending(volume) + adding) * PendingBricks <= store.WorldLightBudget;

    public ChunkCost ShortfallFor(ChunkVolume volume, int adding)
    {
        // Only once the store really is full: until uploads catch up, pending chunks are just estimates.
        int overChunks = volume.LoadedCount + adding - MaxChunks;
        int overBricks = Bricks(volume) + adding * PendingBricks - store.WorldLightBudget;
        return overChunks > 0 || overBricks >= 0 ? new ChunkCost(overChunks, overBricks + EvictSlack) : ChunkCost.None;
    }

    /// <summary>Light bricks chunk <paramref name="p"/> holds in the store (none until it is uploaded). They count as
    /// free as soon as it unloads (<see cref="GridStore.WorldBricksReleasing"/>), though the store releases them over the
    /// next frames.</summary>
    public int CostOf(ChunkVolume volume, ChunkPosition p) => store.BricksOf(volume.Gpu, p);

    public string Describe(ChunkVolume volume) =>
        $"light {Bricks(volume):N0} / {store.WorldLightBudget:N0} bricks, {Pending(volume):N0} of {volume.LoadedCount:N0} chunks not uploaded yet";

    /// <summary>The world index width that fits a view distance: wider than the span of chunks loaded at once (with a
    /// column to spare each side for the frame between a chunk leaving range and its storage being released), so two
    /// loaded chunks never share a cell.</summary>
    public static int WorldIndexDim(float viewDistance) => 2 * (int)MathF.Ceiling(viewDistance / ChunkData.Size) + 3;

    /// <summary>Light bricks the loaded chunks hold in the store (not counting unloaded ones still waiting to be
    /// released).</summary>
    private int Bricks(ChunkVolume volume) => volume.Gpu.Slots.Count - store.WorldBricksReleasing;

    /// <summary>Chunks loaded but not uploaded to the store yet.</summary>
    private int Pending(ChunkVolume volume) =>
        System.Math.Max(0, volume.LoadedCount - (store.WorldChunkCount - store.WorldChunksReleasing));
}

/// <summary>Packs each loaded chunk's opacity for the GPU store on the worker that loaded it, so the frame that uploads
/// it doesn't have to.</summary>
public sealed class LightPacking : IChunkPreparer
{
    public ChunkPreparation Prepare(ChunkData data) => GridStore.Pack(data);
}
