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

/// <summary>An entity's full state, as a <see cref="Commands.Handlers.Spawn{TDescription}"/> carries it: written and
/// read the same way whether it's sent, stored or saved.</summary>
public interface IEntityDescription<TSelf> where TSelf : class, IEntityDescription<TSelf>
{
    void Write(NetWriter w);
    static abstract TSelf Read(ref NetReader r);
}

/// <summary>Descriptions to and from bytes, as they're stored and carried.</summary>
public static class DescriptionBytes
{
    public static byte[] Of<T>(T description) where T : class, IEntityDescription<T>
    {
        var w = new NetWriter();
        description.Write(w);
        return w.ToArray();
    }

    public static T Read<T>(ReadOnlySpan<byte> bytes) where T : class, IEntityDescription<T>
    {
        var r = new NetReader(bytes);
        return T.Read(ref r);
    }
}

/// <summary>
/// A grid's full state: everything needed to create it on any machine. Carried by a spawn, produced by describing a
/// live grid, and kept in storage. Presence layers (drawn, simulated) are never part of it: each machine works those
/// out for itself.
/// </summary>
public sealed class GridDescription : IEntityDescription<GridDescription>
{
    /// <summary>Where its block space (voxel (0,0,0)) is in the world, and its body's velocities (the linear one at its
    /// centre of mass, which the blocks determine).</summary>
    public BodyState Body;

    public bool Locked = true;

    public List<GridVoxel> Voxels = new();

    /// <summary>What its levers, wheels and toggles ask of it.</summary>
    public ShipControls Controls;

    /// <summary>What it's held to by anchors, and where it sits on each (see <see cref="AnchorLinks"/>). A grid held to
    /// another is spawned where its support (the first) puts it on that grid as it is now, if that's here, rather than at
    /// <see cref="Body"/>.</summary>
    public List<AnchorLink> Anchors = new();

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
        w.WriteBool(Controls.Anchored);
        AnchorLinks.Write(w, Anchors);
    }

    public static GridDescription Read(ref NetReader r)
    {
        var d = new GridDescription { Body = BodyState.Read(ref r), Locked = r.ReadBool() };
        using (var ms = new MemoryStream(r.ReadBytes().ToArray())) d.Voxels = GridSerializer.Read(ms);
        d.Controls = new ShipControls { Forward = r.ReadSingle(), Right = r.ReadSingle(), Up = r.ReadSingle(), Turn = r.ReadSingle() };
        if (r.Remaining == 0) return d; // saved before anchors
        d.Controls.Anchored = r.ReadBool();
        d.Anchors = AnchorLinks.Read(ref r);
        return d;
    }

    /// <summary>Just the <see cref="Anchors"/> of a described grid, without reading its blocks: what the Host and
    /// spawns waiting keep of a grid's description (a grid held to another comes and goes with it, see AnchorLinks).</summary>
    public static List<AnchorLink> ReadAnchors(ReadOnlySpan<byte> description)
    {
        var r = new NetReader(description);
        BodyState.Read(ref r);
        r.ReadBool();
        r.ReadBytes();
        for (int i = 0; i < 4; i++) r.ReadSingle();
        if (r.Remaining == 0) return new List<AnchorLink>();
        r.ReadBool();
        return AnchorLinks.Read(ref r);
    }

    // ── .grid files ─────────────────────────────────────────────────────────
    // A .grid file is "CSGF", a format version, then the description exactly as a spawn carries it: a saved ship
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

/// <summary>A player's full state, carried by a spawn, produced by describing a live player, and kept in storage.</summary>
public sealed class PlayerDescription : IEntityDescription<PlayerDescription>
{
    public PlayerId Id;
    public string Name = "";
    public bool FreeFly = true;
    public float FlySpeed = 10f;

    /// <summary>The character capsule's centre, in world space, and its velocity.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    public float Yaw, Pitch;

    /// <summary>What they stand on (a ship), if anything, and where on it: <see cref="Position"/> in its space (its
    /// Transform's). They spawn there on the ship as it is when they spawn, and only once it's loaded.</summary>
    public EntityId Support;
    public Vector3 LocalPosition;

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
        Support.Write(w);
        w.WriteVector3(LocalPosition);
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
        };
        if (r.Remaining == 0) return d; // saved before support was: standing on nothing
        d.Support = EntityId.Read(ref r);
        d.LocalPosition = r.ReadVector3();
        return d;
    }
}
