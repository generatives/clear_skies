using DefaultEcs;
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
    // Top/Bottom are oriented per-voxel by the block's stored orientation (see BlockOrientation.cs), not fixed to
    // world Y: Top lands on whichever face the voxel's top points, Bottom on the opposite face, and
    // every other face gets Texture. A voxel with the default Upright orientation behaves exactly like the
    // old world-Y-relative scheme, so ordinary blocks (e.g. Grass) look unchanged; a placed block
    // (e.g. Fan) reorients its Top face with it.
    public string?         Texture        { get; init; }
    public string?         TextureTop     { get; init; }
    public string?         TextureBottom  { get; init; }

    /// True when this block's appearance actually depends on its stored orientation (i.e. it has a
    /// Top and/or Bottom texture distinct from Texture) — lets GreedyMesher skip the per-voxel
    /// orientation lookup entirely for blocks that look the same on every face regardless of orientation.
    public bool HasOrientedTexture => TextureTop != null || TextureBottom != null;

    /// Model-block path (a glTF file relative to the game's Resources/Models folder, see
    /// <c>BlockModelLibrary</c>). Non-null makes this a model block: drawn as that model, placed at its cell
    /// and turned to the voxel's stored orientation (the model's +Y to its top, -Z to its north face), instead of as a textured cube.
    /// It stays <see cref="IsSolid"/> (raycasts hit it, so it can be targeted, placed against and broken, and it
    /// still collides as a full cell), but it isn't a <see cref="IsFullCube"/>: it emits no cube faces and never
    /// hides a neighbour's.
    public string?         Model          { get; init; }

    /// Entity-block hook: non-null makes every placed block of this type also get its own ECS entity while its
    /// chunk is loaded (created and destroyed by <see cref="ChunkVolume"/>, a Hierarchy child of the chunk
    /// entity, carrying a <see cref="ECS.BlockRef"/> back to its voxel). The hook attaches the block's starting
    /// components: its state and behaviour. The voxel still decides occupancy (collision, raycasts, light, mass,
    /// saving the block type); the entity holds everything else. Plain blocks leave this null and stay voxel-only.
    /// Called on the main thread.
    public Action<Entity>? Components     { get; init; }

    /// True when blocks of this type get an entity (see <see cref="Components"/>). An entity block with a
    /// <see cref="Model"/> is drawn by <c>ModelRenderSystem</c> at its entity's Transform (so it can be
    /// animated), not with its chunk's static model blocks.
    public bool IsEntityBlock => Components != null;

    /// True for blocks drawn as a full cube by <c>GreedyMesher</c>: solid and not a model block. The mesher's
    /// face culling (and the neighbour remeshing that depends on it) keys off this rather than IsSolid.
    public bool IsFullCube => IsSolid && Model == null;

    /// Classifies which texture role <paramref name="faceNormal"/> plays for a voxel whose
    /// top points <paramref name="up"/>: Top if the face points that way, Bottom if it points the opposite way,
    /// Side otherwise.
    public static FaceRole GetFaceRole(Vector3D<int> faceNormal, Direction up)
    {
        var f = up.ToVector();
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
