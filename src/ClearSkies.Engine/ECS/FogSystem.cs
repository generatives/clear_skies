using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Hides the edge of the loaded world: sets the fog (<see cref="SkySettings.FogDistance"/>) around the view, the first
/// terrain interest with a draw radius, at the nearest terrain that isn't ready to be seen. That's a column still
/// loading (<see cref="TerrainColumnLoading"/>), a terrain chunk not yet uploaded to the GPU store or meshed, or past
/// how far streaming has looked (<see cref="TerrainScanned"/>); with none, the draw radius. So an island only partly
/// loaded fades out where loading stopped instead of ending in a hard edge, and chunks appear behind the fog rather
/// than popping in in front of it. It closes in fast, so a gap is covered before it shows, and opens out slowly, so
/// the view opens up gently as loading catches up.
/// </summary>
public sealed class FogSystem : ISystem, IDebugUiSystem
{
    private const int S = ChunkData.Size;

    private readonly float _viewDistance;
    private readonly EntitySet _interests;
    private readonly EntitySet _loadingColumns;
    private readonly EntitySet _chunksNotDrawnYet;
    private float _distance;
    private float _target;

    /// <param name="viewDistance">The farthest terrain is drawn (the same limit as <see cref="ChunkLoadSystem"/>'s).</param>
    public FogSystem(World world, float viewDistance)
    {
        _viewDistance = viewDistance;
        _interests = world.GetEntities().With<TerrainInterest>().With<Transform>().AsSet();
        _loadingColumns = world.GetEntities().With<TerrainColumnLoading>().AsSet();
        // Terrain chunks (each with its own presence; a grid's chunks inherit theirs) waiting to be uploaded or meshed.
        _chunksNotDrawnYet = world.GetEntities().With<Chunk>().With<OwnPresence>()
                            .WithEither<NeedsGpuUploadFlag>().Or<NeedsRemeshFlag>().AsSet();
    }

    /// <summary>Horizontal distance from the view at which the loaded world stops, eased over time. Fog is total by here.</summary>
    public float Distance => _distance;

    public void Update(float dt)
    {
        if (!TryView(out var centre, out float radius, out float scanned)) return;

        // Only what's within the draw radius counts: a column loaded beyond it for colliders is never drawn.
        float target = MathF.Min(radius, scanned);
        foreach (ref readonly Entity e in _loadingColumns.GetEntities())
        {
            ref readonly var c = ref e.Get<TerrainColumnLoading>();
            target = MathF.Min(target, Within(centre, c.X, c.Z, radius));
        }
        foreach (ref readonly Entity e in _chunksNotDrawnYet.GetEntities())
        {
            var pos = e.Get<Chunk>().Entry.Position;
            target = MathF.Min(target, Within(centre, pos.X, pos.Z, radius));
        }
        _target = target;

        float rate = target < _distance ? 8f : 1f;
        _distance += (target - _distance) * (1f - MathF.Exp(-rate * dt));
        SkySettings.SetFogDistance(_distance);
    }

    private bool TryView(out Vector3D<float> centre, out float radius, out float scanned)
    {
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            float draw = MathF.Min(e.Get<TerrainInterest>().DrawRadius, _viewDistance);
            if (draw <= 0) continue;
            centre = e.Get<Transform>().Position;
            radius = draw;
            // Nothing scanned yet (just arrived): none of it is known to be ready.
            scanned = e.Has<TerrainScanned>() ? e.Get<TerrainScanned>().Radius : 0f;
            return true;
        }
        (centre, radius, scanned) = (default, 0f, 0f);
        return false;
    }

    /// <summary>Horizontal distance from the centre to the nearest point of chunk column (x, z), if its middle is within
    /// <paramref name="radius"/> (so it's drawn); infinity otherwise.</summary>
    private static float Within(Vector3D<float> centre, int x, int z, float radius)
    {
        float mx = x * S + S * 0.5f - centre.X, mz = z * S + S * 0.5f - centre.Z;
        if (mx * mx + mz * mz > radius * radius) return float.PositiveInfinity;
        float dx = MathF.Max(0f, MathF.Abs(mx) - S * 0.5f);
        float dz = MathF.Max(0f, MathF.Abs(mz) - S * 0.5f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Fog";

    public void DrawDebugUi()
    {
        ImGui.Text($"Fog distance: {_distance:F0} (target {_target:F0})");
        ImGui.Text($"Columns loading: {_loadingColumns.Count}   Terrain chunks not drawn yet: {_chunksNotDrawnYet.Count}");
    }
}
