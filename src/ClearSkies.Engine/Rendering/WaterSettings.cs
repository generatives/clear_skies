using System.Numerics;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// How water looks (see the Renderer's fs_chunk_transparent and applyWater), tunable live from the Renderer panel.
/// <see cref="Color"/> is both the underwater fog's colour and what deep water turns to seen from above, so the two
/// always match.
/// </summary>
public static class WaterSettings
{
    /// <summary>The deep water and underwater fog colour (linear), before the ambient and sun light it.</summary>
    public static Vector3 Color = new(0.03f, 0.10f, 0.25f);

    /// <summary>Blocks of water the view goes through to cut what shows through it to about a third, and turn the
    /// water's own colour as far towards <see cref="Color"/>. Lower is murkier.</summary>
    public static float Clarity = 4f;

    /// <summary>How opaque the thinnest water is (0-1).</summary>
    public static float ShallowOpacity = 0.45f;

    /// <summary>How far the thinnest water's colour is already towards <see cref="Color"/> (0-1); the rest is its lit
    /// texture, for the ripples.</summary>
    public static float ShallowTint = 0.7f;

    /// <summary>How much the water's thickness varies through it (0-1: up to that much murkier or clearer than
    /// <see cref="Clarity"/> says), so there are darker and lighter stretches of water rather than one even body, and
    /// how big those stretches are, in blocks.</summary>
    public static float MurkVariation = 0.2f;
    public static float MurkSize = 3f;
}
