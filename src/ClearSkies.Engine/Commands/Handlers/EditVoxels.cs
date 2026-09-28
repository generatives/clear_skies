using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Persistence;
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
    /// <summary>The volume's network ID.</summary>
    public uint Volume;

    /// <summary>The editing player's network ID (for the reach check).</summary>
    public uint Editor;

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
/// and reach from the editor's eye to every op, plus a margin. A new lever takes the setting of the levers already on
/// its axis. AfterApply despawns a grid left empty. The undo holds the old blocks in the ops' area, and the state of
/// the block entities there (lever values, wheel angles).
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

    /// <summary>Edits parts of the static world that aren't loaded here (the host saves them; a client marks them to be
    /// fetched fresh). Without one, an edit to an unloaded world chunk creates it empty, as a grid's would.</summary>
    public IWorldChunkEditor? WorldEditor { get; set; }

    public override void Write(NetWriter w, in EditVoxels c)
    {
        w.WriteUInt32(c.Volume);
        w.WriteUInt32(c.Editor);
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
        var c = new EditVoxels { Volume = r.ReadUInt32(), Editor = r.ReadUInt32() };
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
        }
        return Verdict.Accept;
    }

    private static Vector3D<float> EyeOf(Entity editor)
    {
        var p = editor.Get<Transform>().Position;
        float eye = editor.Has<CharacterControllerComponent>() ? editor.Get<CharacterControllerComponent>().EyeHeight : 0f;
        return p + new Vector3D<float>(0, eye, 0);
    }

    public override void Apply(in EditVoxels e, in ApplyContext ctx)
    {
        if (_blocks.Volume(e.Volume) is not { } volume) return;
        bool world = e.Volume == NetRegistry.WorldVolume && WorldEditor != null;
        foreach (var op in e.Ops)
        {
            if (world) ApplyToWorld(volume, op);
            else if (op.IsSingle) volume.SetBlock(op.Min.X, op.Min.Y, op.Min.Z, op.Block, op.Orientation);
            else volume.FillBox(op.Min, op.Max, op.Block, op.Orientation);
        }
        if (_blocks.Registry.Find(e.Volume) is { } root) AdoptLeverSettings(volume, root.Get<NetId>().Value, e.Ops);
    }

    /// <summary>A world edit, chunk by chunk: loaded chunks change in place, the rest through <see cref="WorldEditor"/>.</summary>
    private void ApplyToWorld(ChunkVolume world, VoxelOp op)
    {
        const int S = ChunkData.Size;
        int Floor(int v) => (int)MathF.Floor(v / (float)S);
        for (int cz = Floor(op.Min.Z); cz <= Floor(op.Max.Z); cz++)
        for (int cy = Floor(op.Min.Y); cy <= Floor(op.Max.Y); cy++)
        for (int cx = Floor(op.Min.X); cx <= Floor(op.Max.X); cx++)
        {
            var pos = new ChunkPosition(cx, cy, cz);
            if (cy < world.EditableLayers.Min || cy > world.EditableLayers.Max) continue;
            var min = Vector3D.Max(op.Min, new Vector3D<int>(cx * S, cy * S, cz * S));
            var max = Vector3D.Min(op.Max, new Vector3D<int>(cx * S + S - 1, cy * S + S - 1, cz * S + S - 1));
            if (world.IsLoaded(pos))
            {
                world.FillBox(min, max, op.Block, op.Orientation);
                continue;
            }
            var (block, orientation) = (op.Block, op.Orientation);
            WorldEditor!.EditUnloaded(pos, data =>
            {
                for (int z = min.Z; z <= max.Z; z++) for (int y = min.Y; y <= max.Y; y++) for (int x = min.X; x <= max.X; x++)
                    data.Set(x - cx * S, y - cy * S, z - cz * S, block, orientation);
            });
        }
    }

    /// <summary>New levers take the setting of the levers already on their axis in the volume.</summary>
    private void AdoptLeverSettings(ChunkVolume volume, uint volumeId, VoxelOp[] ops)
    {
        foreach (var op in ops)
        {
            if (op.Block != BlockId.Lever) continue;
            for (int z = op.Min.Z; z <= op.Max.Z; z++)
            for (int y = op.Min.Y; y <= op.Max.Y; y++)
            for (int x = op.Min.X; x <= op.Max.X; x++)
            {
                if (!volume.TryGetBlockEntity(x, y, z, out var lever) || !lever.Has<Lever>()) continue;
                foreach (var (other, sign) in _blocks.LeversOnSameAxis(lever))
                {
                    var p = other.Get<BlockRef>().Position;
                    if (InAnyOp(ops, p)) continue; // placed by this same edit
                    lever.Get<Lever>().Value = other.Get<Lever>().Value * sign;
                    break;
                }
            }
        }
    }

    private static bool InAnyOp(VoxelOp[] ops, Vector3D<int> p)
    {
        foreach (var op in ops)
            if (p.X >= op.Min.X && p.X <= op.Max.X && p.Y >= op.Min.Y && p.Y <= op.Max.Y && p.Z >= op.Min.Z && p.Z <= op.Max.Z)
                return true;
        return false;
    }

    public override void AfterApply(in EditVoxels e, in CommandContext ctx)
    {
        if (_blocks.Registry.Find(e.Volume) is { } root && root.Has<DynamicGrid>() && root.Get<ChunkGrid>().Volume.IsEmpty())
        {
            if (Owner.Handles<DespawnEntity>()) Owner.Send(new DespawnEntity { Entity = e.Volume });
            else Hierarchy.DestroyRecursive(root); // its chunks with it
        }
    }

    // ── undo ────────────────────────────────────────────────────────────────

    /// <summary>What an edit changed: every cell in its ops' boxes as it was, and the block entity state there.</summary>
    public sealed class Undo
    {
        public uint Volume;
        public readonly List<(VoxelOp Box, BlockId[] Blocks, byte[] Orientations)> Boxes = new();
        public readonly List<(Vector3D<int> Cell, float? Lever, float? Wheel)> States = new();
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
                if (volume.TryGetBlockEntity(x, y, z, out var be))
                    undo.States.Add((new Vector3D<int>(x, y, z),
                        be.Has<Lever>() ? be.Get<Lever>().Value : null,
                        be.Has<SteeringWheel>() ? be.Get<SteeringWheel>().Angle : null));
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
        foreach (var (cell, lever, wheel) in undo.States)
        {
            if (!volume.TryGetBlockEntity(cell.X, cell.Y, cell.Z, out var be)) continue;
            if (lever is { } l && be.Has<Lever>()) be.Get<Lever>().Value = l;
            if (wheel is { } w && be.Has<SteeringWheel>()) be.Get<SteeringWheel>().Angle = w;
        }
    }
}
