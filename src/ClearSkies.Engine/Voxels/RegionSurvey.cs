using System.Numerics;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// What streaming has learned about one region of the static world (2^shift x 2^shift chunk columns): for every chunk
/// in the loaded layer range, whether it has been generated (or loaded) yet, and if so whether it holds any block.
/// <c>ChunkLoadSystem</c> spends its budget only on chunks that hold something or aren't known yet, and saves this to
/// disk, so a region's empty sky is only ever discovered once. Also remembers the GPU table section size the region
/// grew to, so a revisit allocates it at that size straight away.
///
/// Per column: two masks over the layer range (bit = chunk y - minY): <see cref="Resolved"/> and
/// <see cref="Content"/>. A resolved bit with no content bit is known air.
/// </summary>
public sealed class RegionSurvey
{
    private const int Magic = 0x53525343; // "CSRS"
    private const int FormatVersion = 1;

    public readonly int X, Z;
    private readonly int _shift;
    private readonly int _minY;
    private readonly int _layers;
    private readonly ulong[] _resolved;
    private readonly ulong[] _content;

    /// <summary>Unsaved changes since the last load or save.</summary>
    public bool Dirty { get; private set; }

    /// <summary>The GPU table section size this region last had, if known.</summary>
    public Vector3D<int>? SectionSize;

    public RegionSurvey(int x, int z, int shift, int minY, int layers)
    {
        if (layers < 1 || layers > 64) throw new ArgumentOutOfRangeException(nameof(layers), "1-64 layers");
        X = x; Z = z;
        _shift = shift; _minY = minY; _layers = layers;
        _resolved = new ulong[1 << (2 * shift)];
        _content  = new ulong[1 << (2 * shift)];
    }

    public ulong LayerMask => _layers == 64 ? ulong.MaxValue : (1UL << _layers) - 1;

    private int Index(int chunkX, int chunkZ)
    {
        int m = (1 << _shift) - 1;
        return (chunkX & m) + ((chunkZ & m) << _shift);
    }

    public ulong Resolved(int chunkX, int chunkZ) => _resolved[Index(chunkX, chunkZ)];
    public ulong Content(int chunkX, int chunkZ)  => _content[Index(chunkX, chunkZ)];

    /// <summary>Chunks of column (x, z) that may hold something: not known yet, or known to hold something.</summary>
    public ulong MaybeContent(int chunkX, int chunkZ)
    {
        int i = Index(chunkX, chunkZ);
        return (~_resolved[i] | _content[i]) & LayerMask;
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

    /// <summary>Records what chunk <paramref name="p"/> turned out to hold. Ignored outside the layer range.</summary>
    public void Record(ChunkPosition p, bool hasContent)
    {
        int bit = p.Y - _minY;
        if (bit < 0 || bit >= _layers) return;
        int i = Index(p.X, p.Z);
        ulong b = 1UL << bit;
        bool wasResolved = (_resolved[i] & b) != 0, hadContent = (_content[i] & b) != 0;
        if (wasResolved && hadContent == hasContent) return;
        _resolved[i] |= b;
        if (hasContent) _content[i] |= b; else _content[i] &= ~b;
        Dirty = true;
    }

    public void MarkSaved() => Dirty = false;

    public void Save(string path, string key)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Magic); w.Write(FormatVersion); w.Write(key);
        w.Write(_shift); w.Write(_minY); w.Write(_layers);
        var size = SectionSize ?? default;
        w.Write(SectionSize.HasValue); w.Write(size.X); w.Write(size.Y); w.Write(size.Z);
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
    /// generator/seed (<paramref name="key"/>) or with a different region size or layer range — it would describe
    /// different chunks.</summary>
    public static RegionSurvey? TryLoad(string path, string key, int x, int z, int shift, int minY, int layers)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != Magic || r.ReadInt32() != FormatVersion || r.ReadString() != key) return null;
            if (r.ReadInt32() != shift || r.ReadInt32() != minY || r.ReadInt32() != layers) return null;
            var survey = new RegionSurvey(x, z, shift, minY, layers);
            bool hasSize = r.ReadBoolean();
            var size = new Vector3D<int>(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (hasSize) survey.SectionSize = size;
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
