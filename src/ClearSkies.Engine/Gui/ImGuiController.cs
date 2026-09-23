using System.Numerics;
using System.Runtime.InteropServices;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Rendering.WebGpu;
using ImGuiNET;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ClearSkies.Engine.Gui;

/// <summary>
/// Bridges Dear ImGui into the engine's WebGPU renderer. Owns its own alpha-blended, scissored
/// pipeline (depth always passes, never writes, so ImGui always draws on top of the scene/HUD)
/// and a dynamic vertex/index buffer that grows to fit each frame's draw data.
///
/// Doubles as the <see cref="ISystem"/> that opens ImGui's frame: <see cref="Update"/> is
/// <c>ImGui.NewFrame()</c> plus input/capture-flag bookkeeping. Register it with
/// <c>AddSystem(host.Gui, SystemStage.Input)</c> — the Input stage runs before Logic/PreRender, so
/// this always opens the frame before any system builds ImGui widgets or reads
/// <see cref="InputManager.UiWantsMouse"/>.
///
/// Also owns the "Systems" debug menu bar (F1 to toggle): any system implementing
/// <see cref="IDebugUiSystem"/> is auto-registered by <c>EngineHost.AddSystem</c> and gets an entry
/// in the "Systems" dropdown that opens/closes its own panel — see <see cref="RegisterDebugUi"/>.
///
/// <see cref="EndFrame"/> is a separate, non-<see cref="ISystem"/> call: it must run after all
/// world/HUD geometry for the frame has been drawn and right before <see cref="Renderer.EndFrame"/>
/// closes the pass, so <c>FrameEndSystem</c> (the EndRender stage) calls it directly.
/// </summary>
public sealed unsafe class ImGuiController : ISystem, IDisposable
{
    // Single 2D texture (the font atlas) + a filtering sampler, with an ortho-projection uniform
    // shared across the frame. No texture array / render-target registration yet — nothing in the
    // engine has a texture to show inside a panel today; add that when something needs it.
    private const string Wgsl = @"
struct Uniforms { mvp: mat4x4<f32> };
@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var samp: sampler;
@group(1) @binding(0) var tex: texture_2d<f32>;

struct VSOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
};

