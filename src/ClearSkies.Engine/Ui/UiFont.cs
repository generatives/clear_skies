using System.Runtime.InteropServices;
using StbTrueTypeSharp;
using static StbTrueTypeSharp.StbTrueType;

namespace ClearSkies.Engine.Ui;

/// <summary>
/// A TrueType font for the game UI, rasterized with stb_truetype. Glyphs are rendered on first use at the exact pixel
/// size they're drawn at and cached in the <see cref="UiAtlas"/>, so text is crisp at any size and UI scale without
/// signed-distance tricks. Sizes are pixel heights (ascender to descender), the same meaning as Clay's fontSize.
/// Metrics (for layout) come straight from the font and never touch the atlas.
/// </summary>
public sealed unsafe class UiFont : IDisposable
{
    public string Name { get; }

    private readonly stbtt_fontinfo _info = new();
    private byte* _data; // stb_truetype keeps pointing into this, so it's native memory that never moves
    private readonly int _ascent, _descent, _lineGap;
    private readonly Dictionary<int, int> _glyphIndex = new();
    private readonly Dictionary<(int Glyph, int PixelSize), Glyph> _glyphs = new();

    /// <summary>A glyph rasterized into the atlas at one pixel size: its rectangle there (empty for blank glyphs
    /// such as space) and where that sits relative to the pen position on the baseline.</summary>
    internal readonly record struct Glyph(int AtlasX, int AtlasY, int Width, int Height, int OffsetX, int OffsetY);

    public UiFont(string name, byte[] ttf)
    {
        Name = name;
        _data = (byte*)NativeMemory.Alloc((nuint)ttf.Length);
        ttf.CopyTo(new Span<byte>(_data, ttf.Length));
        if (stbtt_InitFont(_info, _data, stbtt_GetFontOffsetForIndex(_data, 0)) == 0)
        {
            NativeMemory.Free(_data);
            _data = null;
            throw new InvalidDataException($"Font '{name}' isn't a TrueType/OpenType font stb_truetype can read.");
        }
        int ascent, descent, lineGap;
        stbtt_GetFontVMetrics(_info, &ascent, &descent, &lineGap);
        _ascent = ascent; _descent = descent; _lineGap = lineGap;
    }

    public static UiFont Load(string path) => new(Path.GetFileNameWithoutExtension(path), File.ReadAllBytes(path));

    private float ScaleFor(int pixelSize) => stbtt_ScaleForPixelHeight(_info, pixelSize);

    /// <summary>Baseline distance below the top of a line, in pixels.</summary>
    public float Ascent(int pixelSize) => _ascent * ScaleFor(pixelSize);

    /// <summary>Height of one line (ascender to descender plus the font's line gap), in pixels.</summary>
    public float LineHeight(int pixelSize) => (_ascent - _descent + _lineGap) * ScaleFor(pixelSize);

    internal int GlyphIndex(int codepoint)
    {
        if (!_glyphIndex.TryGetValue(codepoint, out int glyph))
            _glyphIndex[codepoint] = glyph = stbtt_FindGlyphIndex(_info, codepoint);
        return glyph;
    }

    /// <summary>How far the pen moves after <paramref name="glyph"/>, in pixels, before kerning.</summary>
    internal float Advance(int glyph, int pixelSize)
    {
        int advance, bearing;
        stbtt_GetGlyphHMetrics(_info, glyph, &advance, &bearing);
        return advance * ScaleFor(pixelSize);
    }

    internal float Kerning(int glyph, int nextGlyph, int pixelSize) =>
        stbtt_GetGlyphKernAdvance(_info, glyph, nextGlyph) * ScaleFor(pixelSize);

    /// <summary>Width in pixels of <paramref name="utf8"/> on one line: advances plus kerning, plus
    /// <paramref name="letterSpacing"/> pixels between characters.</summary>
    internal float MeasureWidth(ReadOnlySpan<byte> utf8, int pixelSize, float letterSpacing)
    {
        float width = 0;
        int previous = -1, count = 0;
        while (!utf8.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf8(utf8, out var rune, out int consumed);
            utf8 = utf8[consumed..];
            int glyph = GlyphIndex(rune.Value);
            if (previous >= 0) width += Kerning(previous, glyph, pixelSize);
            width += Advance(glyph, pixelSize);
            previous = glyph;
            count++;
        }
        if (count > 1) width += letterSpacing * (count - 1);
        return width;
    }

    /// <summary>The glyph at <paramref name="pixelSize"/>, rasterizing it into <paramref name="atlas"/> the first time.
    /// False if it couldn't be placed (atlas full); blank glyphs succeed with an empty rectangle.</summary>
    internal bool TryGetGlyph(int glyph, int pixelSize, UiAtlas atlas, out Glyph result)
    {
        if (_glyphs.TryGetValue((glyph, pixelSize), out result))
            return true;

        float scale = ScaleFor(pixelSize);
        int x0, y0, x1, y1;
        stbtt_GetGlyphBitmapBox(_info, glyph, scale, scale, &x0, &y0, &x1, &y1);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0 || stbtt_IsGlyphEmpty(_info, glyph) != 0)
        {
            result = new Glyph(0, 0, 0, 0, 0, 0);
            _glyphs[(glyph, pixelSize)] = result;
            return true;
        }

        var bitmap = new byte[w * h];
        fixed (byte* p = bitmap)
            stbtt_MakeGlyphBitmap(_info, p, w, h, w, scale, scale, glyph);
        if (!atlas.TryPlaceAlpha(w, h, bitmap, w, out int ax, out int ay))
            return false;

        result = new Glyph(ax, ay, w, h, x0, y0);
        _glyphs[(glyph, pixelSize)] = result;
        return true;
    }

    public void Dispose()
    {
        if (_data != null) NativeMemory.Free(_data);
        _data = null;
    }
}
