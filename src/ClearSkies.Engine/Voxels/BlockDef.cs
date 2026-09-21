using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public readonly struct BlockDef
{
    public BlockId         Id             { get; init; }
    public string          Name           { get; init; }
    public Vector3D<float> Color          { get; init; }
    public bool            IsSolid        { get; init; }
    public byte            LightEmission  { get; init; } // 0-15: a lamp's brightness at its own block, and its reach
    public Vector3D<float> LightColor     { get; init; } // 0-1 per channel, scales LightEmission; default = white

    /// The colour a light-emitting block actually lights with (white when <see cref="LightColor"/> is unset).
    public Vector3D<float> EffectiveLightColor => LightColor == default ? Vector3D<float>.One : LightColor;
    public byte            Opacity        { get; init; } // 0=transparent, 15=fully opaque (light blocked)

    // Density used for dynamic-grid mass (PhysicsBodySystem): a box's mass = its volume * this. Air is 0;
    // every solid block should be > 0 so it contributes to the compound's mass and centre of mass.
    public float            Weight         { get; init; }

    // Texture-atlas sprite names (sans extension), resolved to array layers by GreedyMesher via
    // TextureAtlas.TryGetLayer. Null means "no texture for this face" — the renderer falls back
    // to the flat Color above. Texture is also the default for Top/Bottom when those are null.
    //
    // Top/Bottom are oriented per-voxel by the block's stored Facing (see Facing.cs), not fixed to
    // world Y: Top lands on whichever face the voxel is facing, Bottom on the opposite face, and
    // every other face gets Texture. A voxel with the default Facing.Up behaves exactly like the
    // old world-Y-relative scheme, so ordinary blocks (e.g. Grass) look unchanged; a placed block
    // (e.g. Fan) reorients its Top face with it.
    public string?         Texture        { get; init; }
    public string?         TextureTop     { get; init; }
    public string?         TextureBottom  { get; init; }

    /// True when this block's appearance actually depends on its stored Facing (i.e. it has a
    /// Top and/or Bottom texture distinct from Texture) — lets GreedyMesher skip the per-voxel
    /// Facing lookup entirely for blocks that look the same on every face regardless of orientation.
    public bool HasOrientedTexture => TextureTop != null || TextureBottom != null;

    /// Classifies which texture role <paramref name="faceNormal"/> plays for a voxel whose stored
    /// orientation is <paramref name="facing"/>: Top if the face points the way the voxel faces,
    /// Bottom if it points the opposite way, Side otherwise.
    public static FaceRole GetFaceRole(Vector3D<int> faceNormal, Facing facing)
    {
        var f = facing.ToVector();
        if (faceNormal == f) return FaceRole.Top;
        if (faceNormal.X == -f.X && faceNormal.Y == -f.Y && faceNormal.Z == -f.Z) return FaceRole.Bottom;
        return FaceRole.Side;
    }

    public string? GetFaceTexture(FaceRole role) => role switch
    {
        FaceRole.Top    => TextureTop    ?? Texture,
        FaceRole.Bottom => TextureBottom ?? Texture,
        _               => Texture,
    };
}

public enum FaceRole : byte { Side, Top, Bottom }
