using System.Numerics;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Net.Session;
using DefaultEcs;

namespace ClearSkies.Game.Startup;

/// <summary>The host's players, kept in the world's save: known by name, spawning where they left off, and saved when
/// they leave. New players spawn at the world's spawn (<see cref="WorldSpawn"/>).</summary>
public sealed class SavedPlayers : IPlayerDirectory
{
    private readonly SaveDatabase _db;
    private readonly WorldSaver _saver;

    public SavedPlayers(SaveDatabase db, WorldSaver saver, ulong seed)
    {
        _db = db;
        _saver = saver;
        var (eye, yaw, pitch) = WorldSpawn.For(seed);
        NewPlayerSpawn = (WorldSpawn.PlayerAt(eye), yaw, pitch);
    }

    public PlayerId PlayerFor(string name) => _db.PlayerFor(name);

    public PlayerDescription? Saved(PlayerId player) =>
        _db.ReadPlayer(player) is { } saved ? DescriptionBytes.Read<PlayerDescription>(saved) : null;

    public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn { get; }

    public void Leaving(Entity player) => _saver.Save([player]); // to the players table
}
