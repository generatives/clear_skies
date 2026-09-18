using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Marks an entity as drawable with a given GPU mesh and per-volume lighting info.
/// <see cref="VolumeGpu"/> null → renderer falls back to the shared full-bright buffer.
/// </summary>
public struct MeshRenderer
{
    public GpuMesh Mesh;

    /// <summary>Owning volume's GPU resources. The renderer reads <c>VolumeGpu.RenderBindGroup</c> for the
    /// light buffer, and derives this chunk's voxel base + the volume dims from it live each draw. Null for
    /// non-chunk meshes (debug cubes, etc.) — uses the shared full-bright fallback.</summary>
    internal VolumeGpuResources? VolumeGpu;

    /// <summary>This chunk's position in the volume. The fragment-shader chunkBase and volSize are computed
    /// from this against <see cref="VolumeGpu"/> at draw time, so a volume reallocation (which moves every
    /// chunk's base and changes the dims) needs no remesh — the geometry is unchanged.</summary>
    public ChunkPosition ChunkPos;
}

/// <summary>Tags the root entity of a dynamic voxel grid, carrying a reference to its data/body.</summary>
public struct DynamicGridComponent
{
    public DynamicGrid Grid;
}

/// <summary>Marker flag: set on exactly one DynamicGrid's root entity at a time — the grid whose
/// blocks a UI action (e.g. Save) currently operates on. See <see cref="GridSelection"/> for the
/// invariant-preserving mutator.</summary>
public struct SelectedGridComponent
{
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

/// <summary>Free-fly (noclip) camera movement speed. See <see cref="MouseLookComponent"/> for the
/// look-angle state shared with the physics-driven walking mode.</summary>
public struct FreeFlyController
{
    public float MoveSpeed;
}

/// <summary>Accumulated mouse-look angles, shared by every camera movement mode (free-fly,
/// walking, grid-piloting) since they all just rotate the same camera Transform.</summary>
public struct MouseLookComponent
{
    public float Yaw;
    public float Pitch;
    public float LookSensitivity;
}

/// <summary>Tags the camera entity with its walking character body. See
/// <see cref="CharacterCameraSyncSystem"/> (pose readback) and
/// <see cref="PlayerMovementSystem"/> (input → motion goals).</summary>
public struct CharacterControllerComponent
{
    public PlayerCharacter Character;
    public float EyeHeight;
}

/// <summary>Movement-mode toggle on the camera entity: true = free-fly noclip (today's default
/// behaviour, no collision/gravity), false = physics-driven walking via
/// <see cref="CharacterControllerComponent"/>. Toggled with V (see PlayerMovementSystem).</summary>
public struct CharacterModeComponent
{
    public bool FreeFly;
}

/// <summary>Tag: set on exactly one DynamicGrid's root entity while GridPilotSystem is piloting it.
/// Read by AirshipFlightSystem to decide whether to take velocity targets from player input.</summary>
public struct PilotedComponent
{
}

/// <summary>Tag: set on the free-fly camera entity while GridPilotSystem is flying it along a
/// piloted DynamicGrid. PlayerInputSystem skips WASD/mouse-look for a camera carrying this
/// component.</summary>
public struct CameraGridFollowComponent
{
}
