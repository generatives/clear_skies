using System.Collections.Generic;
using System.IO;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Reads/writes a DynamicGrid's raw non-air voxel contents to/from a small binary format. No ECS data,
/// spawn position, or physics state is persisted — only grid-local (possibly negative) block coordinates
/// and block ids. Loaded voxel lists are handed to <see cref="DynamicGridFactory.SpawnFromVoxels"/> to
/// reconstruct a grid.
/// </summary>
public static class DynamicGridSerializer
{
    // "CSGD" ClearSkies Grid Data — 4 literal ASCII bytes so the format is identifiable in a hex viewer.
    private static readonly byte[] Magic = { (byte)'C', (byte)'S', (byte)'G', (byte)'D' };
    private const ushort Version = 1;

    public static void Save(DynamicGrid grid, string filePath)
    {
        var voxels = new List<(int X, int Y, int Z, byte Id)>();
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
                voxels.Add((ox + lx, oy + ly, oz + lz, (byte)id));
            }
        }

        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        bw.Write(Magic);
        bw.Write(Version);
        bw.Write(voxels.Count);
        foreach (var (x, y, z, id) in voxels)
        {
            bw.Write(x);
            bw.Write(y);
            bw.Write(z);
            bw.Write(id);
        }
    }

    public static List<(int X, int Y, int Z, BlockId Id)> Load(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var br = new BinaryReader(fs);

        byte[] magic = br.ReadBytes(4);
        if (!MagicMatches(magic))
            throw new InvalidDataException($"Not a ClearSkies grid file: {filePath}");

        ushort version = br.ReadUInt16();
        if (version != Version)
            throw new InvalidDataException($"Unsupported grid save version {version}: {filePath}");

        int count = br.ReadInt32();
        var voxels = new List<(int, int, int, BlockId)>(count);
        for (int i = 0; i < count; i++)
        {
            int x = br.ReadInt32(), y = br.ReadInt32(), z = br.ReadInt32();
            BlockId id = (BlockId)br.ReadByte();
            voxels.Add((x, y, z, id));
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
