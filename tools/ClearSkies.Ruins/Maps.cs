using ClearSkies.Game.Ruins;
using StbImageWriteSharp;

/// <summary>Top-down maps: the landmass with the ruin's region, and each floor of a layout.</summary>
public static class Maps
{
    private static readonly (byte R, byte G, byte B) Sky = (18, 22, 34), Gap = (44, 50, 78), Partial = (72, 74, 82),
        Solid = (104, 106, 112), Hall = (222, 212, 186), Avenue = (250, 242, 214), Hub = (255, 200, 90),
        Bridge = (255, 128, 40), Outside = (54, 60, 78), Core = (60, 220, 230), Entrance = (90, 255, 120);
    private static readonly (byte R, byte G, byte B)[] Uses = [(196, 146, 96), (156, 116, 206), (206, 76, 76)];

    /// <summary>The whole landmass, a pixel per cell: land grey, other islands darker, the region tinted.</summary>
    public static void Site(RuinSite site, string path)
    {
        var sample = site.Sample;
        int w = sample.Nx, h = sample.Nz;
        var rgb = new byte[w * h * 3];
        for (int c = 0; c < w * h; c++)
        {
            bool land = sample.Land[c], mine = sample.IslandOf[c] == site.Island;
            var col = site.LandmassRegion[c] ? (land ? ((byte)170, (byte)150, (byte)110) : Gap)
                : land ? (mine ? Solid : ((byte)64, (byte)66, (byte)72)) : Sky;
            rgb[c * 3] = col.Item1; rgb[c * 3 + 1] = col.Item2; rgb[c * 3 + 2] = col.Item3;
        }
        Write(path, rgb, w, h);
    }

    public static int ScaleFor(RuinSite site) => Math.Clamp(1600 / Math.Max(site.Nx, site.Nz), 1, 3);

    /// <summary>One floor of a plan, <paramref name="scale"/> pixels a cell: the ground, then halls, rooms coloured by
    /// their district's use (outlined, their doors marked), then cores.</summary>
    public static byte[] Floor(RuinPlan plan, int floor, int scale)
    {
        var site = plan.Site;
        int w = site.Nx * scale, h = site.Nz * scale;
        var rgb = new byte[w * h * 3];
        void Px(int x, int y, (byte R, byte G, byte B) c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (x + w * y) * 3; rgb[o] = c.R; rgb[o + 1] = c.G; rgb[o + 2] = c.B;
        }
        void Fill(CellRect r, (byte R, byte G, byte B) c, bool outline)
        {
            var dark = ((byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55));
            for (int y = r.J0 * scale; y < r.J1 * scale; y++)
            for (int x = r.I0 * scale; x < r.I1 * scale; x++)
            {
                bool edge = outline && (x == r.I0 * scale || y == r.J0 * scale || x == r.I1 * scale - 1 || y == r.J1 * scale - 1);
                Px(x, y, edge ? dark : c);
            }
        }
        for (int j = 0; j < site.Nz; j++)
        for (int i = 0; i < site.Nx; i++)
        {
            var g = site.At(floor, i, j);
            // Outside the ruin: the island's land, faint, so the ruin's extent shows against it.
            int li = (site.X0 - site.Sample.X0) / RuinSite.Cell + i, lj = (site.Z0 - site.Sample.Z0) / RuinSite.Cell + j;
            bool land = g == RuinSite.Ground.Out && site.Sample.Land[li + site.Sample.Nx * lj];
            var c = land ? Outside : g switch
            {
                RuinSite.Ground.Out => Sky, RuinSite.Ground.Open => Gap, RuinSite.Ground.Partial => Partial, _ => Solid,
            };
            Fill(new CellRect(i, j, i + 1, j + 1), c, false);
        }
        foreach (var p in plan.Parts)
        {
            if (p.Floor != floor || p.Kind is PartKind.Plot or PartKind.Core) continue;
            Fill(p.Rect, p.Kind switch { PartKind.Avenue => Avenue, PartKind.Hub => Hub, PartKind.Bridge => Bridge, _ => Hall }, false);
        }
        foreach (var p in plan.Parts)
        {
            if (p.Floor != floor || p.Kind != PartKind.Plot) continue;
            var use = p.District >= 0 ? Uses[(int)plan.Uses[p.District]] : Uses[0];
            Fill(p.Rect, use, scale > 1);
            if (scale > 1) Px(p.DoorI * scale + scale / 2 + p.DoorDi * (scale / 2), p.DoorJ * scale + scale / 2 + p.DoorDj * (scale / 2), Hall);
        }
        foreach (var p in plan.Parts)
            if (p.Kind == PartKind.Core && floor >= p.Floor && floor <= p.LastFloor)
                Fill(p.Rect, p.Entrance ? Entrance : Core, false);
        return rgb;
    }

