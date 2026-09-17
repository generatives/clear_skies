using ClearSkies.Engine.Math;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Shared "find the active camera" + "compute a spawn point in front of it" helpers, factored out of
/// PlayerInputSystem so both the G-key single-block spawn and Load-from-file use identical placement.
/// </summary>
public static class CameraUtil
{
    /// <summary>Finds the entity in <paramref name="cameras"/> with <c>CameraComponent.Active == true</c>
    /// and returns its Transform. <paramref name="cameras"/> must be queried with at least
    /// <c>With&lt;Transform&gt;().With&lt;CameraComponent&gt;()</c>.</summary>
    public static bool TryGetActive(EntitySet cameras, out Transform transform)
    {
        foreach (ref readonly Entity e in cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (cc.Active) { transform = e.Get<Transform>(); return true; }
        }
        transform = default;
        return false;
    }

    /// <summary>World point 3 blocks in front of <paramref name="camera"/> — the same placement the
    /// G-key spawn has always used.</summary>
    public static Vector3D<float> SpawnPointInFrontOf(Transform camera) =>
        camera.Position + Vec.Rotate(camera.Rotation, new Vector3D<float>(0, 0, -3));
}
