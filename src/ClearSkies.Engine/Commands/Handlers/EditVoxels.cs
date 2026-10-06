using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>One edit: every cell from <see cref="Min"/> to <see cref="Max"/> (inclusive, in the volume's own voxel
/// space) set to <see cref="Block"/>. A single block has Min = Max.</summary>
public readonly record struct VoxelOp(Vector3D<int> Min, Vector3D<int> Max, BlockId Block, BlockOrientation Orientation)
{
    public static VoxelOp SetBlock(Vector3D<int> cell, BlockId block, BlockOrientation orientation) => new(cell, cell, block, orientation);

    public static VoxelOp FillBox(Vector3D<int> centre, int radius, BlockId block, BlockOrientation orientation) =>
        new(centre - new Vector3D<int>(radius), centre + new Vector3D<int>(radius), block, orientation);

    public bool IsSingle => Min == Max;
    public Vector3D<int> Size => Max - Min + Vector3D<int>.One;
    public int CellCount => Size.X * Size.Y * Size.Z;
}

/// <summary>Changes blocks in a volume (a grid, or the static world): the only way blocks change during gameplay.</summary>
public struct EditVoxels : ICommand
{
    /// <summary>The volume: a grid, or the static world.</summary>
    public EntityId Volume;

    /// <summary>The editing player (for the reach check).</summary>
    public EntityId Editor;

    public VoxelOp[] Ops;

    public readonly EntityAddress Target => EntityAddress.Of(Volume);
}

public enum GameMode
{
    /// <summary>Single blocks within <see cref="EditLimits.SurvivalReach"/>.</summary>
    Survival,
    /// <summary>Brushes up to <see cref="EditLimits.CreativeBrushRadius"/> within <see cref="EditLimits.CreativeReach"/>.</summary>
    Creative,
}

/// <summary>How far and how much a player may edit, by game mode. The authority checks them with a
/// <see cref="ReachMargin"/>, because it sees the editor's position up to a snapshot late.</summary>
public sealed class EditLimits
{
    public const float SurvivalReach = 8f, CreativeReach = 16f;
    public const int CreativeBrushRadius = 8;
    public const float ReachMargin = 2f;
    public const int MaxOpsPerCommand = 64;

    public GameMode Mode { get; set; } = GameMode.Creative;

    public float Reach => Mode == GameMode.Creative ? CreativeReach : SurvivalReach;
    public int MaxBrushRadius => Mode == GameMode.Creative ? CreativeBrushRadius : 0;
    public int MaxBoxSize => 2 * MaxBrushRadius + 1;
}

/// <summary>
/// Applies <see cref="VoxelOp"/>s in voxel space. Validate enforces the <see cref="EditLimits"/>: box size by game mode,
/// and reach from the editor's eye to every op, plus a margin; and turns down an op on terrain the authority hasn't
/// loaded yet. AfterApply despawns a grid left empty. The undo holds the old blocks in the ops' area.
/// <para>The streamed world (with <see cref="Terrain"/>) is edited chunk by chunk: an op changes the chunks that are
/// here as they are (loaded, or known to hold nothing), and for each other chunk, which it can't change here, it leaves
/// a <see cref="TerrainEditedElsewhere"/>, so streaming loads that chunk with the edit later. It never makes a chunk
/// with nothing but the edit in it where the terrain hasn't loaded.</para>
/// </summary>
public sealed class EditVoxelsHandler : PredictedCommandHandler<EditVoxels, EditVoxelsHandler.Undo>
{
    private readonly BlockEntities _blocks;
    private readonly EditLimits _limits;

    public EditVoxelsHandler(BlockEntities blocks, EditLimits limits)
    {
        _blocks = blocks;
        _limits = limits;
    }

    public override ushort Id => CommandIds.EditVoxels;

    /// <summary>What streaming knows of the static world's chunks (none: nothing streams it, so a chunk that isn't
    /// loaded holds nothing, like a grid's).</summary>
    public IChunkStreaming? Terrain { get; set; }

    public override void Write(NetWriter w, in EditVoxels c)
    {
        c.Volume.Write(w);
        c.Editor.Write(w);
        w.WriteVarUInt((uint)c.Ops.Length);
        foreach (var op in c.Ops)
        {
            w.WriteInt32(op.Min.X); w.WriteInt32(op.Min.Y); w.WriteInt32(op.Min.Z);
            bool single = op.IsSingle;
            w.WriteBool(single);
            if (!single) { w.WriteInt32(op.Max.X); w.WriteInt32(op.Max.Y); w.WriteInt32(op.Max.Z); }
            w.WriteByte((byte)op.Block);
            w.WriteByte(op.Orientation.ToByte());
        }
    }

    public override EditVoxels Read(ref NetReader r)
    {
        var c = new EditVoxels { Volume = EntityId.Read(ref r), Editor = EntityId.Read(ref r) };
        uint count = r.ReadVarUInt();
        if (count > EditLimits.MaxOpsPerCommand) throw new InvalidDataException($"EditVoxels with {count} ops.");
        c.Ops = new VoxelOp[count];
        for (int i = 0; i < count; i++)
        {
            var min = new Vector3D<int>(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            var max = r.ReadBool() ? min : new Vector3D<int>(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            c.Ops[i] = new VoxelOp(min, max, (BlockId)r.ReadByte(), BlockOrientation.FromByte(r.ReadByte()));
        }
        return c;
    }

    public override Verdict Validate(ref EditVoxels c, in CommandContext ctx)
    {
        if (c.Ops is null || c.Ops.Length == 0 || c.Ops.Length > EditLimits.MaxOpsPerCommand) return Verdict.Reject;
        if (_blocks.Registry.Find(c.Volume) is not { } root || !root.Has<ChunkGrid>() || !root.Has<Transform>()) return Verdict.Reject;
        if (_blocks.Registry.Find(c.Editor) is not { } editor || !editor.Has<Transform>()) return Verdict.Reject;

        var volume = root.Get<ChunkGrid>().Volume;
        var eye = EyeOf(editor);
        var eyeLocal = volume.WorldToVoxel(root.Get<Transform>(), eye);
        float reach = _limits.Reach + EditLimits.ReachMargin;
        foreach (var op in c.Ops)
        {
            var size = op.Size;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) return Verdict.Reject;
            if (size.X > _limits.MaxBoxSize || size.Y > _limits.MaxBoxSize || size.Z > _limits.MaxBoxSize) return Verdict.Reject;
            if (!BlockRegistry.IsDefined(op.Block)) return Verdict.Reject;
            // Nearest point of the op's box (cells span [min, max + 1]) to the eye.
            var nearest = Vector3D.Clamp(eyeLocal, new Vector3D<float>(op.Min.X, op.Min.Y, op.Min.Z),
                                         new Vector3D<float>(op.Max.X + 1, op.Max.Y + 1, op.Max.Z + 1));
            if (Vector3D.Distance(nearest, eyeLocal) > reach) return Verdict.Reject;
            if (!AllHere(c.Volume, volume, op)) return Verdict.Reject;
        }
        return Verdict.Accept;
    }

    /// <summary>Whether every chunk an op reaches is here as it is, so the authority's edit is whole: terrain still
    /// loading here can't be edited yet.</summary>
    private bool AllHere(EntityId id, ChunkVolume volume, in VoxelOp op)
    {
        var lo = ChunkPosition.FromVoxel(op.Min);
        var hi = ChunkPosition.FromVoxel(op.Max);
        for (int z = lo.Z; z <= hi.Z; z++)
        for (int y = lo.Y; y <= hi.Y; y++)
        for (int x = lo.X; x <= hi.X; x++)
            if (!IsHere(id, volume, new ChunkPosition(x, y, z))) return false;
        return true;
    }

    /// <summary>Whether a chunk is here as it is, so an edit changes it here: always, but for a chunk of the streamed
    /// world that isn't loaded and isn't known to hold nothing (or outside the layers edits change, where they do
    /// nothing).</summary>
    private bool IsHere(EntityId id, ChunkVolume volume, ChunkPosition pos) =>
        id != EntityRegistry.WorldVolume || Terrain is not { } terrain || volume.IsLoaded(pos) ||
        pos.Y < volume.EditableLayers.Min || pos.Y > volume.EditableLayers.Max || terrain.IsKnownEmpty(pos);

    private static Vector3D<float> EyeOf(Entity editor)
    {
        var p = editor.Get<Transform>().Position;
        float eye = editor.Has<CharacterControllerComponent>() ? editor.Get<CharacterControllerComponent>().EyeHeight : 0f;
        return p + new Vector3D<float>(0, eye, 0);
    }

    public override void Apply(in EditVoxels e, in ApplyContext ctx)
    {
        if (_blocks.Volume(e.Volume) is not { } volume) return;
        foreach (var op in e.Ops)
        {
            if (e.Volume == EntityRegistry.WorldVolume && Terrain is not null) ApplyByChunk(volume, op);
            else if (op.IsSingle) volume.SetBlock(op.Min.X, op.Min.Y, op.Min.Z, op.Block, op.Orientation);
            else volume.FillBox(op.Min, op.Max, op.Block, op.Orientation);
        }
    }

    /// <summary>An op on the streamed world: each chunk it reaches that's here changes, and each other one is left for
    /// streaming to load with the edit (a <see cref="TerrainEditedElsewhere"/>).</summary>
    private void ApplyByChunk(ChunkVolume volume, in VoxelOp op)
    {
        const int S = ChunkData.Size;
        var lo = ChunkPosition.FromVoxel(op.Min);
        var hi = ChunkPosition.FromVoxel(op.Max);
        for (int z = lo.Z; z <= hi.Z; z++)
        for (int y = lo.Y; y <= hi.Y; y++)
        for (int x = lo.X; x <= hi.X; x++)
        {
            var pos = new ChunkPosition(x, y, z);
            if (!IsHere(EntityRegistry.WorldVolume, volume, pos))
            {
                volume.Root.World.CreateEntity().Set(new TerrainEditedElsewhere { Position = pos });
                continue;
            }
            var origin = new Vector3D<int>(x * S, y * S, z * S);
            var min = Vector3D.Max(op.Min, origin);
            var max = Vector3D.Min(op.Max, origin + new Vector3D<int>(S - 1));
            if (min == max) volume.SetBlock(min.X, min.Y, min.Z, op.Block, op.Orientation);
            else volume.FillBox(min, max, op.Block, op.Orientation);
        }
    }

    public override void AfterApply(in EditVoxels e, in CommandContext ctx)
    {
        // Only the authority (the grid's owner) runs AfterApply, so the owner despawns it, for everyone.
        if (_blocks.Registry.Find(e.Volume) is { } root && root.Has<DynamicGrid>() && root.Get<ChunkGrid>().Volume.IsEmpty())
            Owner.Send(new DespawnEntity { Entity = e.Volume });
    }

    // ── undo ────────────────────────────────────────────────────────────────

    /// <summary>What an edit changed: every cell in its ops' boxes as it was.</summary>
    public sealed class Undo
    {
        public EntityId Volume;
        public readonly List<(VoxelOp Box, BlockId[] Blocks, byte[] Orientations)> Boxes = new();
    }

    public override Undo Capture(in EditVoxels c)
    {
        var undo = new Undo { Volume = c.Volume };
        if (_blocks.Volume(c.Volume) is not { } volume) return undo;
        foreach (var op in c.Ops)
        {
            int n = op.CellCount, i = 0;
            var blocks = new BlockId[n];
            var orientations = new byte[n];
            for (int z = op.Min.Z; z <= op.Max.Z; z++)
            for (int y = op.Min.Y; y <= op.Max.Y; y++)
            for (int x = op.Min.X; x <= op.Max.X; x++, i++)
            {
                blocks[i] = volume.GetBlock(x, y, z);
                orientations[i] = volume.GetOrientation(x, y, z).ToByte();
            }
            undo.Boxes.Add((op, blocks, orientations));
        }
        return undo;
    }

    public override void Restore(in Undo undo)
    {
        if (_blocks.Volume(undo.Volume) is not { } volume) return;
        for (int b = undo.Boxes.Count - 1; b >= 0; b--) // later ops first, so overlapping boxes end as they were
        {
            var (op, blocks, orientations) = undo.Boxes[b];
            int i = 0;
            for (int z = op.Min.Z; z <= op.Max.Z; z++)
            for (int y = op.Min.Y; y <= op.Max.Y; y++)
            for (int x = op.Min.X; x <= op.Max.X; x++, i++)
            {
                var orientation = BlockOrientation.FromByte(orientations[i]);
                if (volume.GetBlock(x, y, z) != blocks[i] || volume.GetOrientation(x, y, z) != orientation)
                    volume.SetBlock(x, y, z, blocks[i], orientation);
            }
        }
    }
}
