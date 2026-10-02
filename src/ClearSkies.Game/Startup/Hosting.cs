using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;

namespace ClearSkies.Game.Startup;

/// <summary>The world's save, Saves/Worlds/&lt;name&gt;.db, which a host plays from. A new world's seed is --seed's (1337
/// by default); after that it's the save's.</summary>
public sealed class WorldSave : IDisposable
{
    private WorldSave(SaveDatabase db, ulong seed, bool isNew)
    {
        Database = db;
        Seed = seed;
        IsNew = isNew;
    }

    public static WorldSave Open(LaunchOptions options)
    {
        var db = SaveDatabase.Open(SaveDatabase.PathFor(options.WorldName));
        bool isNew = db.Seed is null;
        ulong seed = db.Seed ?? options.NewWorldSeed;
        if (isNew) db.Seed = seed;
        Console.WriteLine($"[save] world '{options.WorldName}' ({(isNew ? "new" : "loaded")}), seed {seed}");
        return new WorldSave(db, seed, isNew);
    }

    public SaveDatabase Database { get; }
    public ulong Seed { get; }

    /// <summary>Whether the world was just made (nothing in it yet).</summary>
    public bool IsNew { get; }

    /// <summary>Where edited terrain chunks are kept.</summary>
    public IChunkStore Chunks => new DatabaseChunkStore(Database);

    public void Dispose() => Database.Dispose();
}

/// <summary>
/// Hosting a world from its save: the host session deciding everything (with <paramref name="transport"/> off,
/// nobody can join), entity IDs from the save, players known by the save, and the save's streaming and autosave
/// (<see cref="Streaming"/> and <see cref="Saver"/>, which the game puts early in the tick, after the local player's
/// input if any).
/// </summary>
public sealed class Hosting : IDisposable
{
    public Hosting(GameWorld world, WorldSave save, LaggedTransport? transport)
    {
        // Entity IDs come in blocks from the save's next free ID, so they never repeat across sessions.
        var ids = new EntityIdAllocator(save.Database.NextFreeId);
        world.Registry.RequestBlock = ids.NextBlock;
        // Entities load within 1,000 blocks of a player and unload past 1,100, written to the save as they go; everything
        // is autosaved every 5 minutes and on exit, in one transaction.
        var index = new StoredEntityIndex(save.Database.ReadEntityIndex());
        Saver = new WorldSaver(world.Host.World, save.Database, index, world.Commands, ids) { SaveChunks = world.ChunkLoad.SaveAllDirty };
        Streaming = new EntityStreamingSystem(world.Host.World, save.Database, index, world.Registry, world.Commands, Saver);
        Players = new SavedPlayers(save.Database, Saver, save.Seed);
        Net = new HostSession(transport, world.Session, world.Commands, world.Registry, world.Host.World, world.Host.Clock, ids,
                              save.Seed, GenerationChecksum.Compute(), Players, world.TerrainReadyFor);
    }

    public HostSession Net { get; }
    public EntityStreamingSystem Streaming { get; }
    public WorldSaver Saver { get; }
    public SavedPlayers Players { get; }

    /// <summary>Saves everything, in one transaction (on exit).</summary>
    public void SaveAll() => Saver.SaveAll();

    public void Dispose() => Net.Dispose();
}
