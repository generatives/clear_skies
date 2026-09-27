namespace ClearSkies.Engine.Voxels;

/// <summary>A voxel in a grid's own space: position, block and orientation.</summary>
public readonly record struct GridVoxel(int X, int Y, int Z, BlockId Id, BlockOrientation Orientation);

/// <summary>
/// Reads/writes a grid's raw non-air voxel contents in a small binary format: grid-local (possibly negative) block
/// coordinates, block ids, and (since v2) each voxel's orientation. No ECS data, position or physics state. The same
/// stream format is used by .grid files and by grid descriptions (see GridDescription), so a saved ship and a ship
/// sent to another player are the same bytes.
/// </summary>
public static class GridSerializer
{
    // "CSGD" ClearSkies Grid Data — 4 literal ASCII bytes so the format is identifiable in a hex viewer.
    private static readonly byte[] Magic = { (byte)'C', (byte)'S', (byte)'G', (byte)'D' };
    private const ushort Version = 2; // v1: (x,y,z,id). v2: + an orientation byte per voxel (older v2 files only hold 0-5, spin 0).

    /// <summary>Every non-air voxel in a volume.</summary>
    public static List<GridVoxel> Voxels(ChunkVolume grid)
    {
        var voxels = new List<GridVoxel>();
        foreach (var (pos, entry) in grid.All)
        {
            if (!entry.Data.HasAnySolid()) continue;
            var origin = pos.WorldOrigin;
            int ox = (int)origin.X, oy = (int)origin.Y, oz = (int)origin.Z;

            for (int lz = 0; lz < ChunkData.Size; lz++)
            for (int ly = 0; ly < ChunkData.Size; ly++)
            for (int lx = 0; lx < ChunkData.Size; lx++)
            {
                var id = entry.Data.Get(lx, ly, lz);
                if (id == BlockId.Air) continue;
                voxels.Add(new GridVoxel(ox + lx, oy + ly, oz + lz, id, entry.Data.GetOrientation(lx, ly, lz)));
            }
        }
        // A fixed order (chunks come from a dictionary), so the same grid always writes the same bytes.
        voxels.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        return voxels;
    }

    public static void Write(Stream stream, IReadOnlyList<GridVoxel> voxels)
    {
        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(Magic);
        bw.Write(Version);
        bw.Write(voxels.Count);
        foreach (var v in voxels)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
            bw.Write(v.Z);
            bw.Write((byte)v.Id);
            bw.Write(v.Orientation.ToByte());
        }
    }

    public static List<GridVoxel> Read(Stream stream)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not ClearSkies grid data.");

        ushort version = br.ReadUInt16();
        if (version != 1 && version != Version)
            throw new InvalidDataException($"Unsupported grid data version {version}.");

        int count = br.ReadInt32();
        if (count < 0 || count > 64 * 1024 * 1024) throw new InvalidDataException($"Grid data claims {count} voxels.");
        var voxels = new List<GridVoxel>(count);
        for (int i = 0; i < count; i++)
        {
            int x = br.ReadInt32(), y = br.ReadInt32(), z = br.ReadInt32();
            BlockId id = (BlockId)br.ReadByte();
            var orientation = version >= 2 ? BlockOrientation.FromByte(br.ReadByte()) : BlockOrientation.Upright;
            if (id != BlockId.Air) voxels.Add(new GridVoxel(x, y, z, id, orientation));
        }
        return voxels;
    }

    public static void Save(ChunkVolume grid, string filePath)
    {
        using var fs = File.Create(filePath);
        Write(fs, Voxels(grid));
    }

    public static List<GridVoxel> Load(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        try { return Read(fs); }
        catch (InvalidDataException e) { throw new InvalidDataException($"{e.Message} ({filePath})", e); }
    }
}
