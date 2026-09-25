using System.Numerics;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// What streaming has learned about one region of the static world (2^shift x 2^shift chunk columns, 64 chunk layers up
/// from minY): for every chunk, whether it has been generated (or loaded) yet, and if so whether it holds any block.
/// <c>ChunkLoadSystem</c> spends its budget only on chunks that may hold something (<see cref="MaybeContent"/>), and
/// saves this to disk, so a region's empty sky is only ever discovered once. Layers the generator never fills start
/// out as known air: only a build can put something there, and saving it records it here.
///
/// Per column: two masks over the layers (bit = chunk y - minY), resolved and content. A resolved bit with no content
/// bit is known air.
/// </summary>
public sealed class RegionSurvey
{
    private const int Magic = 0x53525343; // "CSRS"
    private const int FormatVersion = 2;

    public readonly int X, Z;
    private readonly int _shift;
    private readonly int _minY;
    private readonly ulong _generated;
    private readonly ulong[] _resolved;
    private readonly ulong[] _content;

    /// <summary>Unsaved changes since the last load or save.</summary>
    public bool Dirty { get; private set; }

    /// <param name="generated">The layers (as bits) the world generator can put anything in.</param>
    public RegionSurvey(int x, int z, int shift, int minY, ulong generated)
    {
        X = x; Z = z;
        _shift = shift; _minY = minY; _generated = generated;
        _resolved = new ulong[1 << (2 * shift)];
        _content  = new ulong[1 << (2 * shift)];
    }

    private int Index(int chunkX, int chunkZ)
    {
        int m = (1 << _shift) - 1;
        return (chunkX & m) + ((chunkZ & m) << _shift);
    }

    /// <summary>Chunks of column (x, z) that may hold something: generated layers not known yet, or known to hold
    /// something.</summary>
    public ulong MaybeContent(int chunkX, int chunkZ)
    {
        int i = Index(chunkX, chunkZ);
        return (_generated & ~_resolved[i]) | _content[i];
    }

    public int KnownAirCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _resolved.Length; i++) n += BitOperations.PopCount(_resolved[i] & ~_content[i]);
            return n;
        }
    }

    /// <summary>Records what chunk <paramref name="p"/> turned out to hold. Ignored outside the 64 layers.</summary>
    public void Record(ChunkPosition p, bool hasContent)
    {
        int bit = p.Y - _minY;
        if (bit is < 0 or >= 64) return;
        int i = Index(p.X, p.Z);
        ulong b = 1UL << bit;
        bool wasResolved = (_resolved[i] & b) != 0, hadContent = (_content[i] & b) != 0;
        if (wasResolved && hadContent == hasContent) return;
        _resolved[i] |= b;
        if (hasContent) _content[i] |= b; else _content[i] &= ~b;
        Dirty = true;
    }

    public void Save(string path, string key)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Magic); w.Write(FormatVersion); w.Write(key);
        w.Write(_shift); w.Write(_minY); w.Write(_generated);
        int count = 0;
        for (int i = 0; i < _resolved.Length; i++) if (_resolved[i] != 0) count++;
        w.Write(count);
        for (int i = 0; i < _resolved.Length; i++)
        {
            if (_resolved[i] == 0) continue;
            w.Write(i); w.Write(_resolved[i]); w.Write(_content[i]);
        }
        Dirty = false;
    }

    /// <summary>Reads a survey saved by <see cref="Save"/>; null if there is none, or it was made by a different
    /// generator/seed (<paramref name="key"/>) or with a different region size or layers — it would describe
    /// different chunks.</summary>
    public static RegionSurvey? TryLoad(string path, string key, int x, int z, int shift, int minY, ulong generated)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != Magic || r.ReadInt32() != FormatVersion || r.ReadString() != key) return null;
            if (r.ReadInt32() != shift || r.ReadInt32() != minY || r.ReadUInt64() != generated) return null;
            var survey = new RegionSurvey(x, z, shift, minY, generated);
            int count = r.ReadInt32();
            for (int n = 0; n < count; n++)
            {
                int i = r.ReadInt32();
                survey._resolved[i] = r.ReadUInt64();
                survey._content[i]  = r.ReadUInt64();
            }
            return survey;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or IndexOutOfRangeException)
        {
            Console.WriteLine($"[survey] ignoring unreadable {path}: {e.Message}");
            return null;
        }
    }
}
