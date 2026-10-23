using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Ships' chunks in the chunk pipeline: a grid each, wanted as they come and go, their blocks the volume's.</summary>
public class PipelineShipTests
{
    private static (World World, ChunkPipelineSystem System) Pipeline()
    {
        var world = new World();
        return (world, new ChunkPipelineSystem(world, new ChunkPipeline(work => work())));
    }

    private static Entity Ship(World world, params GridVoxel[] voxels)
    {
        var d = new GridDescription { Body = BodyState.At(Vector3.Zero) };
        d.Voxels.AddRange(voxels);
        return DynamicGridFactory.Create(world, new EntityId(5), d);
    }

    private static GridVoxel Block(int x, int y, int z) => new(x, y, z, BlockId.Stone, BlockOrientation.Upright);

    [Fact]
    public void A_ships_chunks_are_wanted_in_a_grid_of_its_own_with_its_volumes_blocks()
    {
        var (world, system) = Pipeline();
        var ship = Ship(world, Block(0, 0, 0), Block(40, 0, 0));
        system.Update(1 / 60f);

        var volume = ship.Get<ChunkGrid>().Volume;
        var records = system.Pipeline.Records.ToList();
        Assert.Equal(2, records.Count);
        var grid = records[0].Grid;
        Assert.True(grid.FromVolume);
        Assert.Same(volume, grid.Volume);
        foreach (var record in records)
            Assert.Same(volume.GetData(record.Key.Pos), record.Output<ChunkVoxels>(system.ShipBlocks)!.Data);
    }

    /// <summary>Counts its runs on the blocks it reads.</summary>
    private sealed class Counter(Stage blocks) : WorkerStage<object>
    {
        public int Runs;
        public override IReadOnlyList<Input> InputsFor(PipelineGrid grid) => new[] { new Input(blocks, Relation.Self) };
        public override object Make(ChunkKey key, StageInputs inputs) { Runs++; return new object(); }
    }

    [Fact]
    public void An_edit_to_a_ship_runs_what_reads_its_blocks_again()
    {
        var (world, system) = Pipeline();
        var counter = system.Pipeline.Add(new Counter(system.ShipBlocks));
        var ship = Ship(world, Block(0, 0, 0));
        for (int i = 0; i < 3; i++) system.Update(1 / 60f);
        Assert.Equal(1, counter.Runs);

        ship.Get<ChunkGrid>().Volume.SetBlock(1, 0, 0, BlockId.Wood);
        for (int i = 0; i < 3; i++) system.Update(1 / 60f);
        Assert.Equal(2, counter.Runs);
        var record = system.Pipeline.Records.Single();
        Assert.Equal(BlockId.Wood, record.Output<ChunkVoxels>(system.ShipBlocks)!.Data!.Get(1, 0, 0)); // the volume's, edited in place
    }

    [Fact]
    public void A_ship_that_goes_takes_its_grid_and_chunks_with_it()
    {
        var (world, system) = Pipeline();
        var ship = Ship(world, Block(0, 0, 0), Block(40, 0, 0));
        system.Update(1 / 60f);
        var grid = system.Pipeline.Records.First().Grid;

        Hierarchy.DestroyRecursive(ship);
        system.Update(1 / 60f);
        Assert.Empty(system.Pipeline.Records);
        Assert.True(grid.Removed);
    }
}