@vertex
fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>, @location(2) color: vec4<f32>) -> VSOut {
    var o: VSOut;
    o.pos = u.mvp * vec4<f32>(position, 0.0, 1.0);
    o.uv = uv;
    o.color = color;
    return o;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    return in.color * textureSample(tex, samp, in.uv);
}
";

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public const uint SizeBytes = 32;
        public Vector2 Position;
        public Vector2 Uv;
        public Vector4 Color;
    }

    private readonly Renderer _renderer;
    private readonly GpuContext _ctx;
    private readonly WebGPU _api;
    private readonly InputManager _input;

    private nint _imguiCtx;

    private ShaderModule* _shader;
    private BindGroupLayout* _frameLayout;
    private BindGroupLayout* _textureLayout;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _pipeline;
    private Sampler* _sampler;
    private WgpuBuffer* _uniformBuffer;
    private BindGroup* _frameBindGroup;

    private Texture* _fontTexture;
    private TextureView* _fontView;
    private BindGroup* _fontBindGroup;

    private WgpuBuffer* _vertexBuffer;
    private WgpuBuffer* _indexBuffer;
    private ulong _vertexCapacityBytes;
    private ulong _indexCapacityBytes;

    // Per-frame CPU scratch — grown, never shrunk.
    private Vertex[] _vtxScratch = new Vertex[4096];
    private uint[] _idxScratch = new uint[6144];

    private float _scrollX, _scrollY;

    // ── "Systems" menu bar (see IDebugUiSystem) ─────────────────────────────────
    private readonly List<IDebugUiSystem> _debugUiSystems = new();
    private readonly Dictionary<string, bool> _debugUiVisible = new();
    private bool _menuVisible = true;   // shown at startup; F1 toggles

    /// <summary>True when ImGui wants to consume mouse input this frame (hovering/dragging/clicking a
    /// widget) — reflects the previous frame's layout, since it's set inside <see cref="Update"/> before
    /// this frame's <c>ImGui.*</c> calls run. <see cref="Update"/> also publishes this into
    /// <see cref="InputManager.UiWantsMouse"/>; this property is kept for callers that want the raw flag.</summary>
    public bool WantCaptureMouse { get; private set; }

    /// <summary>True when ImGui wants to consume keyboard input this frame (a text field or other
    /// widget has focus) — same "previous frame's layout" caveat as <see cref="WantCaptureMouse"/>.
    /// Also published into <see cref="InputManager.UiWantsKeyboard"/> each frame.</summary>
    public bool WantCaptureKeyboard { get; private set; }

    // Debug UI is easy to read at a distance / on a hi-DPI display this way; bump this if it still
    // feels small. Scales both the font (drawn glyphs) and widget metrics (padding, spacing, etc.)
    // so the two stay proportional.
    private const float UiScale = 2f;

    public ImGuiController(Renderer renderer, InputManager input)
    {
        _renderer = renderer;
        _ctx = renderer.Context;
        _api = _ctx.Api;
        _input = input;

        _imguiCtx = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiCtx);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.FontGlobalScale = UiScale;
        // NOT setting RendererHasVtxOffset — ImGui splits meshes to keep VtxOffset==0, so every
        // index we re-base is relative to the current draw list's base vertex only.

        ImGui.GetStyle().ScaleAllSizes(UiScale);

        CreatePipeline();
        UploadFontAtlas();
        WireInput();
    }

    // ── "Systems" menu bar ───────────────────────────────────────────────────

    /// <summary>Registers a system's debug panel. Called automatically by <c>EngineHost.AddSystem</c>
    /// for any system implementing <see cref="IDebugUiSystem"/> — no need to call this directly.</summary>
    public void RegisterDebugUi(IDebugUiSystem system)
    {
        _debugUiSystems.Add(system);
        _debugUiVisible[system.DebugName] = false;
    }

    /// <summary>Draws the top "Systems" menu bar and any currently-checked panels. Each menu item
    /// toggles independently (multi-select). Only called while <see cref="_menuVisible"/> is true.</summary>
    private void DrawSystemsMenu()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("Systems"))
            {
                foreach (IDebugUiSystem sys in _debugUiSystems)
                {
                    bool visible = _debugUiVisible[sys.DebugName];
                    if (ImGui.MenuItem(sys.DebugName, string.Empty, visible))
                        _debugUiVisible[sys.DebugName] = !visible;
                }
                ImGui.EndMenu();
            }
            ImGui.EndMainMenuBar();
        }

        foreach (IDebugUiSystem sys in _debugUiSystems)
        {
            if (!_debugUiVisible[sys.DebugName]) continue;

            // A default starting size (~5 text lines tall, reasonably wide) so panels aren't tiny
            // single-line slivers before the user has resized them. GetTextLineHeightWithSpacing()
            // already reflects the current font scale, so this stays "5 lines" if UiScale changes.
            // FirstUseEver: only applied the first time this window opens (or with no saved layout);
            // a user resize afterward sticks for the rest of the session.
            float lineHeight = ImGui.GetTextLineHeightWithSpacing();
            var defaultSize = new Vector2(420f, lineHeight * 5f + ImGui.GetFrameHeightWithSpacing());
            ImGui.SetNextWindowSize(defaultSize, ImGuiCond.FirstUseEver);

            bool open = true;
            if (ImGui.Begin(sys.DebugName, ref open))
                sys.DrawDebugUi();
            ImGui.End();

            if (!open) _debugUiVisible[sys.DebugName] = false;
        }
    }

    // ── per-frame API ────────────────────────────────────────────────────────

    /// <summary>The <see cref="ISystem"/> entry point: updates display size, delta time and mouse
    /// state, calls <c>ImGui.NewFrame()</c>, then publishes <see cref="WantCaptureMouse"/> into
    /// <see cref="InputManager.UiWantsMouse"/> for the rest of the frame's systems to read. Register
    /// this at <see cref="SystemStage.Input"/> so it always runs before any system builds ImGui
    /// widgets or reads input.
    ///
    /// Also owns the F1 hotkey: toggles the "Systems" menu bar and, alongside it, mouse capture
    /// (releasing the cursor so panels are actually clickable, same as the FPS-look toggle it
    /// replaces).</summary>
    public void Update(float dt)
    {
        ImGui.SetCurrentContext(_imguiCtx);
        var io = ImGui.GetIO();

        io.DisplaySize = new Vector2(_ctx.Size.X, _ctx.Size.Y);
        io.DeltaTime = MathF.Max(dt, 1f / 1000f);

        // Mouse position + buttons are polled (more reliable than events for held state).
        if (_input.Native.Mice.Count > 0)
        {
            IMouse m = _input.Native.Mice[0];
            io.AddMousePosEvent(m.Position.X, m.Position.Y);
            io.AddMouseButtonEvent(0, m.IsButtonPressed(MouseButton.Left));
            io.AddMouseButtonEvent(1, m.IsButtonPressed(MouseButton.Right));
            io.AddMouseButtonEvent(2, m.IsButtonPressed(MouseButton.Middle));
        }

        if (_scrollX != 0f || _scrollY != 0f)
        {
            io.AddMouseWheelEvent(_scrollX, _scrollY);
            _scrollX = _scrollY = 0f;
        }

        ImGui.NewFrame();
        WantCaptureMouse = io.WantCaptureMouse;
        _input.UiWantsMouse = WantCaptureMouse;
        WantCaptureKeyboard = io.WantCaptureKeyboard;
        _input.UiWantsKeyboard = WantCaptureKeyboard;

        if (_input.WasKeyPressed(Key.F1))
        {
            _menuVisible = !_menuVisible;
            _input.CursorCaptured = !_menuVisible;
        }
        if (_menuVisible)
            DrawSystemsMenu();
    }

    /// <summary>Finalises ImGui's frame and submits its draw data into <see cref="Renderer.CurrentPass"/>.
    /// Call after all other draws for the frame have been issued and before <see cref="Renderer.EndFrame"/>.</summary>
    public void EndFrame()
    {
        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.Render();

        ImDrawDataPtr drawData = ImGui.GetDrawData();
        if (!drawData.Valid || drawData.CmdListsCount == 0)
            return;

        SubmitDrawData(drawData);
    }

    // ── draw data submission ─────────────────────────────────────────────────

    private struct Batch
    {
        public int SX, SY, SW, SH;
        public int IndexStart, IndexCount;
    }

    private readonly List<Batch> _batches = new();

    private void SubmitDrawData(ImDrawDataPtr drawData)
    {
        RenderPassEncoder* pass = _renderer.CurrentPass;
        if (pass == null) return;

        int totalVtx = 0, totalIdx = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr l = drawData.CmdLists[n];
            totalVtx += l.VtxBuffer.Size;
            totalIdx += l.IdxBuffer.Size;
        }
        if (totalVtx == 0 || totalIdx == 0) return;

        // Fully qualified: ClearSkies.Engine.Math (this project's own matrix/vector namespace) shadows
        // System.Math for any unqualified "Math" reference in this assembly.
        if (_vtxScratch.Length < totalVtx) _vtxScratch = new Vertex[System.Math.Max(totalVtx, _vtxScratch.Length * 2)];
        if (_idxScratch.Length < totalIdx) _idxScratch = new uint[System.Math.Max(totalIdx, _idxScratch.Length * 2)];

        _batches.Clear();
        int vtxWrite = 0, idxWrite = 0;
        int fbw = System.Math.Max(0, _ctx.Size.X), fbh = System.Math.Max(0, _ctx.Size.Y);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr list = drawData.CmdLists[n];
            int baseVertex = vtxWrite;

            ImDrawVert* vtxData = (ImDrawVert*)list.VtxBuffer.Data;
            int listVtxCount = list.VtxBuffer.Size;
            for (int i = 0; i < listVtxCount; i++)
            {
                ref readonly ImDrawVert src = ref vtxData[i];
                uint col = src.col;
                _vtxScratch[vtxWrite++] = new Vertex
                {
                    Position = src.pos,
                    Uv = src.uv,
                    Color = new Vector4(
                        (col & 0xFF) / 255f,
                        ((col >> 8) & 0xFF) / 255f,
                        ((col >> 16) & 0xFF) / 255f,
                        ((col >> 24) & 0xFF) / 255f),
                };
            }

            ushort* idxData = (ushort*)list.IdxBuffer.Data;
            int cmdCount = list.CmdBuffer.Size;
            for (int cmdIdx = 0; cmdIdx < cmdCount; cmdIdx++)
            {
                ImDrawCmdPtr cmd = list.CmdBuffer[cmdIdx];
                if (cmd.UserCallback != nint.Zero) continue; // user callbacks unsupported

                Vector4 cr = cmd.ClipRect;
                int sx = (int)cr.X, sy = (int)cr.Y;
                int sw = (int)(cr.Z - cr.X), sh = (int)(cr.W - cr.Y);
                sx = System.Math.Clamp(sx, 0, fbw);
                sy = System.Math.Clamp(sy, 0, fbh);
                sw = System.Math.Clamp(sw, 0, fbw - sx);
                sh = System.Math.Clamp(sh, 0, fbh - sy);
                if (sw <= 0 || sh <= 0) continue;

                int indexStart = idxWrite;
                int elemCount = (int)cmd.ElemCount;
                int idxOffset = (int)cmd.IdxOffset;
                for (int i = 0; i < elemCount; i++)
                    _idxScratch[idxWrite++] = (uint)(baseVertex + idxData[idxOffset + i]);

                _batches.Add(new Batch { SX = sx, SY = sy, SW = sw, SH = sh, IndexStart = indexStart, IndexCount = elemCount });
            }
        }

        if (idxWrite == 0) return;

        UploadGeometry(vtxWrite, idxWrite);
        UpdateProjection(drawData);

        _api.RenderPassEncoderSetPipeline(pass, _pipeline);
        _api.RenderPassEncoderSetBindGroup(pass, 0, _frameBindGroup, 0, null);
        _api.RenderPassEncoderSetBindGroup(pass, 1, _fontBindGroup, 0, null);
        _api.RenderPassEncoderSetVertexBuffer(pass, 0, _vertexBuffer, 0, (ulong)(vtxWrite * Vertex.SizeBytes));
        _api.RenderPassEncoderSetIndexBuffer(pass, _indexBuffer, IndexFormat.Uint32, 0, (ulong)(idxWrite * sizeof(uint)));

        foreach (Batch b in _batches)
        {
            _api.RenderPassEncoderSetScissorRect(pass, (uint)b.SX, (uint)b.SY, (uint)b.SW, (uint)b.SH);
            _api.RenderPassEncoderDrawIndexed(pass, (uint)b.IndexCount, 1, (uint)b.IndexStart, 0, 0);
        }

        // Restore a full-viewport scissor so nothing drawn later in this pass inherits ImGui's clip rect.
        _api.RenderPassEncoderSetScissorRect(pass, 0, 0, (uint)fbw, (uint)fbh);
    }

    private void UploadGeometry(int vtxCount, int idxCount)
    {
        ulong vBytes = (ulong)(vtxCount * Vertex.SizeBytes);
        ulong iBytes = (ulong)(idxCount * sizeof(uint));

        if (_vertexBuffer == null || vBytes > _vertexCapacityBytes)
        {
            if (_vertexBuffer != null) _api.BufferRelease(_vertexBuffer);
            _vertexCapacityBytes = NextPow2(vBytes);
            var desc = new BufferDescriptor { Usage = BufferUsage.Vertex | BufferUsage.CopyDst, Size = _vertexCapacityBytes };
            _vertexBuffer = _api.DeviceCreateBuffer(_ctx.Device, &desc);
        }
        if (_indexBuffer == null || iBytes > _indexCapacityBytes)
        {
            if (_indexBuffer != null) _api.BufferRelease(_indexBuffer);
            _indexCapacityBytes = NextPow2(iBytes);
            var desc = new BufferDescriptor { Usage = BufferUsage.Index | BufferUsage.CopyDst, Size = _indexCapacityBytes };
            _indexBuffer = _api.DeviceCreateBuffer(_ctx.Device, &desc);
        }

        fixed (Vertex* v = _vtxScratch)
            _api.QueueWriteBuffer(_ctx.Queue, _vertexBuffer, 0, v, (nuint)vBytes);
        fixed (uint* i = _idxScratch)
            _api.QueueWriteBuffer(_ctx.Queue, _indexBuffer, 0, i, (nuint)iBytes);
    }

    private static ulong NextPow2(ulong v)
    {
        if (v < 256) return 256;
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16; v |= v >> 32;
        return v + 1;
    }

    /// <summary>Ortho projection mapping ImGui's pixel space (top-left origin, Y-down) to WebGPU NDC
    /// (Y-up). Accounts for <c>DisplayPos</c> even though this engine never offsets it (single viewport).</summary>
    private void UpdateProjection(ImDrawDataPtr drawData)
    {
        float l = drawData.DisplayPos.X;
        float r = l + drawData.DisplaySize.X;
        float t = drawData.DisplayPos.Y;
        float b = t + drawData.DisplaySize.Y;
        if (r <= l || b <= t) return; // degenerate (e.g. minimized window)

        float sx = 2f / (r - l);
        float sy = 2f / (t - b);
        float tx = -1f - sx * l;
        float ty = 1f - sy * t;

        Span<float> m = stackalloc float[]
        {
            sx, 0,  0, 0,
            0,  sy, 0, 0,
            0,  0,  1, 0,
            tx, ty, 0, 1,
        };
        fixed (float* p = m)
            _api.QueueWriteBuffer(_ctx.Queue, _uniformBuffer, 0, p, 64);
    }

    // ── font atlas ────────────────────────────────────────────────────────────

    private void UploadFontAtlas()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int w, out int h);

        var texDesc = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D((uint)w, (uint)h, 1),
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _fontTexture = _api.DeviceCreateTexture(_ctx.Device, &texDesc);

        var dest = new ImageCopyTexture { Texture = _fontTexture, MipLevel = 0, Origin = new Origin3D(0, 0, 0), Aspect = TextureAspect.All };
        var layout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)(w * 4), RowsPerImage = (uint)h };
        var writeSize = new Extent3D((uint)w, (uint)h, 1);
        _api.QueueWriteTexture(_ctx.Queue, &dest, (void*)pixels, (nuint)(w * h * 4), &layout, &writeSize);

        var viewDesc = new TextureViewDescriptor
        {
            Format = TextureFormat.Rgba8Unorm,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All,
        };
        _fontView = _api.TextureCreateView(_fontTexture, &viewDesc);

        var bgEntry = new BindGroupEntry { Binding = 0, TextureView = _fontView };
        var bgDesc = new BindGroupDescriptor { Layout = _textureLayout, EntryCount = 1, Entries = &bgEntry };
        _fontBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &bgDesc);

        io.Fonts.SetTexID((nint)1);
        io.Fonts.ClearTexData(); // free CPU pixel data now that it's uploaded
    }

    // ── pipeline creation ─────────────────────────────────────────────────────

    private void CreatePipeline()
    {
        _shader = CreateShader(Wgsl);

        // group(0): projection uniform + sampler.
        BindGroupLayoutEntry* frameEntries = stackalloc BindGroupLayoutEntry[2];
        frameEntries[0] = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex, Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = 64 } };
        frameEntries[1] = new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering } };
        var frameLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = frameEntries };
        _frameLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &frameLayoutDesc);

        // group(1): the bound texture (font atlas today).
        var texEntry = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment, Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } };
        var texLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &texEntry };
        _textureLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &texLayoutDesc);

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[2];
        layouts[0] = _frameLayout;
        layouts[1] = _textureLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);

        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Linear,
            LodMinClamp = 0,
            LodMaxClamp = 1,
            Compare = CompareFunction.Undefined,
            MaxAnisotropy = 1,
        };
        _sampler = _api.DeviceCreateSampler(_ctx.Device, &samplerDesc);

        var uboDesc = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = 64 };
        _uniformBuffer = _api.DeviceCreateBuffer(_ctx.Device, &uboDesc);

        BindGroupEntry* fgEntries = stackalloc BindGroupEntry[2];
        fgEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = 64 };
        fgEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _sampler };
        var fgDesc = new BindGroupDescriptor { Layout = _frameLayout, EntryCount = 2, Entries = fgEntries };
        _frameBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &fgDesc);

        VertexAttribute* attrs = stackalloc VertexAttribute[3];
        attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
        attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 };
        attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 16, ShaderLocation = 2 };
        var vbLayout = new VertexBufferLayout { ArrayStride = Vertex.SizeBytes, StepMode = VertexStepMode.Vertex, AttributeCount = 3, Attributes = attrs };

        var blend = new BlendState
        {
            Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
            Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
        };
        var colorTarget = new ColorTargetState { Format = _ctx.SurfaceFormat, Blend = &blend, WriteMask = ColorWriteMask.All };

        var vsEntry = (byte*)SilkMarshal.StringToPtr("vs_main", NativeStringEncoding.UTF8);
        var fsEntry = (byte*)SilkMarshal.StringToPtr("fs_main", NativeStringEncoding.UTF8);
        var vertexState = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = 1, Buffers = &vbLayout };
        var fragmentState = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };

        // The main pass carries a depth attachment, so every pipeline used in it (this one included)
        // needs a compatible DepthStencilState — Always/no-write disables testing without breaking
        // pass compatibility. Same trick Renderer's HUD pipeline uses.
        var keep = StencilOperation.Keep;
        var depth = new DepthStencilState
        {
            Format = _ctx.DepthFormat,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.Always,
            StencilFront = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep },
            StencilBack = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep },
        };

        var pipelineDesc = new RenderPipelineDescriptor
        {
            Layout = _pipelineLayout,
            Vertex = vertexState,
            Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, StripIndexFormat = IndexFormat.Undefined, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
            DepthStencil = &depth,
            Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = false },
            Fragment = &fragmentState,
        };
        _pipeline = _api.DeviceCreateRenderPipeline(_ctx.Device, &pipelineDesc);

        SilkMarshal.Free((nint)vsEntry);
        SilkMarshal.Free((nint)fsEntry);
    }

    private ShaderModule* CreateShader(string wgsl)
    {
        var code = (byte*)SilkMarshal.StringToPtr(wgsl, NativeStringEncoding.UTF8);
        var wgslDesc = new ShaderModuleWGSLDescriptor { Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor }, Code = code };
        var desc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };
        var module = _api.DeviceCreateShaderModule(_ctx.Device, &desc);
        SilkMarshal.Free((nint)code);
        return module;
    }

    // ── input wiring ──────────────────────────────────────────────────────────

    // Shares InputManager's Silk.NET input context rather than opening a second one via
    // window.CreateInput() — both simply add handlers to the same underlying devices.
    private void WireInput()
    {
        foreach (IKeyboard kb in _input.Native.Keyboards)
        {
            kb.KeyChar += OnKeyChar;
            kb.KeyDown += OnKeyDown;
            kb.KeyUp += OnKeyUp;
        }
        foreach (IMouse mouse in _input.Native.Mice)
        {
            mouse.Scroll += OnScroll;
        }
    }

    private void OnKeyChar(IKeyboard _, char c)
    {
        ImGui.SetCurrentContext(_imguiCtx);
        ImGui.GetIO().AddInputCharacter(c);
    }

    private void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        ImGui.SetCurrentContext(_imguiCtx);
        var io = ImGui.GetIO();
        UpdateModifiers(io, kb);
        ImGuiKey ik = ToImGuiKey(key);
        if (ik != ImGuiKey.None) io.AddKeyEvent(ik, true);
    }

    private void OnKeyUp(IKeyboard kb, Key key, int _)
    {
        ImGui.SetCurrentContext(_imguiCtx);
        var io = ImGui.GetIO();
        UpdateModifiers(io, kb);
        ImGuiKey ik = ToImGuiKey(key);
        if (ik != ImGuiKey.None) io.AddKeyEvent(ik, false);
    }

    private void OnScroll(IMouse _, ScrollWheel sw)
    {
        _scrollX += sw.X;
        _scrollY += sw.Y;
    }

    private static void UpdateModifiers(ImGuiIOPtr io, IKeyboard kb)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight));
    }

    private static ImGuiKey ToImGuiKey(Key key) => key switch
    {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.AltRight => ImGuiKey.RightAlt,
        Key.F1 => ImGuiKey.F1, Key.F2 => ImGuiKey.F2, Key.F3 => ImGuiKey.F3,
        Key.F4 => ImGuiKey.F4, Key.F5 => ImGuiKey.F5, Key.F6 => ImGuiKey.F6,
        Key.F7 => ImGuiKey.F7, Key.F8 => ImGuiKey.F8, Key.F9 => ImGuiKey.F9,
        Key.F10 => ImGuiKey.F10, Key.F11 => ImGuiKey.F11, Key.F12 => ImGuiKey.F12,
        Key.A => ImGuiKey.A, Key.B => ImGuiKey.B, Key.C => ImGuiKey.C,
        Key.D => ImGuiKey.D, Key.E => ImGuiKey.E, Key.F => ImGuiKey.F,
        Key.G => ImGuiKey.G, Key.H => ImGuiKey.H, Key.I => ImGuiKey.I,
        Key.J => ImGuiKey.J, Key.K => ImGuiKey.K, Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M, Key.N => ImGuiKey.N, Key.O => ImGuiKey.O,
        Key.P => ImGuiKey.P, Key.Q => ImGuiKey.Q, Key.R => ImGuiKey.R,
        Key.S => ImGuiKey.S, Key.T => ImGuiKey.T, Key.U => ImGuiKey.U,
        Key.V => ImGuiKey.V, Key.W => ImGuiKey.W, Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y, Key.Z => ImGuiKey.Z,
        Key.Number0 => ImGuiKey._0, Key.Number1 => ImGuiKey._1,
        Key.Number2 => ImGuiKey._2, Key.Number3 => ImGuiKey._3,
        Key.Number4 => ImGuiKey._4, Key.Number5 => ImGuiKey._5,
        Key.Number6 => ImGuiKey._6, Key.Number7 => ImGuiKey._7,
        Key.Number8 => ImGuiKey._8, Key.Number9 => ImGuiKey._9,
        _ => ImGuiKey.None,
    };

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (IKeyboard kb in _input.Native.Keyboards)
        {
            kb.KeyChar -= OnKeyChar;
            kb.KeyDown -= OnKeyDown;
            kb.KeyUp -= OnKeyUp;
        }
        foreach (IMouse mouse in _input.Native.Mice)
        {
            mouse.Scroll -= OnScroll;
        }

        if (_fontBindGroup != null) _api.BindGroupRelease(_fontBindGroup);
        if (_fontView != null) _api.TextureViewRelease(_fontView);
        if (_fontTexture != null) _api.TextureRelease(_fontTexture);
        if (_vertexBuffer != null) _api.BufferRelease(_vertexBuffer);
        if (_indexBuffer != null) _api.BufferRelease(_indexBuffer);
        if (_pipeline != null) _api.RenderPipelineRelease(_pipeline);
        if (_frameBindGroup != null) _api.BindGroupRelease(_frameBindGroup);
        if (_uniformBuffer != null) _api.BufferRelease(_uniformBuffer);
        if (_sampler != null) _api.SamplerRelease(_sampler);
        if (_pipelineLayout != null) _api.PipelineLayoutRelease(_pipelineLayout);
        if (_frameLayout != null) _api.BindGroupLayoutRelease(_frameLayout);
        if (_textureLayout != null) _api.BindGroupLayoutRelease(_textureLayout);
        if (_shader != null) _api.ShaderModuleRelease(_shader);

        if (_imguiCtx != nint.Zero)
        {
            ImGui.DestroyContext(_imguiCtx);
            _imguiCtx = nint.Zero;
        }
    }
}
