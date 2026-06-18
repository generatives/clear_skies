using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public readonly struct ChunkPosition : IEquatable<ChunkPosition>
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }

    public ChunkPosition(int x, int y, int z) { X = x; Y = y; Z = z; }

    public Vector3D<float> WorldOrigin => new(X * ChunkData.Size, Y * ChunkData.Size, Z * ChunkData.Size);

    public ChunkPosition Offset(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);

    public bool Equals(ChunkPosition other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is ChunkPosition c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(ChunkPosition a, ChunkPosition b) =>  a.Equals(b);
    public static bool operator !=(ChunkPosition a, ChunkPosition b) => !a.Equals(b);
    public override string ToString() => $"({X},{Y},{Z})";
}
