using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public readonly struct BlockDef
{
    public BlockId        Id      { get; init; }
    public string         Name    { get; init; }
    public Vector3D<float> Color  { get; init; }
    public bool           IsSolid { get; init; }
}
