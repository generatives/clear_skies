using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Net.Protocol;
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
/// Hosting a world from its save: the Host, which has no world of its own (it keeps track of who has what, and relays
/// everything), the network others join it over (<see cref="Network"/>, if any), and this machine's Participant, joined
/// to it directly, which has authority over every entity, with the save's streaming and autosave
/// (<see cref="Streaming"/> and <see cref="Saver"/>, which the game puts early in the tick, after the local player's
/// input if any). With <paramref name="transport"/> off, nobody else can join (single-player).
/// </summary>
public sealed class Hosting : IDisposable
{
    /// <param name="playerName">Who plays on this machine (none: nobody, a dedicated host).</param>
    public Hosting(GameWorld world, WorldSave save, LaggedTransport? transport, string? playerName)
    {
        var (eye, yaw, pitch) = WorldSpawn.For(save.Seed);
        ulong checksum = GenerationChecksum.Compute();
        Host = new Host(save.Database, world.Host.Clock, save.Seed, checksum, (WorldSpawn.PlayerAt(eye), yaw, pitch));
        Network = transport is null ? null : new RemoteParticipants(Host, transport);
        Net = SimulationParticipant.Join(Host, new Hello(ProtocolVersion.Current, playerName ?? "", checksum), world.Session, world.Commands,
                                         world.Registry, world.Host.World, world.Host.Clock, world.TerrainReadyFor);
        Net.OthersHere = () => Host.OthersConnected;
        Net.Viewing = playerName is not null;
        // Entities load within 1,000 blocks of a player and unload past 1,100, written to the save as they go; everything
        // is autosaved every 5 minutes and on exit, in one transaction.
        var index = new StoredEntityIndex(save.Database.ReadEntityIndex());
        Saver = new WorldSaver(world.Host.World, save.Database, index, world.Commands, Host.Ids) { SaveChunks = world.ChunkLoad.SaveAllDirty };
        Streaming = new EntityStreamingSystem(world.Host.World, save.Database, index, world.Registry, world.Commands, Saver);
    }

    public Host Host { get; }
    /// <summary>Participants on other machines, joining over the network (none: single-player). First in the tick.</summary>
    public RemoteParticipants? Network { get; }
    public SimulationParticipant Net { get; }
    public EntityStreamingSystem Streaming { get; }
    public WorldSaver Saver { get; }

    /// <summary>Saves everything, in one transaction (on exit).</summary>
    public void SaveAll() => Saver.SaveAll();

    public void Dispose()
    {
        Net.Dispose();
        Network?.Dispose();
    }
}
