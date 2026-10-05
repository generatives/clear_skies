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
/// Hosting a world from its save: the Host, which has no world of its own (it keeps the save, decides what's loaded
/// and who sees what, and relays everything), and this machine's Participant, joined to it over an in-process link,
/// which has authority over every entity. With <paramref name="transport"/> off, nobody else can join (single-player).
/// </summary>
public sealed class Hosting : IDisposable
{
    /// <param name="playerName">Who plays on this machine (none: nobody, a dedicated host).</param>
    public Hosting(GameWorld world, WorldSave save, LaggedTransport? transport, string? playerName)
    {
        var link = new LoopbackNetwork();
        var (eye, yaw, pitch) = WorldSpawn.For(save.Seed);
        ulong checksum = GenerationChecksum.Compute();
        Host = new Host(new HostTransport(link.Listen(), transport), save.Database, world.Host.Clock, save.Seed, checksum,
                        (WorldSpawn.PlayerAt(eye), yaw, pitch))
        {
            SaveChunks = world.ChunkLoad.SaveAllDirty,
        };
        var participant = link.Connect();
        var welcome = Participant.Connect(participant, new Hello(ProtocolVersion.Current, playerName ?? "", checksum),
                                          TimeSpan.FromSeconds(5), () => Host.Update(0));
        Net = new Participant(participant, welcome, world.Session, world.Commands, world.Registry, world.Host.World, world.Host.Clock,
                              world.TerrainReadyFor)
        {
            OthersHere = () => Host.OthersConnected,
            Viewing = playerName is not null,
        };
    }

    public Host Host { get; }
    public Participant Net { get; }

    /// <summary>Saves everything, in one transaction (on exit): the authority describes it all, and the Host writes it.</summary>
    public void SaveAll()
    {
        Host.SaveAll();
        for (int i = 0; i < 100 && Host.Saving; i++)
        {
            Net.Transport!.Poll();
            Host.Transport.Poll();
        }
        if (Host.Saving) Console.WriteLine("[save] the authority never finished describing the world: not saved");
    }

    public void Dispose()
    {
        Net.Dispose();
        Host.Dispose();
    }
}
