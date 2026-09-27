using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Net.Sync;

/// <summary>
/// A client's chunk store: the built-on chunks come from the host. It starts with the host's list of them (in the
/// Welcome); a chunk is fetched when streaming is about to load it and kept in memory (never written to disk). An
/// edit to a chunk that isn't loaded is applied to the kept copy if there is one; otherwise the chunk just joins the
/// list, to be fetched fresh (with the edit) when it's loaded.
/// </summary>
public sealed class RemoteChunkStore : IChunkStore, IWorldChunkEditor
{
    private readonly HashSet<ChunkPosition> _edited;
    private readonly Dictionary<ChunkPosition, byte[]?> _received = new();
    private readonly HashSet<ChunkPosition> _requested = new();
    private readonly List<ChunkPosition> _toRequest = new();
    private readonly object _lock = new();

    public RemoteChunkStore(IEnumerable<ChunkPosition> edited) => _edited = new HashSet<ChunkPosition>(edited);

    public event Action<ChunkPosition>? EditedUnloaded;

    /// <summary>Chunks asked for since the last call (the session sends the requests).</summary>
    public List<ChunkPosition> TakeRequests()
    {
        lock (_lock)
        {
            var list = new List<ChunkPosition>(_toRequest);
            _toRequest.Clear();
            return list;
        }
    }

    /// <summary>The host's answer: a chunk's data (null: nothing there, generate it).</summary>
    public void Receive(ChunkPosition pos, byte[]? blob)
    {
        lock (_lock)
        {
            _requested.Remove(pos);
            _received[pos] = blob;
        }
    }

    public int Received { get { lock (_lock) return _received.Count; } }

    public IEnumerable<ChunkPosition> SavedChunks() { lock (_lock) return _edited.ToList(); }

    public bool IsReady(ChunkPosition pos) { lock (_lock) return _received.ContainsKey(pos); }

    public void Request(ChunkPosition pos)
    {
        lock (_lock)
            if (!_received.ContainsKey(pos) && _requested.Add(pos)) _toRequest.Add(pos);
    }

    public bool TryLoad(ChunkPosition pos, ChunkData data)
    {
        byte[]? blob;
        lock (_lock)
            if (!_received.TryGetValue(pos, out blob) || blob is null) return false;
        StaticWorldSerializer.Read(blob, data);
        return true;
    }

    /// <summary>A chunk unloading here with edits: keep it as it is now, so loading it again needs no fetch.</summary>
    public void Save(ChunkPosition pos, ChunkData data)
    {
        var blob = StaticWorldSerializer.ToBytes(data);
        lock (_lock)
        {
            _edited.Add(pos);
            _received[pos] = blob;
        }
    }

    public void EditUnloaded(ChunkPosition pos, Action<ChunkData> edit)
    {
        byte[]? blob;
        lock (_lock)
        {
            _edited.Add(pos);
            _received.TryGetValue(pos, out blob);
            if (blob is null)
            {
                _received.Remove(pos); // stale or never fetched: fetch it (edit included) when it's needed
                _requested.Remove(pos);
            }
        }
        if (blob != null)
        {
            var data = new ChunkData();
            StaticWorldSerializer.Read(blob, data);
            edit(data);
            lock (_lock) _received[pos] = StaticWorldSerializer.ToBytes(data);
        }
        EditedUnloaded?.Invoke(pos);
    }
}
