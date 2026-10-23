using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>A step of water flowing through the world (see <see cref="WaterFlowSystem"/>): the cells it changed.</summary>
public struct FlowWater : ICommand
{
    public WaterChange[] Changes;

    public readonly EntityAddress Target => EntityAddress.Of(EntityRegistry.WorldVolume);
}

/// <summary>
/// Applies a step of flowing water. Only the world's authority sends it (its <see cref="WaterFlowSystem"/>, which alone
/// runs the water), so anyone else's is turned down. A change to a chunk that isn't loaded here is skipped: the chunk
/// comes with it when it loads, from the Host, which keeps every chunk the authority changes.
/// </summary>
public sealed class FlowWaterHandler : CommandHandler<FlowWater>
{
    /// <summary>At most this many changes to a command; a bigger step is sent as several.</summary>
    public const int MaxChanges = 1024;

    private readonly BlockEntities _blocks;

    public FlowWaterHandler(BlockEntities blocks) => _blocks = blocks;

    public override ushort Id => CommandIds.FlowWater;

    public override void Write(NetWriter w, in FlowWater c)
    {
        w.WriteVarUInt((uint)c.Changes.Length);
        foreach (var change in c.Changes)
        {
            w.WriteInt32(change.Cell.X); w.WriteInt32(change.Cell.Y); w.WriteInt32(change.Cell.Z);
            w.WriteByte((byte)change.Block);
            w.WriteByte(change.State.ToByte());
        }
    }

    public override FlowWater Read(ref NetReader r)
    {
        uint count = r.ReadVarUInt();
        if (count > MaxChanges) throw new InvalidDataException($"FlowWater with {count} changes.");
        var changes = new WaterChange[count];
        for (int i = 0; i < count; i++)
            changes[i] = new WaterChange(new Vector3D<int>(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()), (BlockId)r.ReadByte(),
                                         BlockOrientation.FromByte(r.ReadByte()));
        return new FlowWater { Changes = changes };
    }

    public override Verdict Validate(ref FlowWater c, in CommandContext ctx)
    {
        if (!ctx.FromLocal(Owner.Session) || c.Changes is null || c.Changes.Length > MaxChanges) return Verdict.Reject;
        foreach (var change in c.Changes)
            if (change.Block is not (BlockId.Water or BlockId.Air)) return Verdict.Reject;
        return Verdict.Accept;
    }

    public override void Apply(in FlowWater e, in ApplyContext ctx)
    {
        if (_blocks.Volume(EntityRegistry.WorldVolume) is not { } volume) return;
        foreach (var change in e.Changes)
            if (volume.IsLoaded(ChunkPosition.FromVoxel(change.Cell)))
                volume.SetBlock(change.Cell.X, change.Cell.Y, change.Cell.Z, change.Block, change.State);
    }
}
