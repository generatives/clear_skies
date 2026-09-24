using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Gives each new block entity whose block has a model (<see cref="BlockDef.Model"/>) what
/// <see cref="ModelRenderSystem"/> needs to draw it: a <see cref="ModelRenderer"/> with the block type's shared
/// model, <see cref="VoxelLit"/> to light it from its own cell, and its own <see cref="AnimatedModel"/> so it can be
/// posed independently of every other block of its type. Done here rather than when <see cref="ChunkVolume"/>
/// creates the entity because loading a model needs the renderer. Runs once per new entity, in
/// <see cref="SystemStage.PreRender"/> so a block placed this frame is drawn this frame.
/// </summary>
public sealed class BlockModelSystem : ISystem
{
    private readonly EntitySet _newBlocks;
    private readonly BlockModelLibrary _models;

    public BlockModelSystem(World world, BlockModelLibrary models)
    {
        _models    = models;
        _newBlocks = world.GetEntities().WhenAdded<BlockRef>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _newBlocks.GetEntities())
        {
            ref readonly var block = ref e.Get<BlockRef>();
            if (_models.Get(block.Id) is not { } model) continue; // no model, or it failed to load

            var p = block.Position;
            var chunk = new ChunkPosition(FloorDiv(p.X), FloorDiv(p.Y), FloorDiv(p.Z));
            e.Set(new ModelRenderer { Model = model });
            e.Set(new VoxelLit
            {
                Grid  = block.Volume.Gpu,
                Chunk = chunk,
                Cell  = p - new Vector3D<int>(chunk.X, chunk.Y, chunk.Z) * ChunkData.Size,
            });
            e.Set(AnimatedModel.For(model));
        }
        _newBlocks.Complete();
    }

    private static int FloorDiv(int v) => (int)MathF.Floor((float)v / ChunkData.Size);
}
