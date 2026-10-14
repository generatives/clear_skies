using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Startup;

/// <summary>
/// The game's replicated components (see <see cref="ReplicatedComponents"/>): one line per component, in an order
/// every machine shares (their IDs come from it). <see cref="Checksum"/> goes in the join handshake, so a machine that
/// registers differently is turned away rather than misreading state.
/// </summary>
public static class GameReplication
{
    public static void Register(ReplicatedComponents components)
    {
        // A grid's block entities replicate with it, keyed by cell.
        components.Children<ChunkGrid>(grid => grid.Volume);

        // A Fan's thrust, for its flames: a fraction, to a 255th.
        components.Register<Fan>((in Fan fan, NetWriter w) =>
                                w.WriteByte((byte)MathF.Round(System.Math.Clamp(fan.Thrust, 0f, 1f) * 255f)),
                            (ref Fan fan, ref NetReader r) => fan.Thrust = r.ReadByte() / 255f);

        // A player's grapple rope, to draw: what it's hooked onto, and where on it.
        components.Register<RopeHook>((in RopeHook hook, NetWriter w) =>
                                     {
                                         w.WriteUInt32(hook.Anchor.Value);
                                         if (hook.Anchor.IsNone) return;
                                         w.WriteSingle(hook.LocalPoint.X); w.WriteSingle(hook.LocalPoint.Y); w.WriteSingle(hook.LocalPoint.Z);
                                     },
                                     (ref RopeHook hook, ref NetReader r) =>
                                     {
                                         hook.Anchor = new EntityId(r.ReadUInt32());
                                         hook.LocalPoint = hook.Anchor.IsNone ? default : new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                     });
    }

    /// <summary>A hash of the registered components, in order.</summary>
    public static ulong Checksum
    {
        get
        {
            var components = new ReplicatedComponents();
            Register(components);
            return components.Checksum;
        }
    }
}
