using System.Numerics;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Entities;

/// <summary>A body's pose and velocity in world space.</summary>
public struct BodyState
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;

    public static BodyState At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity };

    public readonly void Write(NetWriter w)
    {
        w.WriteVector3(Position);
        w.WriteQuaternion(Rotation);
        w.WriteVector3(LinearVelocity);
        w.WriteVector3(AngularVelocity);
    }

    public static BodyState Read(ref NetReader r) => new()
    {
        Position = r.ReadVector3(),
        Rotation = r.ReadQuaternion(),
        LinearVelocity = r.ReadVector3(),
        AngularVelocity = r.ReadVector3(),
    };
}

/// <summary>
/// A grid's full state: everything needed to create it on any machine. Carried by SpawnGrid, produced by describing a
/// live grid, and kept in storage. Presence layers (drawn, simulated) are never part of it: each machine works those
/// out for itself.
/// </summary>
public sealed class GridDescription
{
    /// <summary>Its body (the centre of mass) in the world.</summary>
    public BodyState Body;

    /// <summary>Where the body sits in the grid's own voxel space (see ChunkVolume.Pivot).</summary>
    public Vector3 Pivot;

    public bool Locked = true;

    public List<GridVoxel> Voxels = new();

    /// <summary>Lever settings and wheel angles, by cell.</summary>
    public List<(Vector3D<int> Cell, float Value)> Levers = new();
    public List<(Vector3D<int> Cell, float Angle)> Wheels = new();

    /// <summary>A new grid of <paramref name="voxels"/> whose bounding box centre is at <paramref name="position"/>.</summary>
    public static GridDescription FromVoxels(Vector3 position, IEnumerable<GridVoxel> voxels)
    {
        var d = new GridDescription { Body = BodyState.At(position), Voxels = voxels.Where(v => v.Id != BlockId.Air).ToList() };
        d.Pivot = BoundsCentre(d.Voxels);
        return d;
    }

    /// <summary>Centre of the voxels' bounding box (each voxel spans [v, v+1]), or zero if there are none.</summary>
    public static Vector3 BoundsCentre(IReadOnlyList<GridVoxel> voxels)
    {
        if (voxels.Count == 0) return Vector3.Zero;
        int nx = int.MaxValue, ny = int.MaxValue, nz = int.MaxValue;
        int xx = int.MinValue, xy = int.MinValue, xz = int.MinValue;
        foreach (var v in voxels)
        {
            nx = System.Math.Min(nx, v.X); xx = System.Math.Max(xx, v.X);
            ny = System.Math.Min(ny, v.Y); xy = System.Math.Max(xy, v.Y);
            nz = System.Math.Min(nz, v.Z); xz = System.Math.Max(xz, v.Z);
        }
        return new Vector3(nx + xx + 1, ny + xy + 1, nz + xz + 1) * 0.5f;
    }

    public void Write(NetWriter w)
    {
        Body.Write(w);
        w.WriteVector3(Pivot);
        w.WriteBool(Locked);
        using (var ms = new MemoryStream())
        {
            GridSerializer.Write(ms, Voxels);
            w.WriteBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        w.WriteVarUInt((uint)Levers.Count);
        foreach (var (cell, value) in Levers) { WriteCell(w, cell); w.WriteSingle(value); }
        w.WriteVarUInt((uint)Wheels.Count);
        foreach (var (cell, angle) in Wheels) { WriteCell(w, cell); w.WriteSingle(angle); }
    }

    public static GridDescription Read(ref NetReader r)
    {
        var d = new GridDescription { Body = BodyState.Read(ref r), Pivot = r.ReadVector3(), Locked = r.ReadBool() };
        using (var ms = new MemoryStream(r.ReadBytes().ToArray())) d.Voxels = GridSerializer.Read(ms);
        uint levers = r.ReadVarUInt();
        for (int i = 0; i < levers; i++) d.Levers.Add((ReadCell(ref r), r.ReadSingle()));
        uint wheels = r.ReadVarUInt();
        for (int i = 0; i < wheels; i++) d.Wheels.Add((ReadCell(ref r), r.ReadSingle()));
        return d;
    }

    private static void WriteCell(NetWriter w, Vector3D<int> c) { w.WriteInt32(c.X); w.WriteInt32(c.Y); w.WriteInt32(c.Z); }
    private static Vector3D<int> ReadCell(ref NetReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
}

/// <summary>A player's full state, carried by SpawnPlayer, produced by describing a live player, and kept in storage.</summary>
public sealed class PlayerDescription
{
    internal object MemberwiseCopy() => MemberwiseClone();

    public PlayerId Id;
    public string Name = "";
    public bool FreeFly = true;
    public float FlySpeed = 10f;

    /// <summary>The character capsule's centre, in world space, and its velocity. A player standing on a ship is saved
    /// at their world position; the support system picks the ship up again on their first tick there.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    public float Yaw, Pitch;

    /// <summary>What they were standing on or riding with (a network ID; 0 for none), and where on it. A returning
    /// player is put back there, wherever the ship has gone since.</summary>
    public uint Support;
    public Vector3 SupportPosition;

    public void Write(NetWriter w)
    {
        w.WriteGuid(Id.Value);
        w.WriteString(Name);
        w.WriteBool(FreeFly);
        w.WriteSingle(FlySpeed);
        w.WriteVector3(Position);
        w.WriteVector3(Velocity);
        w.WriteSingle(Yaw);
        w.WriteSingle(Pitch);
        w.WriteUInt32(Support);
        if (Support != 0) w.WriteVector3(SupportPosition);
    }

    public static PlayerDescription Read(ref NetReader r)
    {
        var d = new PlayerDescription
        {
            Id = new PlayerId(r.ReadGuid()),
            Name = r.ReadString(),
            FreeFly = r.ReadBool(),
            FlySpeed = r.ReadSingle(),
            Position = r.ReadVector3(),
            Velocity = r.ReadVector3(),
            Yaw = r.ReadSingle(),
            Pitch = r.ReadSingle(),
            Support = r.ReadUInt32(),
        };
        if (d.Support != 0) d.SupportPosition = r.ReadVector3();
        return d;
    }

    /// <summary>Where to put the player: on their support if it's here, else their saved world position.</summary>
    public Vector3 ResolvePosition(NetRegistry registry)
    {
        if (Support == 0 || !registry.TryGet(Support, out var s) || !s.Has<ECS.Transform>()) return Position;
        ref readonly var t = ref s.Get<ECS.Transform>();
        var rotation = new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        return new Vector3(t.Position.X, t.Position.Y, t.Position.Z) + Vector3.Transform(SupportPosition, rotation);
    }
}
