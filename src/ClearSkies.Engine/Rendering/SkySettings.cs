using System.Numerics;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Sky gradient and distance fog, shared by <c>ChunkLoadSystem</c> (which publishes how far the world is loaded)
/// and <c>FrameBeginSystem</c> (the <c>CameraUniform</c> sky/fog fields and the debug sliders).
/// The fog exists to hide the edge of the loaded world, so its distances are fractions of the loaded extent rather
/// than absolute: changing the view distance moves the fog with it. Fogged geometry fades to the sky colour in its
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

    /// <summary>0-1: the fraction of cloud-layer cells that are cloud (see <c>CloudLayer</c>).</summary>
    public static float CloudCoverage = 0.1f;

    /// <summary>World Y of the cloud layer's underside; islands top out well below the default.</summary>
    public static float CloudAltitude = 300f;

    /// <summary>Blocks per second the cloud layer drifts along +X.</summary>
    public static float WindSpeed = 1.5f;

    /// <summary>Where horizontal fog begins, as a fraction of <see cref="LoadedHorizontal"/>; it is total at 1.</summary>
    public static float FogStartFraction = 0.85f;

    /// <summary>Where vertical fog begins, as a fraction of <see cref="LoadedVertical"/>; it is total at 1.</summary>
    public static float VerticalFogStartFraction = 0.75f;

    /// <summary>Guaranteed-loaded distance from the camera, in blocks: horizontally and vertically. The load region is
    /// a box of chunks around the camera's chunk, so its nearest edge is at least radius * chunk size away.</summary>
    public static float LoadedHorizontal { get; private set; } = 256f;
    public static float LoadedVertical   { get; private set; } = 96f;

    public static void SetLoadedExtent(float horizontal, float vertical)
    {
        LoadedHorizontal = horizontal;
        LoadedVertical   = vertical;
    }
}
