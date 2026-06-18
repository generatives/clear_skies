using ClearSkies.Engine.Rendering;

namespace ClearSkies.Engine.ECS;

/// <summary>Marks an entity as drawable with a given GPU mesh.</summary>
public struct MeshRenderer
{
    public GpuMesh Mesh;
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