    /// <summary>Blocks as carved into solid stone, a pixel each, <paramref name="scale"/> pixels a block: a level slice
    /// (horizontal, y = <paramref name="y"/>) and an upright one (x by y, z = <paramref name="z"/>), side by side,
    /// over <paramref name="width"/> blocks from (x0, z0) and <paramref name="height"/> blocks down from y top.</summary>
    public static void Slices(RuinCarving carving, int x0, int z0, int y, int width, int yTop, int height, int z, string path)
    {
        var chunk = new ClearSkies.Engine.Voxels.BlockId[ClearSkies.Engine.Voxels.ChunkData.Volume];
        var cache = new Dictionary<(int, int, int), ClearSkies.Engine.Voxels.BlockId[]>();
        ClearSkies.Engine.Voxels.BlockId At(int x, int yy, int zz)
        {
            var key = (x >> 5, yy >> 5, zz >> 5);
            if (!cache.TryGetValue(key, out var b))
            {
                b = new ClearSkies.Engine.Voxels.BlockId[chunk.Length];
                Array.Fill(b, ClearSkies.Engine.Voxels.BlockId.Stone);
                carving.Carve(key.Item1, key.Item2, key.Item3, b);
                cache[key] = b;
            }
            return b[ClearSkies.Engine.Voxels.ChunkData.Index(x & 31, yy & 31, zz & 31)];
        }
        (byte, byte, byte) Colour(ClearSkies.Engine.Voxels.BlockId b) => b switch
        {
            ClearSkies.Engine.Voxels.BlockId.Air => (24, 26, 34),
            ClearSkies.Engine.Voxels.BlockId.StoneBrick => (196, 150, 110),
            _ => (90, 92, 98),
        };
        const int gap = 8;
        int W = width * 2 + gap, H = Math.Max(width, height);
        var rgb = new byte[W * H * 3];
        void Put(int px, int py, (byte, byte, byte) c) { int o = (px + W * py) * 3; rgb[o] = c.Item1; rgb[o + 1] = c.Item2; rgb[o + 2] = c.Item3; }
        for (int j = 0; j < width; j++)
        for (int i = 0; i < width; i++) Put(i, j, Colour(At(x0 + i, y, z0 + j)));
        for (int j = 0; j < height; j++)
        for (int i = 0; i < width; i++) Put(width + gap + i, j, Colour(At(x0 + i, yTop - j, z)));
        Write(path, rgb, W, H);
    }

    /// <summary>Images side by side, a gap between, on the sky colour.</summary>
    public static void Row(string path, IReadOnlyList<byte[]> images, int w, int h)
    {
        const int gap = 8;
        int W = images.Count * w + (images.Count - 1) * gap;
        var rgb = new byte[W * h * 3];
        for (int k = 0; k < rgb.Length; k += 3) { rgb[k] = Sky.R; rgb[k + 1] = Sky.G; rgb[k + 2] = Sky.B; }
        for (int n = 0; n < images.Count; n++)
        for (int y = 0; y < h; y++)
            Array.Copy(images[n], y * w * 3, rgb, (y * W + n * (w + gap)) * 3, w * 3);
        Write(path, rgb, W, h);
    }

    // 3 x 5 digits, a row of three bits per line, top line first.
    private static readonly int[][] Digits =
    [
        [7, 5, 5, 5, 7], [2, 6, 2, 2, 7], [7, 1, 7, 4, 7], [7, 1, 7, 1, 7], [5, 5, 7, 1, 1],
        [7, 4, 7, 1, 7], [7, 4, 7, 5, 7], [7, 1, 1, 1, 1], [7, 5, 7, 5, 7], [7, 5, 7, 1, 7],
    ];

    /// <summary>Images in rows (top floor first, left to right), each labelled with its floor number above it.</summary>
    public static void Sheet(string path, IReadOnlyList<byte[]> images, int w, int h)
    {
        int px = Math.Max(3, w / 80), label = px * 7;
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(images.Count * (double)h / w)));
        int rows = (images.Count + cols - 1) / cols;
        int gap = px * 3, W = cols * w + (cols - 1) * gap, H = rows * (h + label);
        var rgb = new byte[W * H * 3];
        for (int k = 0; k < rgb.Length; k += 3) { rgb[k] = Sky.R; rgb[k + 1] = Sky.G; rgb[k + 2] = Sky.B; }
        for (int n = 0; n < images.Count; n++)
        {
            int ox = n % cols * (w + gap), oy = n / cols * (h + label) + label;
            for (int y = 0; y < h; y++)
                Array.Copy(images[n], y * w * 3, rgb, ((oy + y) * W + ox) * 3, w * 3);
            string text = n.ToString();
            for (int d = 0; d < text.Length; d++)
            {
                var glyph = Digits[text[d] - '0'];
                for (int gy = 0; gy < 5; gy++)
                for (int gx = 0; gx < 3; gx++)
                {
                    if ((glyph[gy] >> (2 - gx) & 1) == 0) continue;
                    for (int yy = 0; yy < px; yy++)
                    for (int xx = 0; xx < px; xx++)
                    {
                        int x = ox + px + (d * 4 + gx) * px + xx, y = oy - label + px + gy * px + yy;
                        int o = (x + W * y) * 3; rgb[o] = 240; rgb[o + 1] = 240; rgb[o + 2] = 240;
                    }
                }
            }
        }
        Write(path, rgb, W, H);
    }

    public static void Write(string path, byte[] rgb, int w, int h)
    {
        using var f = File.Create(path);
        new ImageWriter().WritePng(rgb, w, h, ColorComponents.RedGreenBlue, f);
    }
}
