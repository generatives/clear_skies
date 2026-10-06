using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

public readonly struct BlockDef
{
    public BlockId         Id             { get; init; }
    public string          Name           { get; init; }
    public Vector3D<float> Color          { get; init; }
    public bool            IsSolid        { get; init; }
    public bool            PlaceOriented  { get; init ;}

    /// True for a solid block that bodies pass through: raycasts still hit it (so it can be targeted, used, placed
    /// against and broken), but it gets no collision shape, so characters walk through it. Having no shape, it adds
    /// nothing to a ship's mass either. For small fittings like levers.
    public bool            Passable       { get; init; }

    /// True when this block gets a collision shape: solid and not <see cref="Passable"/>.
    public bool            Collides       => IsSolid && !Passable;
    public byte            LightEmission  { get; init; } // 0-15: a lamp's brightness at its own block, and its reach
    public Vector3D<float> LightColor     { get; init; } // 0-1 per channel, scales LightEmission; default = white

    /// The colour a light-emitting block actually lights with (white when <see cref="LightColor"/> is unset).
    public Vector3D<float> EffectiveLightColor => LightColor == default ? Vector3D<float>.One : LightColor;

    /// How a full-cube block's faces are drawn (see <see cref="RenderLayer"/>): opaque (the default), cut out (glass)
    /// or blended (water).
    public RenderLayer     Layer          { get; init; }

    /// True for a full-cube block you can see through: any <see cref="Layer"/> but <see cref="RenderLayer.Opaque"/>. It
    /// never hides a neighbour's face, except another block of its own type (so a wall of glass or a pool of water shows
    /// only its outer surface), and it lets light through (see <see cref="BlocksLight"/>).
    public bool            Transparent    => Layer != RenderLayer.Opaque;

    /// True for a <see cref="Transparent"/> cube that doesn't hide even its own type's faces, so a mass of it shows its
    /// inside through its gaps: leaves, whose canopy should look dense rather than a thin shell.
    public bool            ShowsInnerFaces { get; init; }

    /// Opacity (0-1) a <see cref="RenderLayer.Translucent"/> block's faces are drawn with, multiplying its texture's
    /// alpha: e.g. water, whose texture is fully opaque. 0 (unset) means 1, the texture's alpha alone.
    public float           Alpha          { get; init; }

    /// <see cref="Alpha"/>, with unset read as 1.
    public float           EffectiveAlpha => Alpha <= 0f ? 1f : Alpha;

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

    /// For a <see cref="BlockShape.Cross"/> block: the sprites each of its two quads picks from, by a hash of its
    /// cell, so a patch of grass isn't the same sprite over and over. List a sprite more than once to weight it. Null:
    /// just <see cref="Texture"/>.
    public string[]?       CrossTextures  { get; init; }

    /// True when this block's appearance actually depends on its stored orientation (i.e. it has a
    /// Top and/or Bottom texture distinct from Texture) — lets GreedyMesher skip the per-voxel
    /// orientation lookup entirely for blocks that look the same on every face regardless of orientation.
    public bool HasOrientedTexture => TextureTop != null || TextureBottom != null;

    /// Model-block path (a glTF file relative to the game's Resources/Models folder, see
    /// <c>BlockModelLibrary</c>). Non-null makes this a model block: drawn as that model, placed at its cell
    /// and turned to the voxel's stored orientation (the model's +Y to its top, -Z to its north face), instead of as a textured cube.
    /// It stays <see cref="IsSolid"/> (raycasts hit it, so it can be targeted, placed against and broken, and unless
    /// <see cref="Passable"/> it still collides as a full cell), but it isn't a <see cref="IsFullCube"/>: it emits no cube faces and never
    /// hides a neighbour's.
    public string?         Model          { get; init; }

    /// Inventory icon: a PNG relative to the game's Resources/Icons folder, shown for this block in the UI (e.g. the
    /// hotbar). Model blocks get theirs baked from <see cref="Model"/> by tools/ClearSkies.IconBaker; rerun it after
    /// changing a model. Null lets the UI make one: the block's <see cref="Texture"/>, or a swatch of its
    /// <see cref="Color"/>.
    public string?         IconTexture    { get; init; }

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

    /// How a block that isn't a <see cref="Model"/> block is drawn: a textured cube (the default), or a
    /// <see cref="BlockShape.Cross"/> of two upright quads for plants and the like.
    public BlockShape      Shape          { get; init; }

    /// True for a <see cref="BlockShape.Cross"/> block (grass, mushrooms, pebbles): drawn by <c>GreedyMesher</c> as two
    /// textured quads crossing diagonally through its cell, seen from both sides, with the texture's clear texels cut
    /// out (the <see cref="RenderLayer.Cutout"/> mesh, whatever its <see cref="Layer"/>). It fills so little of its cell
    /// that it stops no light and hides no neighbour's face. Usually also <see cref="Passable"/> (walked through) and
    /// <see cref="Replaceable"/>.
    public bool IsCross => IsSolid && Model == null && Shape == BlockShape.Cross;

    /// True for a block that a block placed against it replaces, and that a block can be placed into (like Minecraft's
    /// grass): clicking a tuft of grass puts the new block where the grass was, not on top of it. Cross blocks are.
    public bool Replaceable => IsCross;

    /// True for blocks drawn as a full cube by <c>GreedyMesher</c>: solid, not a model block and not a
    /// <see cref="BlockShape.Cross"/>. The mesher's face culling (and the neighbour remeshing that depends on it) keys
    /// off this rather than IsSolid.
    public bool IsFullCube => IsSolid && Model == null && Shape == BlockShape.Cube;

    /// True for a model block whose model fills its whole cell (e.g. Fan): it's treated like an opaque cube, so it
    /// stops light and hides the faces of blocks against it (which would otherwise be drawn on top of the model's
    /// sides and flicker). Other model blocks only fill part of their cell, so they do neither.
    public bool            OpaqueModel    { get; init; }

    /// True when this block stops light (sun, lamps, bounce) and darkens its neighbours' corners: a full cube unless
    /// it's <see cref="Transparent"/>, or an <see cref="OpaqueModel"/>. The lighting system's occupancy is exactly this.
    public bool BlocksLight => Model == null ? IsFullCube && !Transparent : IsSolid && OpaqueModel;

    /// True for a block that lets light through but is still lit like a surface: the lighting system works out light
    /// in its cell and the cells around it (where an opaque block's faces would be), though rays pass through it.
    /// Blocks that <see cref="ShowsInnerFaces"/> (leaves) are: a canopy is drawn right through, away from any opaque
    /// block, and would otherwise get no light of its own.
    public bool CatchesLight => IsFullCube && Transparent && ShowsInnerFaces;

    /// True when this block hides the face of a <paramref name="neighbour"/> block that touches it: an opaque cube or
    /// <see cref="OpaqueModel"/> hides every face against it, a <see cref="Transparent"/> cube only those of its own type
    /// (unless it <see cref="ShowsInnerFaces"/>).
    public bool HidesFaceOf(BlockId neighbour) => BlocksLight || (IsFullCube && Transparent && Id == neighbour && !ShowsInnerFaces);

    /// True when this block can hide a neighbour's face (see <see cref="HidesFaceOf"/>).
    public bool HidesFaces => BlocksLight || IsFullCube;

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

/// <summary>How a block that isn't a model block is drawn (see <see cref="BlockDef.Shape"/>).</summary>
public enum BlockShape : byte
{
    /// <summary>A full cube, greedy meshed with its neighbours.</summary>
    Cube,

    /// <summary>Two upright quads crossing diagonally through the cell (a crossed billboard), each seen from both
    /// sides: grass, flowers, mushrooms, pebbles. Textured with <see cref="BlockDef.Texture"/>, upright regardless of
    /// the block's orientation.</summary>
    Cross,
}

/// <summary>How a full-cube block's faces are drawn; <c>GreedyMesher</c> gives each layer its own mesh per chunk.</summary>
public enum RenderLayer : byte
{
    /// <summary>Solid faces, drawn with the world (<c>fs_main</c>).</summary>
    Opaque,

    /// <summary>Drawn with the world, but texels under half alpha are cut out (<c>fs_cutout</c>): fully see-through
    /// there, solid (and depth-writing) elsewhere, so it needs no sorting. Back faces aren't drawn, so looking through a
    /// block of glass you see only its near side. For mostly clear textures like glass.</summary>
    Cutout,

    /// <summary>Alpha-blended over the world after it (<c>fs_chunk_transparent</c>), its texture's alpha times
    /// <see cref="BlockDef.Alpha"/>; only the nearest translucent face shows at each pixel. For water.</summary>
    Translucent,
}
