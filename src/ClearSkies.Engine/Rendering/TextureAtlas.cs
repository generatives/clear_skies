using System.Xml.Linq;
using StbImageSharp;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Loads a TexturePacker-style spritesheet (a PNG plus an XML index of named sub-texture
/// rects) and re-slices it into a stack of same-size RGBA layers, one per named sprite, ready
/// to upload as a GPU <c>texture_2d_array</c>. All sub-textures in the sheet must be the same
/// size — that size becomes the array's per-layer tile size.
///
/// This is pure data (no WebGPU dependency); <c>Renderer.LoadTextureAtlas</c> owns turning it
/// into GPU resources.
/// </summary>
public sealed class TextureAtlas
{
    private readonly Dictionary<string, int> _layerByName = new();
    private readonly byte[][] _layers;

    public int TileSize   { get; }
    public int LayerCount => _layers.Length;

    public TextureAtlas(string pngPath, string xmlPath)
    {
        var doc = XDocument.Load(xmlPath);
        var subTextures = doc.Root!.Elements("SubTexture").ToList();
        if (subTextures.Count == 0)
            throw new InvalidOperationException($"No <SubTexture> entries found in '{xmlPath}'.");

        using var pngStream = File.OpenRead(pngPath);
        var image = ImageResult.FromStream(pngStream, ColorComponents.RedGreenBlueAlpha);

        TileSize = (int)subTextures[0].Attribute("width")!;
        _layers  = new byte[subTextures.Count][];

        for (int i = 0; i < subTextures.Count; i++)
        {
            var el = subTextures[i];
            string name = Path.GetFileNameWithoutExtension((string)el.Attribute("name")!);
            int x = (int)el.Attribute("x")!, y = (int)el.Attribute("y")!;
            int w = (int)el.Attribute("width")!, h = (int)el.Attribute("height")!;
            if (w != TileSize || h != TileSize)
                throw new InvalidOperationException(
                    $"Sub-texture '{name}' is {w}x{h}, expected a uniform {TileSize}x{TileSize} sheet.");

            _layers[i] = CropRgba(image.Data, image.Width, x, y, w, h);
            _layerByName[name] = i;
        }
    }

    public bool TryGetLayer(string? name, out int layer)
    {
        if (name != null) return _layerByName.TryGetValue(name, out layer);
        layer = -1;
        return false;
    }

    /// <summary>Raw RGBA8 pixels for <paramref name="layer"/>, row-major, <see cref="TileSize"/> square.</summary>
    public byte[] GetLayerPixels(int layer) => _layers[layer];

    private static byte[] CropRgba(byte[] src, int srcWidth, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int srcOffset = ((y + row) * srcWidth + x) * 4;
            int dstOffset = row * w * 4;
            Array.Copy(src, srcOffset, dst, dstOffset, w * 4);
        }
        return dst;
    }
}
