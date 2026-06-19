using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>Marks an entity as drawable with a given GPU mesh.</summary>
public struct MeshRenderer
{
    public GpuMesh Mesh;

    /// <summary>Opaque group-2 light bind group handle (per-chunk light buffer). 0 = shared full-bright.</summary>
    public nint LightBindGroup;
}

/// <summary>Tags the root entity of a dynamic voxel grid, carrying a reference to its data/body.</summary>
public struct DynamicGridComponent
{
    public DynamicGrid Grid;
}

/// <summary>Marks an entity as a camera. Only the first active camera is used for rendering.</summary>
public struct CameraComponent
{
    public Camera Camera;
    public bool Active;
}

/// <summary>Always renders the mesh as a wireframe overlay regardless of the global WireframeMode.</summary>
public struct WireframeRenderer
{
    public GpuMesh Mesh;
}

/// <summary>Renders the mesh in screen space (HUD pipeline: depth always passes, no depth write). Vertices are in NDC.</summary>
public struct HudRenderer
{
    public GpuMesh Mesh;
}

/// <summary>Free-fly camera control parameters and accumulated look angles.</summary>
public struct FreeFlyController
{
    public float MoveSpeed;        // units/sec
    public float LookSensitivity;  // radians per pixel
    public float Yaw;              // radians
    public float Pitch;            // radians
}
