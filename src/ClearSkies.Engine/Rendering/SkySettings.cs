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

    public static bool CloudsEnabled = true;

    /// <summary>0-1: the fraction of the sky that is cloud over open sky, far from any island (see <c>CloudLayer</c>
    /// and <c>ICloudDensityMap</c>).</summary>
    public static float CloudCoverageOpen = 0.03f;

    /// <summary>0-1: the fraction of the sky that is cloud around islands.</summary>
    public static float CloudCoverageIslands = 0.4f;

    /// <summary>World Y of the middle cloud layer, just above the islands' peaks; the low layer sits under the islands
    /// and the high one above (see <c>CloudLayer</c>).</summary>
    public static float CloudAltitude = 320f;

    /// <summary>Blocks per second the middle cloud layer drifts along +X (the others a little slower and faster).</summary>
    public static float WindSpeed = 1.5f;

    /// <summary>How far before <see cref="FogDistance"/> the fog starts, in blocks: a fixed width, since the fog
    /// distance ranges from a few hundred blocks to thousands.</summary>
    public static float FogBand = 64f;


    /// <summary>Horizontal distance from the camera at which the loaded world stops (the nearest chunk column the
    /// streaming budget cut off or that is still loading), in blocks; published by <c>ChunkLoadSystem</c>.</summary>
    public static float FogDistance { get; private set; } = 256f;

    public static void SetFogDistance(float distance) => FogDistance = distance;
}
