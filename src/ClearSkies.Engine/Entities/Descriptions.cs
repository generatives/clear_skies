using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Entities;

/// <summary>A pose and velocity in world space.</summary>
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
    /// <summary>Where its block space (voxel (0,0,0)) is in the world, and its body's velocities (the linear one at its
    /// centre of mass, which the blocks determine).</summary>
    public BodyState Body;

    public bool Locked = true;

    public List<GridVoxel> Voxels = new();

    /// <summary>What its levers and wheels ask of it.</summary>
    public ShipControls Controls;

    /// <summary>A new grid of <paramref name="voxels"/> whose bounding box centre is at <paramref name="position"/>.</summary>
    public static GridDescription FromVoxels(Vector3 position, IEnumerable<GridVoxel> voxels)
    {
        var solid = voxels.Where(v => v.Id != BlockId.Air).ToList();
        return new GridDescription { Body = BodyState.At(position - BoundsCentre(solid)), Voxels = solid };
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
        w.WriteBool(Locked);
        using (var ms = new MemoryStream())
        {
            GridSerializer.Write(ms, Voxels);
            w.WriteBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        w.WriteSingle(Controls.Forward);
        w.WriteSingle(Controls.Right);
        w.WriteSingle(Controls.Up);
        w.WriteSingle(Controls.Turn);
    }

    public static GridDescription Read(ref NetReader r)
    {
        var d = new GridDescription { Body = BodyState.Read(ref r), Locked = r.ReadBool() };
        using (var ms = new MemoryStream(r.ReadBytes().ToArray())) d.Voxels = GridSerializer.Read(ms);
        d.Controls = new ShipControls { Forward = r.ReadSingle(), Right = r.ReadSingle(), Up = r.ReadSingle(), Turn = r.ReadSingle() };
        return d;
    }

    // ── .grid files ─────────────────────────────────────────────────────────
    // A .grid file is "CSGF", a format version, then the description exactly as SpawnGrid carries it: a saved ship
    // is the same thing as a ship spawned, sent or stored, and gains whatever descriptions gain.

    private static readonly byte[] FileMagic = { (byte)'C', (byte)'S', (byte)'G', (byte)'F' };
    private const ushort FileVersion = 1;

    public void SaveFile(string path)
    {
        var w = new NetWriter();
        w.WriteRaw(FileMagic);
        w.WriteUInt16(FileVersion);
        Write(w);
        File.WriteAllBytes(path, w.ToArray());
    }

    public static GridDescription LoadFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < FileMagic.Length + 2 || !bytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            throw new InvalidDataException($"Not a ClearSkies grid file (saved by an older build?): {path}");
        var r = new NetReader(bytes);
        r.ReadRaw(FileMagic.Length);
        ushort version = r.ReadUInt16();
        if (version != FileVersion) throw new InvalidDataException($"Unsupported grid file version {version}: {path}");
        try { return Read(ref r); }
        catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException($"Corrupt grid file: {path}", e); }
    }
}

/// <summary>A player's full state, carried by SpawnPlayer, produced by describing a live player, and kept in storage.</summary>
public sealed class PlayerDescription
{
    public PlayerId Id;
    public string Name = "";
    public bool FreeFly = true;
    public float FlySpeed = 10f;

    /// <summary>The character capsule's centre, in world space, and its velocity. A player standing on a ship is saved
    /// at their world position; the support system picks the ship up again on their first tick there.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    public float Yaw, Pitch;

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
    }

    public static PlayerDescription Read(ref NetReader r) => new()
    {
        Id = new PlayerId(r.ReadGuid()),
        Name = r.ReadString(),
        FreeFly = r.ReadBool(),
        FlySpeed = r.ReadSingle(),
        Position = r.ReadVector3(),
        Velocity = r.ReadVector3(),
        Yaw = r.ReadSingle(),
        Pitch = r.ReadSingle(),
    };
}
