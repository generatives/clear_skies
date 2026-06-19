using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public readonly struct BlockDef
{
    public BlockId         Id             { get; init; }
    public string          Name           { get; init; }
    public Vector3D<float> Color          { get; init; }
    public bool            IsSolid        { get; init; }
    public byte            LightEmission  { get; init; } // 0-15; seeds block-light BFS when placed
    public byte            Opacity        { get; init; } // 0=transparent, 15=fully opaque (light blocked)
}
