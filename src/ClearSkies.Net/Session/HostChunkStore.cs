using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;

namespace ClearSkies.Net.Session;

/// <summary>
/// A Participant's edited terrain chunks, which the Host keeps: which were edited before it joined (in its Welcome),
/// and each one's data, asked of the Host when streaming is about to load it (<see cref="IHost.RequestChunk"/>) and
/// held until it's loaded once. Every machine's, the hosting machine's included: nothing but the Host reads the save.
/// <para>The Host answers with every edit whose event came before the answer, and every later edit's event comes after
/// it, so a chunk loaded from here takes each event once. An event for a chunk held here but not loaded yet makes it
/// stale (<see cref="Outdated"/>): it's asked for again. One for a chunk already asked for needs nothing: the answer has
/// it.</para>
/// </summary>
public sealed class HostChunkStore : IChunkStore
{
    private readonly object _lock = new();
    private readonly HashSet<ChunkPosition> _asked = new();          // main thread
    private readonly Dictionary<ChunkPosition, byte[]?> _here = new(); // under _lock: workers load from it
    private ChunkPosition[] _edited = Array.Empty<ChunkPosition>();
    private IHost? _host;

    /// <summary>Asks <paramref name="host"/> from now on (anything asked for before goes now), starting with the chunks
    /// edited before this joined.</summary>
    internal void Connect(IHost host, ChunkPosition[] edited)
    {
        _host = host;
        _edited = edited;
        foreach (var pos in _asked) host.RequestChunk(pos);
    }

    /// <summary>The Host's answer (no data: never edited).</summary>
    internal void Received(in ChunkMessage chunk)
    {
        if (!_asked.Remove(chunk.Position)) return; // not asked for
        var data = chunk.Data.IsEmpty ? null : chunk.Data.ToArray();
        lock (_lock) _here[chunk.Position] = data;
    }

    /// <summary>Chunks asked for and not answered yet.</summary>
    public int Asked => _asked.Count;

    public IEnumerable<ChunkPosition> EditedChunks() => _edited;

    public bool IsReady(ChunkPosition pos)
    {
        lock (_lock) return _here.ContainsKey(pos);
    }

    public void Request(ChunkPosition pos)
    {
        if (IsReady(pos) || !_asked.Add(pos)) return;
        _host?.RequestChunk(pos);
    }

    public bool TryLoad(ChunkPosition pos, ChunkData data)
    {
        byte[]? blob;
        lock (_lock)
            if (!_here.Remove(pos, out blob) || blob is null) return false;
        StaticWorldSerializer.Read(blob, data);
        return true;
    }

    public void Outdated(ChunkPosition pos)
    {
        lock (_lock) _here.Remove(pos);
    }
}
