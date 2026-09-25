using System.Numerics;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Sky gradient and distance fog, shared by <c>ChunkLoadSystem</c> (which publishes how far the world is loaded)
/// and <c>RenderFrame</c> (the <c>CameraUniform</c> sky/fog fields and the debug sliders).
/// The fog exists to hide the edge of the loaded world, so it follows <see cref="FogDistance"/>, which moves as
/// the streaming budget reaches further or less far. Fogged geometry fades to the sky colour in its
/// own view direction, so it blends into exactly what is drawn behind it.
/// </summary>
public static class SkySettings
{
    /// <summary>Straight-up sky colour (the old flat clear colour).</summary>
    public static Vector3 ZenithColor = new(0.10f, 0.3078f, 0.4804f);

    /// <summary>Horizon haze, and the colour distant terrain fades to.</summary>
    public static Vector3 HorizonColor = new(0.42f, 0.58f, 0.72f);

    public static bool FogEnabled = true;

    /// <summary>Distance haze (aerial perspective): how strong it gets far away (0-1), how far until it's 63% of
    /// that, in blocks (3D, so islands far above and below are hazed too), and its colour, which turns towards the
    /// sky's as it thickens.</summary>
    public static bool HazeEnabled = true;
    public static float HazeStrength = 0.95f;
    public static float HazeDistance = 2500f;

    /// <summary>The cloud sea: a floor of blocky cloud far below the islands, drawn with the sky. Its lowest block
    /// face's altitude, how much of it is cloud (0-1), its cells' size and its blocks' greatest thickness, in
    /// blocks.</summary>
    public static bool CloudSeaEnabled = true;
    public static float CloudSeaAltitude = -120f;
    public static float CloudSeaCoverage = 0.6f;
    public static float CloudSeaCell = 32f;
    public static float CloudSeaThickness = 48f;
    public static Vector3 HazeColor = new(0.47f, 0.60f, 0.78f);

    public static bool CloudsEnabled = true;

    /// <summary>0-1: the fraction of the sky that is cloud over open sky, far from any island (see <c>CloudLayer</c>
    /// and <c>ICloudDensityMap</c>).</summary>
    public static float CloudCoverageOpen = 0.001f;

    /// <summary>0-1: the fraction of the sky that is cloud around islands.</summary>
    public static float CloudCoverageIslands = 0.1f;

    /// <summary>World Y of the lowest cloud layer, just above the large islands' peaks (islands of other sizes are
    /// above and below it); the other two stack above it (see <c>CloudLayer</c>).</summary>
    public static float CloudAltitude = 1500f;

    /// <summary>Blocks per second the lowest cloud layer drifts along +X (the ones above a little faster).</summary>
    public static float WindSpeed = 1.5f;

    /// <summary>How far before <see cref="FogDistance"/> the fog starts, in blocks: a fixed width, since the fog
    /// distance ranges from a few hundred blocks to thousands.</summary>
    public static float FogBand = 64f;


    /// <summary>Horizontal distance from the camera at which the loaded world stops (the nearest chunk column the
    /// streaming budget cut off or that is still loading), in blocks; published by <c>ChunkLoadSystem</c>.</summary>
    public static float FogDistance { get; private set; } = 256f;

    public static void SetFogDistance(float distance) => FogDistance = distance;
}
