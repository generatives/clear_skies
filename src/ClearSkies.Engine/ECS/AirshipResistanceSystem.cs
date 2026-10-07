using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Works out each grid's <see cref="ResistsAir"/> entries from its blocks (the component itself comes with the grid, see
/// DynamicGridFactory): for each of the 6 body axis directions, the area of the
/// faces first hit along it (one m² per column of blocks with any solid block in it) and the area-weighted centre of
/// those faces, relative to the centre of mass. Redone whenever the grid's body gets a new shape (PhysicsBodySystem builds
/// one on every edit), since the centre of mass moves too; a full scan of the grid each time.
/// </summary>
public sealed class AirshipResistanceSystem : ISystem
{
    private readonly PhysicsWorld _physics;
    private readonly EntitySet _grids;

    // Per axis: each column (keyed by its other two coordinates) and its lowest and highest solid cell along the axis.
    private readonly Dictionary<long, (int Min, int Max)>[] _columns = { new(), new(), new() };

    public AirshipResistanceSystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _grids = world.GetEntities().With<ResistsAir>().With<DynamicGrid>().With<ChunkGrid>().With<PhysicsBodyComponent>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            ref readonly var pb = ref e.Get<PhysicsBodyComponent>();
            int key = (int)_physics.GetBodyShape(pb.Body).Packed;
            ref var air = ref e.Get<ResistsAir>();
            if (air.ShapeKey == key) continue;
            air.Faces = Derive(e.Get<ChunkGrid>().Volume, PhysicsConv.ToBepu(pb.Offset));
            air.ShapeKey = key;
        }
    }

    /// <summary>A grid's drag entries, with centres relative to <paramref name="centreOfMass"/> (block space).</summary>
    public AirFace[] Derive(ChunkVolume volume, Vector3 centreOfMass)
    {
        foreach (var c in _columns) c.Clear();
        const int S = ChunkData.Size;
        foreach (var (pos, entry) in volume.All)
        {
            var data = entry.Data;
            if (!data.HasAnyColliding()) continue;
            int ox = pos.X * S, oy = pos.Y * S, oz = pos.Z * S;
            for (int z = 0; z < S; z++)
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                if (!BlockRegistry.Get(data.Get(x, y, z)).Collides) continue;
                int gx = ox + x, gy = oy + y, gz = oz + z;
                Extend(_columns[0], Key(gy, gz), gx);
                Extend(_columns[1], Key(gx, gz), gy);
                Extend(_columns[2], Key(gx, gy), gz);
            }
        }

        var faces = new AirFace[6];
        for (int axis = 0; axis < 3; axis++)
        {
            var plus = Vector3.Zero;
            var minus = Vector3.Zero;
            foreach (var (k, (min, max)) in _columns[axis])
            {
                // The column's centre on the other two axes, and its two end faces along this one.
                float a = (int)(k >> 32) + 0.5f, b = (int)k + 0.5f;
                plus += Place(axis, max + 1, a, b);
                minus += Place(axis, min, a, b);
            }
            int n = _columns[axis].Count;
            if (n == 0) continue;
            faces[ResistsAir.Index(axis, true)]  = new AirFace { Area = n, Centroid = plus / n - centreOfMass };
            faces[ResistsAir.Index(axis, false)] = new AirFace { Area = n, Centroid = minus / n - centreOfMass };
        }
        return faces;
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    private static void Extend(Dictionary<long, (int Min, int Max)> columns, long key, int v)
    {
        if (columns.TryGetValue(key, out var r)) columns[key] = (System.Math.Min(r.Min, v), System.Math.Max(r.Max, v));
        else columns[key] = (v, v);
    }

    // A point at `along` on `axis`, with the column's other two coordinates in axis order (x: y, z; y: x, z; z: x, y).
    private static Vector3 Place(int axis, float along, float a, float b) => axis switch
    {
        0 => new Vector3(along, a, b),
        1 => new Vector3(a, along, b),
        _ => new Vector3(a, b, along),
    };
}
