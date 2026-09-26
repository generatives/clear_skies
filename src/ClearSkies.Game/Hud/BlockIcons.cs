using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Ui;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Hud;

/// <summary>Hotbar icons for blocks: the block's <see cref="BlockDef.IconTexture"/> if it has one (model blocks get
/// theirs baked by tools/ClearSkies.IconBaker), otherwise a 16x16 pixel-art icon made here: its side texture
/// box-filtered down from the block atlas, or for untextured blocks (lamps) a bevelled swatch of its color, glowing in
/// the middle if it emits light.</summary>
internal static class BlockIcons
{
    public const int Size = 16;

    /// <param name="iconsDirectory">Where <see cref="BlockDef.IconTexture"/> paths are relative to (Resources/Icons).</param>
    public static UiSprite Create(UiAtlas atlas, BlockDef block, TextureAtlas? textures, string iconsDirectory)
    {
        if (block.IconTexture is { } icon)
        {
            string path = Path.Combine(iconsDirectory, icon);
            if (File.Exists(path))
                return atlas.LoadSprite($"block-icon:{block.Name}", path);
            Console.Error.WriteLine($"[hud] {block.Name}: icon '{path}' not found (bake it with tools/ClearSkies.IconBaker); using a generated one.");
        }

        byte[] pixels = textures != null && textures.TryGetLayer(block.Texture, out int layer)
            ? Downsample(textures.GetLayerPixels(layer), textures.TileSize)
            : Swatch(block);
        return atlas.AddSprite($"block-icon:{block.Name}", pixels, Size, Size);
    }

    /// <summary>Averages each (tile / 16)-pixel square, weighting colors by alpha so transparent texels don't darken
    /// the edges.</summary>
    private static byte[] Downsample(byte[] rgba, int tile)
    {
        var result = new byte[Size * Size * 4];
        int step = System.Math.Max(1, tile / Size);
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            long r = 0, g = 0, b = 0, a = 0;
            int count = 0;
            for (int sy = y * step; sy < (y + 1) * step && sy < tile; sy++)
            for (int sx = x * step; sx < (x + 1) * step && sx < tile; sx++)
            {
                int i = (sy * tile + sx) * 4;
                int alpha = rgba[i + 3];
                r += rgba[i] * alpha; g += rgba[i + 1] * alpha; b += rgba[i + 2] * alpha; a += alpha;
                count++;
            }
            int o = (y * Size + x) * 4;
            if (a > 0)
            {
                result[o] = (byte)(r / a); result[o + 1] = (byte)(g / a); result[o + 2] = (byte)(b / a);
            }
            result[o + 3] = (byte)(a / System.Math.Max(count, 1));
        }
        return result;
    }

    private static byte[] Swatch(BlockDef block)
    {
        var result = new byte[Size * Size * 4];
        var c = block.Color;
        bool glows = block.LightEmission > 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int ring = System.Math.Min(System.Math.Min(x, y), System.Math.Min(Size - 1 - x, Size - 1 - y));
            float shade;
            if (ring == 0) shade = 0.35f;                           // outline
            else if (ring == 1) shade = x == 1 || y == 1 ? 1.25f : 0.75f; // bevel: lit top-left, shaded bottom-right
            else shade = 1f;
            if (glows && ring >= 2)
            {
                float dx = x - (Size - 1) / 2f, dy = y - (Size - 1) / 2f;
                shade += MathF.Max(0f, 0.6f - MathF.Sqrt(dx * dx + dy * dy) / 8f);
            }
            int o = (y * Size + x) * 4;
            result[o]     = ToByte(c.X * shade);
            result[o + 1] = ToByte(c.Y * shade);
            result[o + 2] = ToByte(c.Z * shade);
            result[o + 3] = 255;
        }
        return result;
    }

    private static byte ToByte(float v) => (byte)System.Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
