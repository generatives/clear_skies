using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;
using Xunit.Abstractions;

namespace ClearSkies.Tests;

/// <summary>How smoothly a flying ship, and the crew on it, are drawn: frame by frame, as the game draws them, with a
/// host and a client each at 60 fps with a little noise in their frame times.</summary>
public class ShipDrawingTests
{
    private readonly ITestOutputHelper _out;
    public ShipDrawingTests(ITestOutputHelper output) => _out = output;

    private const float Speed = 6f; // blocks a second, along X

    private static Vector3 V(Vector3D<float> v) => new(v.X, v.Y, v.Z);
    private static Quaternion Q(Quaternion<float> q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>What was drawn over five seconds of flight (flying east and turning), once things have settled.</summary>
    private sealed class Flight
    {
        /// <summary>The ship's centre of mass as the client draws it, less where steady motion puts it by then: a frame
        /// that took longer should move it further, so only changes in this are jitter.</summary>
        public readonly List<float> ClientShipError = new();
        /// <summary>The client's player (their camera), in the ship's space as the client draws it.</summary>
        public readonly List<Vector3> CameraOnDeck = new();
        /// <summary>The client's player as the host draws them, in the ship's space as the host draws it.</summary>
        public readonly List<Vector3> CrewOnHostDeck = new();
        public bool StillAboard;
    }

    private static Flight Fly(bool holdingControls)
    {
        var game = new LoopbackGame(1);
        using var _ = game;
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        var ship = game.Host.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 50, 0), voxels));
        game.Host.SpawnLocalPlayer(new Vector3(-2, 52, -2));
        game.Tick(2);
        var (client, _) = game.Join("crew");
        var crew = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        Players.SetFreeFlying(crew, false);
        crew.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(1, 51.4f, 1));
        game.Tick(60);
        if (holdingControls) crew.Set(new LookLockedComponent());
        var copy = client.Registry.Find(ship.Get<NetId>().Value)!.Value;
        var crewOnHost = game.Host.Registry.Find(crew.Get<NetId>().Value)!.Value;
        var body = ship.Get<PhysicsBodyComponent>().Body;
        game.Host.Physics.SetBodyLinearVelocity(body, new Vector3(Speed, 0, 0));
        game.Host.Physics.SetBodyAngularVelocity(body, new Vector3(0, 0.3f, 0));

        var flight = new Flight();
        var rng = new Random(1);
        double now = 0, hostNoise = 0, clientNoise = 0;
        for (int frame = 0; frame < 600; frame++)
        {
            // Each frame is a sixtieth of a second give or take 2 ms, on each machine independently, keeping to real time.
            double h = (rng.NextDouble() * 2 - 1) * 0.002, c = (rng.NextDouble() * 2 - 1) * 0.002;
            game.Network.ManualTime += 1000.0 / 60.0;
            now += 1 / 60.0;
            game.Host.Frame(1 / 60.0 + h - hostNoise);
            client.Frame(1 / 60.0 + c - clientNoise);
            (hostNoise, clientNoise) = (h, c);
            if (frame < 300) continue;

            ref readonly var drawnCopy = ref copy.Get<Transform>(); // a copy is drawn where its Transform is
            var drawnCentre = copy.Get<PhysicsBodyComponent>().BodyPosition(drawnCopy); // the turn carries its block origin round
            flight.ClientShipError.Add(drawnCentre.X - Speed * (float)(now + clientNoise));
            var toCopy = Quaternion.Conjugate(Q(drawnCopy.Rotation));
            flight.CameraOnDeck.Add(Vector3.Transform(V(crew.DrawnPose().Position) - V(drawnCopy.Position), toCopy));
            var hostShip = ship.DrawnPose();
            flight.CrewOnHostDeck.Add(Vector3.Transform(V(crewOnHost.Get<Transform>().Position) - V(hostShip.Position),
                                                        Quaternion.Conjugate(Q(hostShip.Rotation))));
        }
        flight.StillAboard = crew.Get<Support>().Supporter == copy;
        return flight;
    }

    private static float LargestChange(List<float> v) => v.Zip(v.Skip(1), (a, b) => MathF.Abs(b - a)).Max();
    private static float LargestChange(List<Vector3> v) => v.Zip(v.Skip(1), (a, b) => (b - a).Length()).Max();

    [Fact]
    public void TheClientDrawsTheShipMovingSteadily()
    {
        var flight = Fly(holdingControls: false);
        float jitter = LargestChange(flight.ClientShipError);
        _out.WriteLine($"largest jump off steady motion: {jitter:0.00000} blocks (a frame's motion is {Speed / 60:0.0})");
        Assert.True(jitter < 0.001f, $"the ship jitters by up to {jitter} blocks a frame");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrewStayStillOnTheDeckAsEachMachineDrawsIt(bool holdingControls)
    {
        var flight = Fly(holdingControls);
        float camera = LargestChange(flight.CameraOnDeck), onHost = LargestChange(flight.CrewOnHostDeck);
        _out.WriteLine($"camera on the client's deck moves up to {camera:0.00000}; crew on the host's deck, {onHost:0.00000}");
        Assert.True(flight.StillAboard);
        Assert.True(camera < 0.001f, $"the camera shakes against the deck by up to {camera} blocks a frame");
        Assert.True(onHost < 0.001f, $"the host sees the crew shake against the deck by up to {onHost} blocks a frame");
    }
}
