using System.IO;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Reads/writes a single static-world chunk's raw 32x32x32 block (+ facing, since v2) array to/from a
/// small binary format. One file per chunk; the chunk position is implied entirely by the filename (see
/// <see cref="ChunkLoadSystem"/>), so no position is stored in the payload itself.
/// </summary>
internal static class StaticWorldSerializer
{
    // "CSCD" ClearSkies Chunk Data — 4 literal ASCII bytes so the format is identifiable in a hex viewer.
    private static readonly byte[] Magic = { (byte)'C', (byte)'S', (byte)'C', (byte)'D' };
    private const ushort Version = 2; // v1: blocks only. v2: + a facing byte per voxel.
    private const int PayloadBytes = ChunkData.Size * ChunkData.Size * ChunkData.Size;

    public static void Save(ChunkData data, string filePath)
    {
        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        bw.Write(Magic);
        bw.Write(Version);
        bw.Write(data.BlocksAsBytes());
        bw.Write(data.FacingsAsBytes());
    }

    /// <summary>Loads bytes into <paramref name="data"/> in place. Returns false (leaving
    /// <paramref name="data"/> untouched) if the file doesn't exist.</summary>
    public static bool TryLoad(string filePath, ChunkData data)
    {
        if (!File.Exists(filePath)) return false;

        using var fs = File.OpenRead(filePath);
        using var br = new BinaryReader(fs);

        if (!MagicMatches(br.ReadBytes(Magic.Length)))
            throw new InvalidDataException($"Not a ClearSkies chunk file: {filePath}");

        ushort version = br.ReadUInt16();
        if (version != 1 && version != Version)
            throw new InvalidDataException($"Unsupported chunk save version {version}: {filePath}");

        byte[] blockPayload = br.ReadBytes(PayloadBytes);
        if (blockPayload.Length != PayloadBytes)
            throw new InvalidDataException($"Truncated chunk file (expected {PayloadBytes} bytes, got {blockPayload.Length}): {filePath}");
        data.LoadBlockBytes(blockPayload);

        if (version >= 2)
        {
            byte[] facingPayload = br.ReadBytes(PayloadBytes);
            if (facingPayload.Length == PayloadBytes)
                data.LoadFacingBytes(facingPayload);
        }
        // v1 files have no facing payload — every voxel keeps ChunkData's default facing.

        return true;
    }

    private static bool MagicMatches(byte[] magic)
    {
        if (magic.Length != Magic.Length) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (magic[i] != Magic[i]) return false;
        return true;
    }
}
