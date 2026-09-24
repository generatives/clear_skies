using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Draws every entity block that has a model (<see cref="BlockDef.IsEntityBlock"/> with a <see cref="BlockDef.Model"/>)
/// at its entity's <see cref="Transform"/> — kept on its cell, and on a moving ship, by the Hierarchy — lit from its
/// cell's voxel light through <see cref="BlockRef"/>, and posed by its <see cref="AnimatedModel"/> if it has one.
/// Static model blocks are drawn by <see cref="ChunkRenderSystem"/>, which leaves entity blocks out, so nothing is
/// drawn twice. Runs in <see cref="SystemStage.RenderWorld"/>.
/// </summary>
public sealed class BlockEntityRenderSystem : IRenderSystem, IDebugUiSystem
{
    private readonly EntitySet _blocks;
    private readonly EntitySet _levers;
    private readonly Renderer _renderer;
    private readonly BlockModelLibrary _models;
    private Mat4[] _pose = new Mat4[8];
    private int _drawn;

    // Debug-only: swings every lever's arm, to see node posing working before levers get behaviour.
    private float _testLeverAngle;

    public BlockEntityRenderSystem(World world, Renderer renderer, BlockModelLibrary models)
    {
        _renderer = renderer;
        _models   = models;
        _blocks   = world.GetEntities().With<BlockRef>().With<Transform>().AsSet();
        _levers   = world.GetEntities().With<BlockRef>().With<Lever>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        _drawn = 0;
        foreach (ref readonly Entity e in _blocks.GetEntities())
        {
            ref readonly var block = ref e.Get<BlockRef>();
            if (_models.Get(block.Id) is not { } model) continue;

            var world = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(world, model.BoundsMin, model.BoundsMax)) continue;

            ReadOnlySpan<Mat4> pose = default;
            if (e.Has<AnimatedModel>() && e.Get<AnimatedModel>().NodeRotations is { } rotations)
            {
                if (_pose.Length < model.Nodes.Count) _pose = new Mat4[model.Nodes.Count];
                model.ComputePose(_pose, rotations);
                pose = _pose.AsSpan(0, model.Nodes.Count);
            }

            // Lit from its own cell, like a static model block: the volume's grid, the cell's chunk and chunk-local cell.
            var p = block.Position;
            var chunk = new ChunkPosition(FloorDiv(p.X), FloorDiv(p.Y), FloorDiv(p.Z));
            var cell  = p - new Vector3D<int>(chunk.X, chunk.Y, chunk.Z) * ChunkData.Size;
            _renderer.DrawModel(model, world, block.Volume.Gpu.Index, chunk, cell, pose);
            _drawn++;
        }
    }

    private static int FloorDiv(int v) => (int)MathF.Floor((float)v / ChunkData.Size);

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Block Entities";

    public void DrawDebugUi()
    {
        ImGui.Text($"Block entities: {_blocks.Count:N0}, drawn this frame: {_drawn:N0}");
        ImGui.Separator();
        ImGui.TextDisabled("Test: node posing (levers have no behaviour yet)");
        if (ImGui.SliderAngle("Lever arm angle", ref _testLeverAngle, -60f, 60f))
        {
            var rotation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitZ, _testLeverAngle);
            foreach (ref readonly Entity e in _levers.GetEntities())
                e.Set(new AnimatedModel { NodeRotations = new() { ["arm_group"] = rotation } });
        }
    }
}
