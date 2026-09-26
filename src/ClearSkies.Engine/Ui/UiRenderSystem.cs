using System.Numerics;
using System.Runtime.InteropServices;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ClearSkies.Engine.Ui;

/// <summary>
/// Draws the game UI: closes <see cref="UiContext"/>'s layout for the frame and turns Clay's render commands into textured
/// quads, all from the one <see cref="UiAtlas"/> texture, so a frame is one draw call per scissor region. Register in
/// <see cref="SystemStage.RenderHud"/>; ImGui still draws over it.
///
/// One shader handles everything: each quad samples the atlas (the white block for solid shapes) times a color, and
/// may be cut to a rounded rectangle, or to a rounded border ring, by a signed-distance test in the fragment shader —
/// which is how Clay's corner radii and per-side border widths are drawn exactly at any size. Nine-sliced sprites
/// become nine quads; text becomes one quad per glyph, rasterized on first use (see <see cref="UiFont"/>). Positions
/// are snapped to whole pixels so pixel art and glyphs stay sharp. Colors are authored values: on an sRGB swapchain
/// the shader converts them so they display as specified.
/// </summary>
public sealed unsafe class UiRenderSystem : IRenderSystem, IDisposable
{
    private const string Wgsl = @"
struct Uniforms { proj: mat4x4<f32>, params: vec4<f32> }; // params.x: 1 if the target is sRGB
@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var samp: sampler;
@group(1) @binding(0) var tex: texture_2d<f32>;

struct VSIn {
    @location(0) pos: vec2<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) color: vec4<f32>,
    @location(3) overlay: vec4<f32>,
    @location(4) local: vec2<f32>,
    @location(5) size: vec2<f32>,
    @location(6) radii: vec4<f32>,
    @location(7) border: vec4<f32>,
    @location(8) mode: f32,
};

struct VSOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) overlay: vec4<f32>,
    @location(3) local: vec2<f32>,
    @location(4) @interpolate(flat) size: vec2<f32>,
    @location(5) @interpolate(flat) radii: vec4<f32>,
    @location(6) @interpolate(flat) border: vec4<f32>,
    @location(7) @interpolate(flat) mode: f32,
};

@vertex
fn vs_main(v: VSIn) -> VSOut {
    var o: VSOut;
    o.pos = u.proj * vec4<f32>(v.pos, 0.0, 1.0);
    o.uv = v.uv;
    o.color = v.color;
    o.overlay = v.overlay;
    o.local = v.local;
    o.size = v.size;
    o.radii = v.radii;
    o.border = v.border;
    o.mode = v.mode;
    return o;
}

// Signed distance from p (relative to the rectangle's centre, y down) to a rectangle of half-size h whose corners
// are rounded by r = (top-left, top-right, bottom-right, bottom-left).
fn round_rect(p: vec2<f32>, h: vec2<f32>, r: vec4<f32>) -> f32 {
    let right = p.x > 0.0;
    let radius = select(select(r.w, r.z, right), select(r.x, r.y, right), p.y < 0.0);
    let q = abs(p) - h + vec2<f32>(radius);
    return min(max(q.x, q.y), 0.0) + length(max(q, vec2<f32>(0.0))) - radius;
}

