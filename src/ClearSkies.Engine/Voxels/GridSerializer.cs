namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Reads/writes a ChunkVolume's raw non-air voxel contents to/from a small binary format. No ECS data,
/// spawn position, or physics state is persisted — only grid-local (possibly negative) block coordinates,
/// block ids, and (since v2) each voxel's orientation. Loaded voxel lists are handed to
/// <see cref="DynamicGridFactory.SpawnFromVoxels"/> to reconstruct a grid.
/// </summary>
public static class GridSerializer
{
    // "CSGD" ClearSkies Grid Data — 4 literal ASCII bytes so the format is identifiable in a hex viewer.
    private static readonly byte[] Magic = { (byte)'C', (byte)'S', (byte)'G', (byte)'D' };
    private const ushort Version = 2; // v1: (x,y,z,id). v2: + an orientation byte per voxel (older v2 files only hold 0-5, spin 0).

    public static void Save(ChunkVolume grid, string filePath)
    {
        var voxels = new List<(int X, int Y, int Z, byte Id, byte Orientation)>();
        foreach (var (pos, entry) in grid.All)
        {
            if (!entry.Data.HasAnySolid()) continue;
            var origin = pos.WorldOrigin;
            int ox = (int)origin.X, oy = (int)origin.Y, oz = (int)origin.Z;

            for (int lx = 0; lx < ChunkData.Size; lx++)
            for (int ly = 0; ly < ChunkData.Size; ly++)
            for (int lz = 0; lz < ChunkData.Size; lz++)
            {
                var id = entry.Data.Get(lx, ly, lz);
                if (id == BlockId.Air) continue;
                voxels.Add((ox + lx, oy + ly, oz + lz, (byte)id, entry.Data.GetOrientation(lx, ly, lz).ToByte()));
            }
        }

        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        bw.Write(Magic);
        bw.Write(Version);
        bw.Write(voxels.Count);
        foreach (var (x, y, z, id, orientation) in voxels)
        {
            bw.Write(x);
            bw.Write(y);
            bw.Write(z);
            bw.Write(id);
            bw.Write(orientation);
        }
    }

    public static List<(int X, int Y, int Z, BlockId Id, BlockOrientation Orientation)> Load(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var br = new BinaryReader(fs);

        byte[] magic = br.ReadBytes(4);
        if (!MagicMatches(magic))
            throw new InvalidDataException($"Not a ClearSkies grid file: {filePath}");

        ushort version = br.ReadUInt16();
        if (version != 1 && version != Version)
            throw new InvalidDataException($"Unsupported grid save version {version}: {filePath}");

        int count = br.ReadInt32();
        var voxels = new List<(int, int, int, BlockId, BlockOrientation)>(count);
        for (int i = 0; i < count; i++)
        {
            int x = br.ReadInt32(), y = br.ReadInt32(), z = br.ReadInt32();
            BlockId id = (BlockId)br.ReadByte();
            var orientation = version >= 2 ? BlockOrientation.FromByte(br.ReadByte()) : BlockOrientation.Upright;
            voxels.Add((x, y, z, id, orientation));
        }
        return voxels;
    }

    private static bool MagicMatches(byte[] magic)
    {
        if (magic.Length != Magic.Length) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (magic[i] != Magic[i]) return false;
        return true;
    }
}
