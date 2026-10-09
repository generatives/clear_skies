using System.Numerics;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;

namespace ClearSkies.Net.Protocol;

[Flags]
public enum SnapshotFlags : byte
{
    None = 0,
    /// <summary>A <see cref="LookAngles"/> streamed value follows.</summary>
    HasLook = 1,
    /// <summary>A player free-flying: no character body, so nothing to stand on or push.</summary>
    FreeFlying = 2,
    /// <summary>A player played on another machine: the last of its inputs applied (<see cref="BodySnapshot.Input"/>)
    /// follows, so it can check its prediction.</summary>
    HasInput = 4,
    /// <summary>Synced block entity state on it follows (<see cref="BodySnapshot.State"/>).</summary>
    HasState = 8,
}

/// <summary>One synced field of a block entity on a ship (see <see cref="ClearSkies.Engine.Entities.SyncedState"/>):
/// the block's cell in the ship, the field's ID and its value.</summary>
public readonly record struct SyncedValue(short X, short Y, short Z, byte Field, byte Value);

/// <summary>A player's look direction, streamed with their body.</summary>
public readonly record struct LookAngles(float Yaw, float Pitch);

/// <summary>
/// One body's pose and velocity at a tick, relative to what supports it (world space with no support), with any
/// streamed values. About 55 bytes: the rotation is smallest-three encoded in 64 bits and velocities are halves.
/// </summary>
public struct BodySnapshot
{
    public EntityId Entity;
    public ushort Epoch;
    public EntityId Support;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 LinearVelocity, AngularVelocity;
    public SnapshotFlags Flags;
    public LookAngles Look;
    /// <summary>With <see cref="SnapshotFlags.HasInput"/>: the number of the player's last input applied.</summary>
    public uint Input;
    /// <summary>With <see cref="SnapshotFlags.HasState"/>: synced block entity fields that changed (or all of them, now
    /// and then).</summary>
    public SyncedValue[]? State;

    public readonly void Write(NetWriter w)
    {
        Entity.Write(w);
        w.WriteUInt16(Epoch);
        Support.Write(w);
        w.WriteVector3(Position);
        w.WriteUInt64(QuaternionCodec.Pack(Rotation));
        w.WriteHalf(LinearVelocity.X); w.WriteHalf(LinearVelocity.Y); w.WriteHalf(LinearVelocity.Z);
        w.WriteHalf(AngularVelocity.X); w.WriteHalf(AngularVelocity.Y); w.WriteHalf(AngularVelocity.Z);
        w.WriteByte((byte)Flags);
        if ((Flags & SnapshotFlags.HasLook) != 0)
        {
            w.WriteInt16(ToShort(Look.Yaw, MathF.PI * 4));
            w.WriteInt16(ToShort(Look.Pitch, MathF.PI));
        }
        if ((Flags & SnapshotFlags.HasInput) != 0) w.WriteUInt32(Input);
        if ((Flags & SnapshotFlags.HasState) != 0)
        {
            var state = State ?? Array.Empty<SyncedValue>();
            w.WriteUInt16((ushort)state.Length);
            foreach (var v in state)
            {
                w.WriteInt16(v.X); w.WriteInt16(v.Y); w.WriteInt16(v.Z);
                w.WriteByte(v.Field); w.WriteByte(v.Value);
            }
        }
    }

    /// <summary>How many bytes <see cref="Write"/> writes.</summary>
    public readonly int EncodedSize =>
        43 + ((Flags & SnapshotFlags.HasLook) != 0 ? 4 : 0) + ((Flags & SnapshotFlags.HasInput) != 0 ? 4 : 0)
           + ((Flags & SnapshotFlags.HasState) != 0 ? 2 + 8 * (State?.Length ?? 0) : 0);

    public static BodySnapshot Read(ref NetReader r)
    {
        var s = new BodySnapshot
        {
            Entity = EntityId.Read(ref r),
            Epoch = r.ReadUInt16(),
            Support = EntityId.Read(ref r),
            Position = r.ReadVector3(),
            Rotation = QuaternionCodec.Unpack(r.ReadUInt64()),
            LinearVelocity = new Vector3(r.ReadHalf(), r.ReadHalf(), r.ReadHalf()),
            AngularVelocity = new Vector3(r.ReadHalf(), r.ReadHalf(), r.ReadHalf()),
            Flags = (SnapshotFlags)r.ReadByte(),
        };
        if ((s.Flags & SnapshotFlags.HasLook) != 0)
            s.Look = new LookAngles(FromShort(r.ReadInt16(), MathF.PI * 4), FromShort(r.ReadInt16(), MathF.PI));
        if ((s.Flags & SnapshotFlags.HasInput) != 0) s.Input = r.ReadUInt32();
        if ((s.Flags & SnapshotFlags.HasState) != 0)
        {
            s.State = new SyncedValue[r.ReadUInt16()];
            for (int i = 0; i < s.State.Length; i++)
                s.State[i] = new SyncedValue(r.ReadInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadByte(), r.ReadByte());
        }
        return s;
    }

    private static short ToShort(float v, float range) => (short)System.Math.Clamp(MathF.Round(v / range * short.MaxValue), -short.MaxValue, short.MaxValue);
    private static float FromShort(short v, float range) => v / (float)short.MaxValue * range;
}

/// <summary>Smallest-three quaternion encoding: the index of the largest component in 2 bits, and the other three,
/// each within ±1/√2, in 20 bits each (64 bits in all). Good to about a millionth of a radian: coarser (10 bits, 0.1°)
/// made a ship wobble visibly, turning everything seen from its deck with it, and moving its blocks and body, which
/// are placed through the rotation from its block origin.</summary>
public static class QuaternionCodec
{
    private const float Range = 0.70710678f;
    private const int Bits = 20;
    private const ulong Max = (1UL << Bits) - 1;

    public static ulong Pack(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        Span<float> c = stackalloc float[] { q.X, q.Y, q.Z, q.W };
        int largest = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[largest])) largest = i;
        float sign = c[largest] < 0 ? -1f : 1f;
        ulong packed = (ulong)largest << 62;
        int shift = 2 * Bits;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            double v = System.Math.Clamp(c[i] * sign, -Range, Range);
            ulong q20 = (ulong)System.Math.Round((v + Range) / (2 * Range) * Max);
            packed |= q20 << shift;
            shift -= Bits;
        }
        return packed;
    }

    public static Quaternion Unpack(ulong packed)
    {
        int largest = (int)(packed >> 62);
        Span<float> c = stackalloc float[4];
        int shift = 2 * Bits;
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float v = (float)(((packed >> shift) & Max) / (double)Max * (2 * Range) - Range);
            c[i] = v;
            sum += v * v;
            shift -= Bits;
        }
        c[largest] = MathF.Sqrt(MathF.Max(0, 1 - sum));
        return Quaternion.Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
    }
}
