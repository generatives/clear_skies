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

    // Texture-atlas sprite names (sans extension), resolved to array layers by GreedyMesher via
    // TextureAtlas.TryGetLayer. Null means "no texture for this face" — the renderer falls back
    // to the flat Color above. Texture is also the default for Top/Bottom when those are null.
    public string?         Texture        { get; init; }
    public string?         TextureTop     { get; init; }
    public string?         TextureBottom  { get; init; }

    public string? GetFaceTexture(Vector3D<int> normal) =>
        normal.Y > 0 && TextureTop    != null ? TextureTop :
        normal.Y < 0 && TextureBottom != null ? TextureBottom :
        Texture;
}