fn srgb_to_linear(c: vec3<f32>) -> vec3<f32> {
    return select(pow((c + 0.055) / 1.055, vec3<f32>(2.4)), c / 12.92, c <= vec3<f32>(0.04045));
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    var c = textureSample(tex, samp, in.uv) * in.color;
    if (in.mode > 0.5) {
        // 1: rounded rectangle, 2: rounded border (the rectangle minus an inner one inset by the side widths).
        let half = in.size * 0.5;
        var coverage = clamp(0.5 - round_rect(in.local - half, half, in.radii), 0.0, 1.0);
        if (in.mode > 1.5) {
            let b = in.border; // left, top, right, bottom
            let inner_min = b.xy;
            let inner_max = in.size - b.zw;
            if (all(inner_max > inner_min)) {
                let inner_r = max(in.radii - vec4<f32>(max(b.x, b.y), max(b.z, b.y), max(b.z, b.w), max(b.x, b.w)), vec4<f32>(0.0));
                let inner_half = (inner_max - inner_min) * 0.5;
                coverage *= clamp(0.5 + round_rect(in.local - (inner_min + inner_half), inner_half, inner_r), 0.0, 1.0);
            }
        }
        c.a *= coverage;
    }
    c = vec4<f32>(mix(c.rgb, in.overlay.rgb, in.overlay.a), c.a);
    if (u.params.x > 0.5) {
        c = vec4<f32>(srgb_to_linear(c.rgb), c.a);
    }
    return c;
}
";

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public const uint SizeBytes = 76;
        public Vector2 Position;  // framebuffer pixels
        public Vector2 Uv;
        public uint Color;        // RGBA8
        public uint Overlay;      // RGBA8; alpha is how much of it to blend in
        public Vector2 Local;     // position within the shape's rectangle, pixels
        public Vector2 Size;      // the shape's rectangle, pixels
        public Vector4 Radii;     // top-left, top-right, bottom-right, bottom-left, pixels
        public Vector4 Border;    // left, top, right, bottom widths, pixels
        public float Mode;        // 0 plain quad, 1 rounded rectangle, 2 rounded border
    }

    private const float ModePlain = 0, ModeRounded = 1, ModeBorder = 2;

    private struct Batch
    {
        public uint IndexStart, IndexCount;
        public int SX, SY, SW, SH;
    }

    private readonly UiContext _ui;
    private readonly Renderer _renderer;
    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule* _shader;
    private BindGroupLayout* _frameLayout, _textureLayout;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _pipeline;
    private Sampler* _sampler;
    private WgpuBuffer* _uniformBuffer;
    private BindGroup* _frameBindGroup;
    private Texture* _atlasTexture;
    private TextureView* _atlasView;
    private BindGroup* _atlasBindGroup;
    private WgpuBuffer* _vertexBuffer, _indexBuffer;
    private ulong _vertexCapacity, _indexCapacity;

    private Vertex[] _vertices = new Vertex[4096];
    private uint[] _indices = new uint[6144];
    private int _vertexCount, _indexCount;
    private readonly List<Batch> _batches = new();
    private readonly Stack<(int X, int Y, int W, int H)> _scissors = new();
    private readonly Stack<uint> _overlays = new();
    private (int X, int Y, int W, int H) _scissor;
    private uint _overlay;
    private int _fbWidth, _fbHeight, _scale;
    private double _lastTime = -1;

    public UiRenderSystem(UiContext ui, Renderer renderer)
    {
        _ui = ui;
        _renderer = renderer;
        _ctx = renderer.Context;
        _api = _ctx.Api;
        CreatePipeline();
        CreateAtlasTexture();
    }

    public void Render(in RenderContext frame)
    {
        float dt = _lastTime < 0 ? 0f : (float)(frame.TimeSeconds - _lastTime);
        _lastTime = frame.TimeSeconds;
        if (!_ui.EndLayout(dt, out var commands)) return;

        _fbWidth = System.Math.Max(0, _ctx.Size.X);
        _fbHeight = System.Math.Max(0, _ctx.Size.Y);
        _scale = _ui.Scale;
        if (_fbWidth == 0 || _fbHeight == 0) return;

        BuildGeometry(commands); // may rasterize glyphs into the atlas, so before its upload
        _ui.LastRenderCommands = commands.Length;
        _ui.LastQuads = _indexCount / 6;
        _ui.LastDrawCalls = _batches.Count;
        UploadAtlas();
        if (_indexCount == 0) return;

        UploadGeometry();
        UpdateUniforms();

        RenderPassEncoder* pass = _renderer.CurrentPass;
        _api.RenderPassEncoderSetPipeline(pass, _pipeline);
        _api.RenderPassEncoderSetBindGroup(pass, 0, _frameBindGroup, 0, null);
        _api.RenderPassEncoderSetBindGroup(pass, 1, _atlasBindGroup, 0, null);
        _api.RenderPassEncoderSetVertexBuffer(pass, 0, _vertexBuffer, 0, (ulong)_vertexCount * Vertex.SizeBytes);
        _api.RenderPassEncoderSetIndexBuffer(pass, _indexBuffer, IndexFormat.Uint32, 0, (ulong)_indexCount * sizeof(uint));
        foreach (var b in _batches)
        {
            if (b.SW <= 0 || b.SH <= 0 || b.IndexCount == 0) continue;
            _api.RenderPassEncoderSetScissorRect(pass, (uint)b.SX, (uint)b.SY, (uint)b.SW, (uint)b.SH);
            _api.RenderPassEncoderDrawIndexed(pass, b.IndexCount, 1, b.IndexStart, 0, 0);
        }
        _api.RenderPassEncoderSetScissorRect(pass, 0, 0, (uint)_fbWidth, (uint)_fbHeight);
        _renderer.ForgetBoundPipeline();
    }

    // ── Render commands → quads ──────────────────────────────────────────────

    private void BuildGeometry(ClayRenderCommandArray commands)
    {
        _vertexCount = _indexCount = 0;
        _batches.Clear();
        _scissors.Clear();
        _overlays.Clear();
        _scissor = (0, 0, _fbWidth, _fbHeight);
        _overlay = 0;
        StartBatch();
        uint imageId = 0; // the element whose image was drawn last, while its other commands follow

        for (int i = 0; i < commands.Length; i++)
        {
            ref ClayRenderCommand cmd = ref commands.InternalArray[i];
            var box = PixelRect(cmd.BoundingBox);
            switch (cmd.CommandType)
            {
                case RenderCommandType.Rectangle:
                {
                    // Clay emits an image element's background color twice: as the image's tint, and as a rectangle
                    // after (so over) it. It's the tint here, so skip the rectangle.
                    if (cmd.Id == imageId && imageId != 0) break;
                    ref var r = ref cmd.RenderData.Rectangle;
                    var radii = Radii(r.CornerRadius);
                    AddShape(box, Pack(r.BackgroundColor), radii, default, radii == Vector4.Zero ? ModePlain : ModeRounded);
                    break;
                }
                case RenderCommandType.Border:
                {
                    ref var b = ref cmd.RenderData.Border;
                    var widths = new Vector4(b.Width.Left, b.Width.Top, b.Width.Right, b.Width.Bottom) * _scale;
                    AddShape(box, Pack(b.Color), Radii(b.CornerRadius), widths, ModeBorder);
                    break;
                }
                case RenderCommandType.Image:
                    AddImage(box, ref cmd.RenderData.Image);
                    imageId = cmd.Id;
                    continue;
                case RenderCommandType.Text:
                    AddText(box, ref cmd.RenderData.Text);
                    break;
                case RenderCommandType.ScissorStart:
                    _scissors.Push(_scissor);
                    SetScissor(Intersect(_scissor, box));
                    break;
                case RenderCommandType.ScissorEnd:
                    SetScissor(_scissors.Count > 0 ? _scissors.Pop() : (0, 0, _fbWidth, _fbHeight));
                    break;
                case RenderCommandType.OverlayColorStart:
                    _overlays.Push(_overlay);
                    _overlay = Pack(cmd.RenderData.OverlayColor);
                    break;
                case RenderCommandType.OverlayColorEnd:
                    _overlay = _overlays.Count > 0 ? _overlays.Pop() : 0;
                    break;
                // Custom: nothing uses it yet.
            }
            if (cmd.CommandType != RenderCommandType.ScissorStart) imageId = 0;
        }
        EndBatch();
    }

    private (float X, float Y, float W, float H) PixelRect(in UiBoundingBox b)
    {
        float x0 = MathF.Round(b.X * _scale), y0 = MathF.Round(b.Y * _scale);
        float x1 = MathF.Round((b.X + b.Width) * _scale), y1 = MathF.Round((b.Y + b.Height) * _scale);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private Vector4 Radii(in CornerRadius r) =>
        new Vector4(r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft) * _scale;

    private void AddShape((float X, float Y, float W, float H) box, uint color, Vector4 radii, Vector4 border, float mode)
    {
        var (u, v) = _ui.Atlas.WhiteUv;
        AddQuad(box.X, box.Y, box.W, box.H, u, v, u, v, color, box.W, box.H, 0, 0, radii, border, mode);
    }

    private void AddImage((float X, float Y, float W, float H) box, ref ClayImageRenderData image)
    {
        int index = (int)(nint)image.ImageData - 1;
        if (!_ui.Atlas.TryGetEntry(index, out var sprite)) return;
        // Clay's image tint defaults to all zero, meaning untinted.
        uint color = image.BackgroundColor.IsZero ? 0xFFFFFFFFu : Pack(image.BackgroundColor);
        float inv = 1f / _ui.Atlas.Size;
        float u0 = sprite.X * inv, v0 = sprite.Y * inv;
        float u1 = (sprite.X + sprite.Width) * inv, v1 = (sprite.Y + sprite.Height) * inv;

        if (sprite.Slice.IsNone)
        {
            var radii = Radii(image.CornerRadius);
            AddQuad(box.X, box.Y, box.W, box.H, u0, v0, u1, v1, color, box.W, box.H, 0, 0, radii, default,
                    radii == Vector4.Zero ? ModePlain : ModeRounded);
            return;
        }

        // Nine-slice: the insets keep their texel size times the UI scale (shrunk to fit tiny boxes); the rest
        // stretches. Columns and rows: [edge, middle, edge].
        var s = sprite.Slice;
        float l = s.Left * _scale, r = s.Right * _scale, t = s.Top * _scale, b = s.Bottom * _scale;
        float fitX = l + r > box.W && l + r > 0 ? box.W / (l + r) : 1f;
        float fitY = t + b > box.H && t + b > 0 ? box.H / (t + b) : 1f;
        l *= fitX; r *= fitX; t *= fitY; b *= fitY;
        Span<float> xs = stackalloc float[] { box.X, box.X + l, box.X + box.W - r, box.X + box.W };
        Span<float> ys = stackalloc float[] { box.Y, box.Y + t, box.Y + box.H - b, box.Y + box.H };
        Span<float> us = stackalloc float[] { u0, (sprite.X + s.Left) * inv, (sprite.X + sprite.Width - s.Right) * inv, u1 };
        Span<float> vs = stackalloc float[] { v0, (sprite.Y + s.Top) * inv, (sprite.Y + sprite.Height - s.Bottom) * inv, v1 };
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 3; col++)
        {
            float w = xs[col + 1] - xs[col], h = ys[row + 1] - ys[row];
            if (w <= 0 || h <= 0) continue;
            AddQuad(xs[col], ys[row], w, h, us[col], vs[row], us[col + 1], vs[row + 1], color,
                    w, h, 0, 0, default, default, ModePlain);
        }
    }

    private void AddText((float X, float Y, float W, float H) box, ref ClayTextRenderData text)
    {
        var fonts = _ui.Fonts;
        if (fonts.Count == 0 || text.StringContents.Length == 0) return;
        UiFont font = fonts[text.FontId < fonts.Count ? text.FontId : 0];
        int px = _ui.PixelSize(text.FontSize);
        uint color = Pack(text.TextColor);
        float letterSpacing = text.LetterSpacing * _scale;
        float inv = 1f / _ui.Atlas.Size;

        // Clay sizes a line to its lineHeight (the font's own by default); centre the font's line in it.
        float baseline = MathF.Round(box.Y + (box.H - font.LineHeight(px)) * 0.5f + font.Ascent(px));
        float pen = box.X;
        var utf8 = new ReadOnlySpan<byte>(text.StringContents.Chars, text.StringContents.Length);
        int previous = -1;
        while (!utf8.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf8(utf8, out var rune, out int consumed);
            utf8 = utf8[consumed..];
            int glyph = font.GlyphIndex(rune.Value);
            if (previous >= 0) pen += font.Kerning(previous, glyph, px) + letterSpacing;
            previous = glyph;

            if (font.TryGetGlyph(glyph, px, _ui.Atlas, out var g) && g.Width > 0)
            {
                float x = MathF.Round(pen) + g.OffsetX, y = baseline + g.OffsetY;
                AddQuad(x, y, g.Width, g.Height, g.AtlasX * inv, g.AtlasY * inv,
                        (g.AtlasX + g.Width) * inv, (g.AtlasY + g.Height) * inv, color,
                        g.Width, g.Height, 0, 0, default, default, ModePlain);
            }
            pen += font.Advance(glyph, px);
        }
    }

    private void AddQuad(float x, float y, float w, float h, float u0, float v0, float u1, float v1, uint color,
                         float shapeW, float shapeH, float localX, float localY, Vector4 radii, Vector4 border, float mode)
    {
        if (color >> 24 == 0) return; // fully transparent
        EnsureCapacity(4, 6);
        var size = new Vector2(shapeW, shapeH);
        uint baseIndex = (uint)_vertexCount;
        var common = new Vertex { Color = color, Overlay = _overlay, Size = size, Radii = radii, Border = border, Mode = mode };
        _vertices[_vertexCount++] = common with { Position = new(x, y),         Uv = new(u0, v0), Local = new(localX, localY) };
        _vertices[_vertexCount++] = common with { Position = new(x + w, y),     Uv = new(u1, v0), Local = new(localX + w, localY) };
        _vertices[_vertexCount++] = common with { Position = new(x + w, y + h), Uv = new(u1, v1), Local = new(localX + w, localY + h) };
        _vertices[_vertexCount++] = common with { Position = new(x, y + h),     Uv = new(u0, v1), Local = new(localX, localY + h) };
        _indices[_indexCount++] = baseIndex;
        _indices[_indexCount++] = baseIndex + 1;
        _indices[_indexCount++] = baseIndex + 2;
        _indices[_indexCount++] = baseIndex;
        _indices[_indexCount++] = baseIndex + 2;
        _indices[_indexCount++] = baseIndex + 3;
    }

    private void EnsureCapacity(int vertices, int indices)
    {
        if (_vertexCount + vertices > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);
        if (_indexCount + indices > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);
    }

    /// <summary>Clay colors are 0-255 per channel; packed as RGBA8 (little-endian, so R is the low byte).</summary>
    private static uint Pack(in UiColor c)
    {
        static uint Channel(float v) => (uint)System.Math.Clamp((int)MathF.Round(v), 0, 255);
        return Channel(c.R) | Channel(c.G) << 8 | Channel(c.B) << 16 | Channel(c.A) << 24;
    }

    // ── Scissor batches ──────────────────────────────────────────────────────

    private void SetScissor((int X, int Y, int W, int H) scissor)
    {
        EndBatch();
        _scissor = scissor;
        StartBatch();
    }

    private void StartBatch() => _batches.Add(new Batch
    {
        IndexStart = (uint)_indexCount, SX = _scissor.X, SY = _scissor.Y, SW = _scissor.W, SH = _scissor.H,
    });

    private void EndBatch()
    {
        var b = _batches[^1];
        b.IndexCount = (uint)_indexCount - b.IndexStart;
        _batches[^1] = b;
        if (b.IndexCount == 0) _batches.RemoveAt(_batches.Count - 1);
    }

    private (int, int, int, int) Intersect((int X, int Y, int W, int H) a, (float X, float Y, float W, float H) b)
    {
        int x0 = System.Math.Max(a.X, (int)b.X), y0 = System.Math.Max(a.Y, (int)b.Y);
        int x1 = System.Math.Min(a.X + a.W, (int)(b.X + b.W)), y1 = System.Math.Min(a.Y + a.H, (int)(b.Y + b.H));
        x0 = System.Math.Clamp(x0, 0, _fbWidth); y0 = System.Math.Clamp(y0, 0, _fbHeight);
        x1 = System.Math.Clamp(x1, x0, _fbWidth); y1 = System.Math.Clamp(y1, y0, _fbHeight);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    // ── GPU ──────────────────────────────────────────────────────────────────

    private void UploadGeometry()
    {
        ulong vBytes = (ulong)_vertexCount * Vertex.SizeBytes, iBytes = (ulong)_indexCount * sizeof(uint);
        EnsureBuffer(ref _vertexBuffer, ref _vertexCapacity, vBytes, BufferUsage.Vertex);
        EnsureBuffer(ref _indexBuffer, ref _indexCapacity, iBytes, BufferUsage.Index);
        fixed (Vertex* v = _vertices) _api.QueueWriteBuffer(_ctx.Queue, _vertexBuffer, 0, v, (nuint)vBytes);
        fixed (uint* i = _indices) _api.QueueWriteBuffer(_ctx.Queue, _indexBuffer, 0, i, (nuint)iBytes);
    }

    private void EnsureBuffer(ref WgpuBuffer* buffer, ref ulong capacity, ulong bytes, BufferUsage usage)
    {
        if (buffer != null && bytes <= capacity) return;
        if (buffer != null) _api.BufferRelease(buffer);
        capacity = System.Math.Max(4096UL, System.Numerics.BitOperations.RoundUpToPowerOf2(bytes));
        var desc = new BufferDescriptor { Usage = usage | BufferUsage.CopyDst, Size = capacity };
        buffer = _api.DeviceCreateBuffer(_ctx.Device, &desc);
    }

    private void UpdateUniforms()
    {
        // Pixels (top-left origin, y down) to clip space.
        float sx = 2f / _fbWidth, sy = -2f / _fbHeight;
        bool srgb = _ctx.SurfaceFormat.ToString().Contains("Srgb", StringComparison.OrdinalIgnoreCase);
        Span<float> data = stackalloc float[]
        {
            sx, 0, 0, 0,
            0, sy, 0, 0,
            0, 0, 1, 0,
            -1, 1, 0, 1,
            srgb ? 1 : 0, 0, 0, 0,
        };
        fixed (float* p = data) _api.QueueWriteBuffer(_ctx.Queue, _uniformBuffer, 0, p, 80);
    }

    private void UploadAtlas()
    {
        var atlas = _ui.Atlas;
        if (!atlas.TakeDirtyRect(out int y, out int height)) return;
        var dest = new ImageCopyTexture { Texture = _atlasTexture, MipLevel = 0, Origin = new Origin3D(0, (uint)y, 0), Aspect = TextureAspect.All };
        var layout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)(atlas.Size * 4), RowsPerImage = (uint)height };
        var extent = new Extent3D((uint)atlas.Size, (uint)height, 1);
        fixed (byte* p = &atlas.Pixels[y * atlas.Size * 4])
            _api.QueueWriteTexture(_ctx.Queue, &dest, p, (nuint)(atlas.Size * 4 * height), &layout, &extent);
    }

    private void CreateAtlasTexture()
    {
        int size = _ui.Atlas.Size;
        var texDesc = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D((uint)size, (uint)size, 1),
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _atlasTexture = _api.DeviceCreateTexture(_ctx.Device, &texDesc);
        var viewDesc = new TextureViewDescriptor
        {
            Format = TextureFormat.Rgba8Unorm, Dimension = TextureViewDimension.Dimension2D,
            MipLevelCount = 1, ArrayLayerCount = 1, Aspect = TextureAspect.All,
        };
        _atlasView = _api.TextureCreateView(_atlasTexture, &viewDesc);
        var entry = new BindGroupEntry { Binding = 0, TextureView = _atlasView };
        var bgDesc = new BindGroupDescriptor { Layout = _textureLayout, EntryCount = 1, Entries = &entry };
        _atlasBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &bgDesc);
    }

    private void CreatePipeline()
    {
        var code = (byte*)SilkMarshal.StringToPtr(Wgsl, NativeStringEncoding.UTF8);
        var wgslDesc = new ShaderModuleWGSLDescriptor { Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor }, Code = code };
        var shaderDesc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };
        _shader = _api.DeviceCreateShaderModule(_ctx.Device, &shaderDesc);
        SilkMarshal.Free((nint)code);

        // group(0): projection + params uniform, sampler. group(1): the atlas.
        BindGroupLayoutEntry* frameEntries = stackalloc BindGroupLayoutEntry[2];
        frameEntries[0] = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex | ShaderStage.Fragment, Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = 80 } };
        frameEntries[1] = new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering } };
        var frameLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = frameEntries };
        _frameLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &frameLayoutDesc);

        var texEntry = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment, Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } };
        var texLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &texEntry };
        _textureLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &texLayoutDesc);

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[2] { _frameLayout, _textureLayout };
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);

        // Nearest: sprites are pixel art drawn at whole-number scales and glyphs are drawn 1:1.
        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Nearest, MinFilter = FilterMode.Nearest, MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0, LodMaxClamp = 1, Compare = CompareFunction.Undefined, MaxAnisotropy = 1,
        };
        _sampler = _api.DeviceCreateSampler(_ctx.Device, &samplerDesc);

        var uboDesc = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = 80 };
        _uniformBuffer = _api.DeviceCreateBuffer(_ctx.Device, &uboDesc);
        BindGroupEntry* fgEntries = stackalloc BindGroupEntry[2];
        fgEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = 80 };
        fgEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _sampler };
        var fgDesc = new BindGroupDescriptor { Layout = _frameLayout, EntryCount = 2, Entries = fgEntries };
        _frameBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &fgDesc);

        VertexAttribute* attrs = stackalloc VertexAttribute[9];
        attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };  // position
        attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 };  // uv
        attrs[2] = new VertexAttribute { Format = VertexFormat.Unorm8x4, Offset = 16, ShaderLocation = 2 };  // color
        attrs[3] = new VertexAttribute { Format = VertexFormat.Unorm8x4, Offset = 20, ShaderLocation = 3 };  // overlay
        attrs[4] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 4 }; // local
        attrs[5] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 32, ShaderLocation = 5 }; // size
        attrs[6] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 40, ShaderLocation = 6 }; // radii
        attrs[7] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 7 }; // border
        attrs[8] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 72, ShaderLocation = 8 };   // mode
        var vbLayout = new VertexBufferLayout { ArrayStride = Vertex.SizeBytes, StepMode = VertexStepMode.Vertex, AttributeCount = 9, Attributes = attrs };

        var blend = new BlendState
        {
            Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
            Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
        };
        var colorTarget = new ColorTargetState { Format = _ctx.SurfaceFormat, Blend = &blend, WriteMask = ColorWriteMask.All };

        var vsEntry = (byte*)SilkMarshal.StringToPtr("vs_main", NativeStringEncoding.UTF8);
        var fsEntry = (byte*)SilkMarshal.StringToPtr("fs_main", NativeStringEncoding.UTF8);
        var fragmentState = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };

        // The main pass has a depth attachment, so the pipeline needs a compatible depth state: always pass, never
        // write (as the HUD and ImGui pipelines do).
        var keep = StencilOperation.Keep;
        var stencil = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep };
        var depth = new DepthStencilState
        {
            Format = _ctx.DepthFormat, DepthWriteEnabled = false, DepthCompare = CompareFunction.Always,
            StencilFront = stencil, StencilBack = stencil,
        };

        var pipelineDesc = new RenderPipelineDescriptor
        {
            Layout = _pipelineLayout,
            Vertex = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = 1, Buffers = &vbLayout },
            Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, StripIndexFormat = IndexFormat.Undefined, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
            DepthStencil = &depth,
            Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = false },
            Fragment = &fragmentState,
        };
        _pipeline = _api.DeviceCreateRenderPipeline(_ctx.Device, &pipelineDesc);
        SilkMarshal.Free((nint)vsEntry);
        SilkMarshal.Free((nint)fsEntry);
    }

    public void Dispose()
    {
        if (_vertexBuffer != null) _api.BufferRelease(_vertexBuffer);
        if (_indexBuffer != null) _api.BufferRelease(_indexBuffer);
        if (_atlasBindGroup != null) _api.BindGroupRelease(_atlasBindGroup);
        if (_atlasView != null) _api.TextureViewRelease(_atlasView);
        if (_atlasTexture != null) _api.TextureRelease(_atlasTexture);
        if (_frameBindGroup != null) _api.BindGroupRelease(_frameBindGroup);
        if (_uniformBuffer != null) _api.BufferRelease(_uniformBuffer);
        if (_sampler != null) _api.SamplerRelease(_sampler);
        if (_pipeline != null) _api.RenderPipelineRelease(_pipeline);
        if (_pipelineLayout != null) _api.PipelineLayoutRelease(_pipelineLayout);
        if (_textureLayout != null) _api.BindGroupLayoutRelease(_textureLayout);
        if (_frameLayout != null) _api.BindGroupLayoutRelease(_frameLayout);
        if (_shader != null) _api.ShaderModuleRelease(_shader);
        _vertexBuffer = _indexBuffer = _uniformBuffer = null;
        _atlasBindGroup = _frameBindGroup = null;
        _atlasView = null; _atlasTexture = null; _sampler = null; _pipeline = null;
        _pipelineLayout = null; _textureLayout = _frameLayout = null; _shader = null;
    }
}
