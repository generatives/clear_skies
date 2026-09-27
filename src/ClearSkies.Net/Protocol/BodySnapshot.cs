using System.Numerics;
using ClearSkies.Engine.Serialization;

namespace ClearSkies.Net.Protocol;

[Flags]
public enum SnapshotFlags : byte
{
    None = 0,
    /// <summary>At rest: receivers stop extrapolating.</summary>
    Sleeping = 1,
    /// <summary>A <see cref="LookAngles"/> streamed value follows.</summary>
    HasLook = 2,
}

/// <summary>A player's look direction, streamed with their body.</summary>
public readonly record struct LookAngles(float Yaw, float Pitch);

/// <summary>
/// One body's pose and velocity at a tick, relative to what supports it (world space with no support), with any
/// streamed values. About 50 bytes: the rotation is smallest-three encoded in 32 bits and velocities are halves.
/// </summary>
public struct BodySnapshot
{
    public uint Entity;
    public ushort Epoch;
    /// <summary>For grids, the event number of the last shape change: receivers hold the snapshot until they've
    /// applied that event, because the body's origin (centre of mass) moves when blocks change.</summary>
    public uint ShapeVersion;
    public uint Support;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 LinearVelocity, AngularVelocity;
    public SnapshotFlags Flags;
    public LookAngles Look;

    public readonly void Write(NetWriter w)
    {
        w.WriteUInt32(Entity);
        w.WriteUInt16(Epoch);
        w.WriteUInt32(ShapeVersion);
        w.WriteUInt32(Support);
        w.WriteVector3(Position);
        w.WriteUInt32(QuaternionCodec.Pack(Rotation));
        w.WriteHalf(LinearVelocity.X); w.WriteHalf(LinearVelocity.Y); w.WriteHalf(LinearVelocity.Z);
        w.WriteHalf(AngularVelocity.X); w.WriteHalf(AngularVelocity.Y); w.WriteHalf(AngularVelocity.Z);
        w.WriteByte((byte)Flags);
        if ((Flags & SnapshotFlags.HasLook) != 0)
        {
            w.WriteInt16(ToShort(Look.Yaw, MathF.PI * 4));
            w.WriteInt16(ToShort(Look.Pitch, MathF.PI));
        }
    }

    public static BodySnapshot Read(ref NetReader r)
    {
        var s = new BodySnapshot
        {
            Entity = r.ReadUInt32(),
            Epoch = r.ReadUInt16(),
            ShapeVersion = r.ReadUInt32(),
            Support = r.ReadUInt32(),
            Position = r.ReadVector3(),
            Rotation = QuaternionCodec.Unpack(r.ReadUInt32()),
            LinearVelocity = new Vector3(r.ReadHalf(), r.ReadHalf(), r.ReadHalf()),
            AngularVelocity = new Vector3(r.ReadHalf(), r.ReadHalf(), r.ReadHalf()),
            Flags = (SnapshotFlags)r.ReadByte(),
        };
        if ((s.Flags & SnapshotFlags.HasLook) != 0)
            s.Look = new LookAngles(FromShort(r.ReadInt16(), MathF.PI * 4), FromShort(r.ReadInt16(), MathF.PI));
        return s;
    }

    private static short ToShort(float v, float range) => (short)System.Math.Clamp(MathF.Round(v / range * short.MaxValue), -short.MaxValue, short.MaxValue);
    private static float FromShort(short v, float range) => v / (float)short.MaxValue * range;
}

/// <summary>Smallest-three quaternion encoding: the index of the largest component in 2 bits, and the other three,
/// each within ±1/√2, in 10 bits each. Good to about 0.1°.</summary>
public static class QuaternionCodec
{
    private const float Range = 0.70710678f;
    private const int Bits = 10, Max = (1 << Bits) - 1;

    public static uint Pack(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        Span<float> c = stackalloc float[] { q.X, q.Y, q.Z, q.W };
        int largest = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[largest])) largest = i;
        float sign = c[largest] < 0 ? -1f : 1f;
        uint packed = (uint)largest << 30;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float v = System.Math.Clamp(c[i] * sign, -Range, Range);
            uint q10 = (uint)MathF.Round((v + Range) / (2 * Range) * Max);
            packed |= q10 << shift;
            shift -= Bits;
        }
        return packed;
    }

    public static Quaternion Unpack(uint packed)
    {
        int largest = (int)(packed >> 30);
        Span<float> c = stackalloc float[4];
        int shift = 20;
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float v = ((packed >> shift) & Max) / (float)Max * (2 * Range) - Range;
            c[i] = v;
            sum += v * v;
            shift -= Bits;
        }
        c[largest] = MathF.Sqrt(MathF.Max(0, 1 - sum));
        return Quaternion.Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
    }
}
