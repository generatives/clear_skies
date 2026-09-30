using System.IO.Compression;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Encodes a single static-world chunk's raw 32x32x32 block (+ orientation, since v2) array as a compressed blob, as
/// stored in the save database's chunks table and sent in chunk transfer. The chunk position is stored alongside it,
/// not in the payload.
/// </summary>
public static class StaticWorldSerializer
{
    // "CSCD" ClearSkies Chunk Data — 4 literal ASCII bytes so the format is identifiable in a hex viewer.
    private static readonly byte[] Magic = { (byte)'C', (byte)'S', (byte)'C', (byte)'D' };
    private const ushort Version = 2; // v1: blocks only. v2: + an orientation byte per voxel.
    private const int PayloadBytes = ChunkData.Size * ChunkData.Size * ChunkData.Size;

    public static byte[] ToBytes(ChunkData data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var bw = new BinaryWriter(z))
        {
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(data.BlocksAsBytes());
            bw.Write(data.OrientationsAsBytes());
        }
        return ms.ToArray();
    }

    /// <summary>Loads a blob into <paramref name="data"/> in place.</summary>
    public static void Read(ReadOnlySpan<byte> blob, ChunkData data)
    {
        using var ms = new MemoryStream(blob.ToArray());
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var br = new BinaryReader(z);

        if (!br.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not ClearSkies chunk data.");

        ushort version = br.ReadUInt16();
        if (version != 1 && version != Version)
            throw new InvalidDataException($"Unsupported chunk data version {version}.");

        byte[] blockPayload = br.ReadBytes(PayloadBytes);
        if (blockPayload.Length != PayloadBytes)
            throw new InvalidDataException($"Truncated chunk data (expected {PayloadBytes} bytes, got {blockPayload.Length}).");
        data.LoadBlockBytes(blockPayload);

        if (version >= 2)
        {
            byte[] orientationPayload = br.ReadBytes(PayloadBytes);
            if (orientationPayload.Length == PayloadBytes)
                data.LoadOrientationBytes(orientationPayload);
        }
    }
}
