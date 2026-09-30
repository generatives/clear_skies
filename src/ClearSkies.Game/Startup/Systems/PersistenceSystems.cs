using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>
/// A host's save: entities load within 1,000 blocks of a player and unload past 1,100, written to the save as they go;
/// everything is autosaved every 5 minutes and on exit, in one transaction. Early in the tick, before gameplay.
/// </summary>
public sealed class PersistenceSystems
{
    private readonly EntityStreamingSystem _streaming;

    public PersistenceSystems(GameWorld w, SaveDatabase db, EntityIdAllocator ids)
    {
        var index = new StoredEntityIndex(db.ReadEntityIndex());
        Saver = new WorldSaver(w.Host.World, db, index, w.Commands, ids) { SaveChunks = w.ChunkLoad.SaveAllDirty };
        _streaming = new EntityStreamingSystem(w.Host.World, db, index, w.Registry, w.Commands, Saver);
    }

    public WorldSaver Saver { get; }

    public void Add(EngineHost host)
    {
        host.AddSystem(_streaming, SystemStage.Simulation);
        host.AddSystem(Saver, SystemStage.Simulation);
    }
}
