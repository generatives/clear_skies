using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Startup;
using ClearSkies.Net.Protocol;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class ReplicationTests
{
    [Fact]
    public void ComponentsGetIdsInRegistrationOrder()
    {
        var components = new ReplicatedComponents();
        components.Register<Fan>((in Fan f, NetWriter w) => w.WriteSingle(f.Thrust), (ref Fan f, ref NetReader r) => f.Thrust = r.ReadSingle());
        components.Register<Buoyant>((in Buoyant _, NetWriter _) => { }, (ref Buoyant _, ref NetReader _) => { });
        Assert.Equal((ushort)0, components.All[0].Id);
        Assert.Equal((ushort)1, components.All[1].Id);
        Assert.Throws<ArgumentException>(() =>
            components.Register<Fan>((in Fan _, NetWriter _) => { }, (ref Fan _, ref NetReader _) => { }));

        var reordered = new ReplicatedComponents();
        reordered.Register<Buoyant>((in Buoyant _, NetWriter _) => { }, (ref Buoyant _, ref NetReader _) => { });
        reordered.Register<Fan>((in Fan _, NetWriter _) => { }, (ref Fan _, ref NetReader _) => { });
        Assert.NotEqual(components.Checksum, reordered.Checksum);
        Assert.Equal(GameReplication.Checksum, GameReplication.Checksum);
    }

    [Fact]
    public void AComponentWritesAndReadsItself()
    {
        using var world = new World();
        var components = new ReplicatedComponents();
        GameReplication.Register(components);
        var fan = components.All.Single();
        var from = world.CreateEntity();
        from.Set(new Fan { Thrust = 0.5f });
        var w = new NetWriter();
        Assert.True(fan.TryWrite(from, w));
        Assert.False(fan.TryWrite(world.CreateEntity(), new NetWriter())); // no Fan

        var to = world.CreateEntity();
        to.Set(new Fan());
        var r = new NetReader(w.Written);
        fan.Read(to, ref r);
        Assert.Equal(0.5f, to.Get<Fan>().Thrust, 2);
        Assert.True(r.AtEnd);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, -3, 1000)]
    [InlineData(-1_000_000, 1_000_000, -1)]
    public void CellsRoundTripThroughKeys(int x, int y, int z)
    {
        ulong key = ChunkVolume.CellKey(new Vector3D<int>(x, y, z));
        Assert.NotEqual(0UL, key); // 0 is the root
        Assert.Equal(new Vector3D<int>(x, y, z), ChunkVolume.KeyCell(key));
    }

    [Fact]
    public void StateRoundTripsAndFramesStayUnderTheirBudget()
    {
        var states = new List<EntityState>();
        for (int i = 0; i < 5; i++)
        {
            var s = new EntityState { Root = new EntityId((uint)(i + 1)) };
            for (int c = 0; c < 60; c++) s.Components.Add(new ComponentState((ulong)c, 3, new byte[] { (byte)c, 7 }));
            states.Add(s);
        }
        var read = new List<EntityState>();
        int packets = 0;
        EntityState.WriteFrames(new NetWriter(), 9, states, packet =>
        {
            Assert.True(packet.Length <= EntityState.MaxFrameBytes + 7); // plus the frame's kind, tick and count
            var r = new NetReader(packet);
            Assert.Equal((byte)MessageKind.EntityStateFrame, r.ReadByte());
            var frame = new List<EntityState>();
            Assert.Equal(9u, EntityState.ReadFrame(ref r, frame));
            Assert.True(r.AtEnd);
            read.AddRange(frame);
            packets++;
        });
        Assert.Equal(5, read.Count);
        Assert.True(packets > 1);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(states[i].Root, read[i].Root);
            Assert.Equal(states[i].Components.Count, read[i].Components.Count);
            Assert.Equal(states[i].Components[59].Key, read[i].Components[59].Key);
            Assert.Equal(states[i].Components[59].Data, read[i].Components[59].Data);
        }
    }

    [Fact]
    public void AFansThrustReachesAnotherMachine()
    {
        using var game = new LoopbackGame(latencyMs: 50);
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 4; x++) for (int z = 0; z < 4; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        voxels.Add(new(1, 1, 1, BlockId.Fan, BlockOrientation.Upright));
        var ship = game.Host.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 50, 0), voxels));
        game.Tick(2);
        var (client, _) = game.Join();
        game.Tick(30);
        var id = ship.Get<EntityId>();

        ship.Get<ChunkGrid>().Volume.TryGetBlockEntity(1, 1, 1, out var hostFan);
        hostFan.Get<Fan>().Thrust = 0.6f;
        game.Tick(40); // past the client's playback delay
        Assert.True(client.Registry.Find(id)!.Value.Get<ChunkGrid>().Volume.TryGetBlockEntity(1, 1, 1, out var clientFan));
        Assert.Equal(0.6f, clientFan.Get<Fan>().Thrust, 2);

        hostFan.Get<Fan>().Thrust = 0f;
        game.Tick(40);
        Assert.Equal(0f, clientFan.Get<Fan>().Thrust, 2);
    }
}
