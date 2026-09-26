using StbImageSharp;

namespace ClearSkies.Engine.Ui;

/// <summary>A sprite registered with <see cref="UiAtlas"/>; put it on an element with
/// <see cref="ImageConfig.Sprite"/>.</summary>
public readonly record struct UiSprite(int Index)
{
    /// <summary>No sprite: an image element with it draws nothing. What adding a sprite to a full atlas returns.</summary>
    public static readonly UiSprite None = new(-1);
}

/// <summary>Nine-slice insets in texels: the sprite's edges this far in keep their size (scaled only by
/// <see cref="UiContext.Scale"/>) while the middle stretches, so one small frame texture fits any element size.</summary>
public readonly record struct NineSlice(int Left, int Top, int Right, int Bottom)
{
    public NineSlice(int all) : this(all, all, all, all) { }
    public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>
/// The single RGBA texture everything in the game UI draws from: sprites (loaded from PNGs or pixels, optionally
/// nine-sliced), font glyphs (added on demand by <see cref="UiFont"/>) and a white block that solid rectangles sample,
/// so a whole UI frame is one texture and usually one draw call. Packed in shelves with a transparent pixel of
/// padding around every entry.
///
/// This is the CPU copy; UiRenderSystem uploads whatever changed (<see cref="TakeDirtyRect"/>) before
/// drawing. It never shrinks or repacks: if it fills up, further glyphs and sprites are dropped with a warning (raise
/// the size passed to <see cref="UiContext"/>'s constructor).
/// </summary>
public sealed class UiAtlas
{
    public int Size { get; }
    internal byte[] Pixels { get; }

    private readonly List<SpriteEntry> _sprites = new();
    private readonly Dictionary<string, UiSprite> _spritesByName = new();

    internal readonly record struct SpriteEntry(string Name, int X, int Y, int Width, int Height, NineSlice Slice);

    // Shelf packer: rows of entries left to right; a new shelf starts below the tallest entry of the current one.
    private int _shelfX, _shelfY, _shelfHeight;
    private const int Padding = 1;

    // Region changed since the renderer last uploaded (exclusive max); empty when _dirtyMaxX <= _dirtyMinX.
    private int _dirtyMinX, _dirtyMinY, _dirtyMaxX, _dirtyMaxY;
    private bool _warnedFull;

    /// <summary>Centre of the white block, for untextured quads (solid rectangles and borders).</summary>
    internal (float U, float V) WhiteUv { get; }

    public int UsedHeight => _shelfY + _shelfHeight;

    public UiAtlas(int size = 2048)
    {
        Size = size;
        Pixels = new byte[size * size * 4];
        _dirtyMinX = _dirtyMinY = int.MaxValue;

        // A 4x4 white block; sampling its centre is white whatever the filtering.
        var white = new byte[4 * 4 * 4];
        Array.Fill(white, (byte)255);
        if (!TryPlace(4, 4, white, out int wx, out int wy))
            throw new InvalidOperationException("UI atlas is too small for even its white block.");
        WhiteUv = ((wx + 2f) / size, (wy + 2f) / size);
    }

    // ── Sprites ──────────────────────────────────────────────────────────────

    /// <summary>Adds a sprite from RGBA8 pixels (row-major, <paramref name="width"/> x <paramref name="height"/>).
    /// Adding a name that already exists replaces what it points to (the old pixels stay in the atlas).</summary>
    public UiSprite AddSprite(string name, ReadOnlySpan<byte> rgba, int width, int height, NineSlice slice = default)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"Sprite '{name}' needs {width * height * 4} bytes of RGBA, got {rgba.Length}.");
        if (!TryPlace(width, height, rgba, out int x, out int y))
            return UiSprite.None;

        var sprite = new UiSprite(_sprites.Count);
        _sprites.Add(new SpriteEntry(name, x, y, width, height, slice));
        _spritesByName[name] = sprite;
        return sprite;
    }

    /// <summary>Loads a PNG as a sprite named <paramref name="name"/>.</summary>
    public UiSprite LoadSprite(string name, string pngPath, NineSlice slice = default)
    {
        using var stream = File.OpenRead(pngPath);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return AddSprite(name, image.Data, image.Width, image.Height, slice);
    }

    /// <summary>Loads every PNG in <paramref name="directory"/> as a sprite named after its file (without extension).
    /// A file named <c>name.9.png</c> is nine-sliced with insets from <paramref name="nineSlices"/>[name], or a third
    /// of its size if not listed.</summary>
    public void LoadSprites(string directory, IReadOnlyDictionary<string, NineSlice>? nineSlices = null)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            NineSlice slice = default;
            if (name.EndsWith(".9", StringComparison.Ordinal))
            {
                name = name[..^2];
                if (nineSlices == null || !nineSlices.TryGetValue(name, out slice))
                {
                    using var file = File.OpenRead(path);
                    var info = ImageInfo.FromStream(file);
                    int w = info?.Width ?? 3, h = info?.Height ?? 3;
                    slice = new NineSlice(w / 3, h / 3, w / 3, h / 3);
                }
            }
            else if (nineSlices != null)
                nineSlices.TryGetValue(name, out slice);
            LoadSprite(name, path, slice);
        }
    }

    public bool TryGetSprite(string name, out UiSprite sprite) => _spritesByName.TryGetValue(name, out sprite);

    /// <summary>The sprite called <paramref name="name"/>; throws if there isn't one.</summary>
    public UiSprite Sprite(string name) => _spritesByName.TryGetValue(name, out var s)
        ? s : throw new KeyNotFoundException($"No UI sprite named '{name}'.");

    public (int Width, int Height) SpriteSize(UiSprite sprite)
    {
        var e = _sprites[sprite.Index];
        return (e.Width, e.Height);
    }

    internal bool TryGetEntry(int index, out SpriteEntry entry)
    {
        if ((uint)index < (uint)_sprites.Count) { entry = _sprites[index]; return true; }
        entry = default;
        return false;
    }

    public int SpriteCount => _sprites.Count;

    // ── Packing ──────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="rgba"/> into a free spot; false (with a one-time warning) if the atlas is
    /// full.</summary>
    internal bool TryPlace(int width, int height, ReadOnlySpan<byte> rgba, out int x, out int y)
    {
        if (!TryAllocate(width, height, out x, out y))
            return false;
        for (int row = 0; row < height; row++)
            rgba.Slice(row * width * 4, width * 4).CopyTo(Pixels.AsSpan(((y + row) * Size + x) * 4, width * 4));
        MarkDirty(x, y, width, height);
        return true;
    }

    /// <summary>Copies a single-channel coverage bitmap (e.g. a glyph) in as white with that alpha.</summary>
    internal bool TryPlaceAlpha(int width, int height, ReadOnlySpan<byte> alpha, int stride, out int x, out int y)
    {
        if (!TryAllocate(width, height, out x, out y))
            return false;
        for (int row = 0; row < height; row++)
        {
            int dst = ((y + row) * Size + x) * 4;
            for (int col = 0; col < width; col++, dst += 4)
            {
                Pixels[dst] = Pixels[dst + 1] = Pixels[dst + 2] = 255;
                Pixels[dst + 3] = alpha[row * stride + col];
            }
        }
        MarkDirty(x, y, width, height);
        return true;
    }

    private bool TryAllocate(int width, int height, out int x, out int y)
    {
        int w = width + Padding * 2, h = height + Padding * 2;
        if (_shelfX + w > Size)
        {
            _shelfY += _shelfHeight;
            _shelfX = 0;
            _shelfHeight = 0;
        }
        if (w > Size || _shelfY + h > Size)
        {
            if (!_warnedFull)
                Console.Error.WriteLine($"[ui] atlas ({Size}x{Size}) is full; further sprites and glyphs are dropped.");
            _warnedFull = true;
            x = y = 0;
            return false;
        }
        x = _shelfX + Padding;
        y = _shelfY + Padding;
        _shelfX += w;
        _shelfHeight = System.Math.Max(_shelfHeight, h);
        return true;
    }

    private void MarkDirty(int x, int y, int w, int h)
    {
        _dirtyMinX = System.Math.Min(_dirtyMinX, x);
        _dirtyMinY = System.Math.Min(_dirtyMinY, y);
        _dirtyMaxX = System.Math.Max(_dirtyMaxX, x + w);
        _dirtyMaxY = System.Math.Max(_dirtyMaxY, y + h);
    }

    /// <summary>The region changed since the last call (whole rows, to upload in one copy), and clears it.</summary>
    internal bool TakeDirtyRect(out int y, out int height)
    {
        if (_dirtyMaxX <= _dirtyMinX) { y = height = 0; return false; }
        y = _dirtyMinY;
        height = _dirtyMaxY - _dirtyMinY;
        _dirtyMinX = _dirtyMinY = int.MaxValue;
        _dirtyMaxX = _dirtyMaxY = 0;
        return true;
    }
}
