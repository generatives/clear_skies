using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Windowing;
using ImGuiNET;
using Silk.NET.Input;

namespace ClearSkies.Engine.Ui;

/// <summary>
/// The game UI: immediate-mode, laid out by Clay (native/clay) and drawn by UiRenderSystem. Every frame
/// systems declare the UI they want to show from scratch — nothing persists between frames except what Clay keeps
/// keyed by element id (last frame's layout, for hover and clicks):
///
/// <code>
/// using (ui.Element("hotbar", ElementDeclaration.Anchored(AttachPoint.CenterBottom, 0, -8) with { ... }))
/// {
///     ui.Text("Hello", new TextConfig { FontSize = 10, TextColor = UiColor.White });
/// }
/// </code>
///
/// Frame order: <see cref="Update"/> (registered in <see cref="SystemStage.Input"/>, after the ImGui controller) opens
/// the layout; Logic and PreRender systems declare elements; UiRenderSystem (in
/// <see cref="SystemStage.RenderHud"/>) closes it and draws. Declaring outside that window throws.
///
/// Units: layouts are in "layout units", framebuffer pixels divided by <see cref="Scale"/> — an integer, so pixel-art
/// sprites stay crisp. By default it's the largest that keeps the layout at least
/// <see cref="MinLayoutWidth"/> x <see cref="MinLayoutHeight"/>, so a UI designed for 640x360 fits any window.
///
/// Input: while the cursor is captured for mouse-look the UI sees no pointer. When it's free, elements under it are
/// hovered, <see cref="Clicked"/> reports clicks, and elements marked with <see cref="BlockPointer"/> take the mouse
/// from gameplay (via <see cref="InputManager.UiWantsMouse"/>).
/// </summary>
public sealed unsafe class UiContext : ISystem, IDebugUiSystem, IDisposable
{
    public const float MinLayoutWidth = 640f, MinLayoutHeight = 360f;

    private readonly GameWindow _window;
    private readonly InputManager _input;
    private void* _memory;
    private GCHandle _self;
    private readonly List<UiFont> _fonts = new();
    private readonly Utf8Arena _frameText = new();
    private readonly Dictionary<string, (ClayString Name, ElementId Id)> _ids = new();
    private readonly HashSet<string> _reportedErrors = new();

    private bool _layoutOpen;
    private bool _pointerDown, _pointerPressed;
    private bool _pressLatched; // a left press seen by event since the last frame, in case it was also released
    private float _scrollX, _scrollY;
    // Elements that take the mouse from gameplay: marked while declaring this frame, tested against the pointer
    // next frame (when this frame's layout is the one the pointer is over).
    private HashSet<uint> _blocking = new(), _blockingNext = new();

    public UiAtlas Atlas { get; }

    /// <summary>Fonts by <see cref="TextConfig.FontId"/>. Font 0 is also what Clay's debug view uses.</summary>
    public IReadOnlyList<UiFont> Fonts => _fonts;

    /// <summary>Framebuffer pixels per layout unit this frame.</summary>
    public int Scale { get; private set; } = 1;

    /// <summary>Forces <see cref="Scale"/> (0 = automatic).</summary>
    public int ScaleOverride { get; set; }

    /// <summary>The screen's size in layout units this frame.</summary>
    public UiDimensions LayoutSize { get; private set; }

    /// <summary>True while the pointer is over an element marked with <see cref="BlockPointer"/>.</summary>
    public bool WantsMouse { get; private set; }

    /// <summary>Clay's built-in ease-out curve, for <see cref="TransitionConfig.Handler"/>.</summary>
    public static void* EaseOut => ClayNative.ClayShim_EaseOutHandler();

    // Stats for the debug panel, filled in by UiRenderSystem.
    internal int LastRenderCommands, LastDrawCalls, LastQuads;

    public UiContext(GameWindow window, InputManager input, int maxElements = 8192, int atlasSize = 2048)
    {
        _window = window;
        _input = input;
        Atlas = new UiAtlas(atlasSize);

        ClayLayoutCheck.Verify();
        ClayNative.ClayShim_SetMaxElementCount(maxElements);
        uint size = ClayNative.ClayShim_MinMemorySize();
        _memory = NativeMemory.AlignedAlloc(size, 64);
        _self = GCHandle.Alloc(this);
        void* self = (void*)GCHandle.ToIntPtr(_self);

        UpdateLayoutSize();
        ClayNative.ClayShim_Initialize(_memory, size, LayoutSize.Width, LayoutSize.Height, &OnClayError, self);
        ClayNative.ClayShim_SetMeasureTextFunction(&MeasureText, self);

        foreach (IMouse mouse in _input.Native.Mice)
        {
            mouse.Scroll += OnScroll;
            mouse.MouseDown += OnMouseDown;
        }
    }

    /// <summary>Adds a font; its index is the <see cref="TextConfig.FontId"/> that selects it.</summary>
    public ushort AddFont(UiFont font)
    {
        _fonts.Add(font);
        ClayNative.ClayShim_ResetMeasureTextCache();
        return (ushort)(_fonts.Count - 1);
    }

    /// <summary>The pixel size glyphs of <paramref name="fontSize"/> (layout units) are rasterized at.</summary>
    internal int PixelSize(float fontSize) => System.Math.Max(1, (int)MathF.Round(fontSize * Scale));

    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>Opens this frame's layout: the screen size and scale, and the pointer (from last frame's layout,
    /// which is what's on screen). Register in <see cref="SystemStage.Input"/> after the ImGui controller, whose own
    /// update resets <see cref="InputManager.UiWantsMouse"/>.</summary>
    public void Update(float dt)
    {
        if (_layoutOpen) EndLayout(dt, out _); // the last frame wasn't drawn (e.g. minimized): discard its layout

        int oldScale = Scale;
        UpdateLayoutSize();
        if (Scale != oldScale) ClayNative.ClayShim_ResetMeasureTextCache(); // measurements depend on pixel size
        ClayNative.ClayShim_SetLayoutDimensions(LayoutSize.Width, LayoutSize.Height);

        UpdatePointer(dt);

        (_blocking, _blockingNext) = (_blockingNext, _blocking);
        _blockingNext.Clear();
        WantsMouse = false;
        if (_blocking.Count > 0)
        {
            ClayElementIdArray over;
            ClayNative.ClayShim_GetPointerOverIds(&over);
            for (int i = 0; i < over.Length && !WantsMouse; i++)
                WantsMouse = _blocking.Contains(over.InternalArray[i].Id);
        }
        if (WantsMouse) _input.UiWantsMouse = true;

        _frameText.Reset();
        ClayNative.ClayShim_BeginLayout();
        _layoutOpen = true;
    }

    private void UpdateLayoutSize()
    {
        var fb = _window.FramebufferSize;
        int w = System.Math.Max(1, fb.X), h = System.Math.Max(1, fb.Y);
        int auto = System.Math.Max(1, (int)System.Math.Min(w / MinLayoutWidth, h / MinLayoutHeight));
        Scale = ScaleOverride > 0 ? ScaleOverride : auto;
        LayoutSize = new UiDimensions((float)w / Scale, (float)h / Scale);
    }

    private void UpdatePointer(float dt)
    {
        IMouse? mouse = _input.Native.Mice.Count > 0 ? _input.Native.Mice[0] : null;
        bool free = mouse != null && !_input.CursorCaptured;
        // A click shorter than a frame is released again before it can be polled, so presses are also latched from
        // the button event.
        bool held = free && mouse!.IsButtonPressed(MouseButton.Left);
        _pointerPressed = free && (_pressLatched || (held && !_pointerDown));
        _pressLatched = false;
        bool down = held || _pointerPressed;
        _pointerDown = held;

        float x = -1e6f, y = -1e6f; // nowhere: nothing is hovered while the cursor drives the camera
        if (free)
        {
            // Mouse positions are in window coordinates, which differ from framebuffer pixels on high-DPI displays.
            var windowSize = _window.Native.Size;
            var fb = _window.FramebufferSize;
            float toPixelsX = windowSize.X > 0 ? (float)fb.X / windowSize.X : 1f;
            float toPixelsY = windowSize.Y > 0 ? (float)fb.Y / windowSize.Y : 1f;
            x = mouse!.Position.X * toPixelsX / Scale;
            y = mouse.Position.Y * toPixelsY / Scale;
        }
        ClayNative.ClayShim_SetPointerState(x, y, down ? 1 : 0);

        const float ScrollSpeed = 24f; // layout units per wheel notch
        ClayNative.ClayShim_UpdateScrollContainers(0, free ? _scrollX * ScrollSpeed : 0, free ? _scrollY * ScrollSpeed : 0, dt);
        _scrollX = _scrollY = 0;
    }

    private void OnMouseDown(IMouse _, MouseButton button)
    {
        if (button == MouseButton.Left) _pressLatched = true;
    }

    private void OnScroll(IMouse _, ScrollWheel wheel)
    {
        _scrollX += wheel.X;
        _scrollY += wheel.Y;
    }

    /// <summary>Closes the layout and returns its render commands, valid until the next <see cref="Update"/>.
    /// Called by UiRenderSystem; false if no layout was open.</summary>
    internal bool EndLayout(float dt, out ClayRenderCommandArray commands)
    {
        commands = default;
        if (!_layoutOpen) return false;
        _layoutOpen = false;
        ClayRenderCommandArray result;
        ClayNative.ClayShim_EndLayout(dt, &result);
        commands = result;
        return true;
    }

    // ── Declaring elements ───────────────────────────────────────────────────

    /// <summary>Closes the element it was opened with when disposed: use with <c>using</c>, declaring children in
    /// its block.</summary>
    public readonly ref struct ElementScope
    {
        public void Dispose() => ClayNative.ClayShim_CloseElement();
    }

    /// <summary>Opens an element with an automatically generated id (fine for anything that isn't hovered, clicked
    /// or looked up).</summary>
    public ElementScope Element(in ElementDeclaration declaration)
    {
        EnsureOpen();
        ClayNative.ClayShim_OpenElement(null);
        fixed (ElementDeclaration* d = &declaration) ClayNative.ClayShim_ConfigureOpenElement(d);
        return default;
    }

    public ElementScope Element(string id, in ElementDeclaration declaration) => Element(Id(id), declaration);

    public ElementScope Element(ElementId id, in ElementDeclaration declaration)
    {
        EnsureOpen();
        ClayNative.ClayShim_OpenElement(&id);
        fixed (ElementDeclaration* d = &declaration) ClayNative.ClayShim_ConfigureOpenElement(d);
        return default;
    }

    /// <summary>A text element in the currently open element. The text is copied, so any span will do.</summary>
    public void Text(ReadOnlySpan<char> text, in TextConfig config)
    {
        EnsureOpen();
        ClayString s = _frameText.Add(text);
        fixed (TextConfig* c = &config) ClayNative.ClayShim_OpenTextElement(&s, c);
    }

    /// <summary>Marks the currently open element as taking the mouse from gameplay while the pointer is over it
    /// (panels, buttons).</summary>
    public void BlockPointer()
    {
        EnsureOpen();
        _blockingNext.Add(ClayNative.ClayShim_GetOpenElementId());
    }

    private void EnsureOpen()
    {
        if (!_layoutOpen)
            throw new InvalidOperationException(
                "UI declared outside the frame's layout: declare it from a Logic or PreRender system (UiContext.Update " +
                "opens the layout in the Input stage and UiRenderSystem closes it).");
    }

    // ── Ids ──────────────────────────────────────────────────────────────────

    /// <summary>The id for <paramref name="name"/> (Clay's CLAY_ID). Names are interned: cheap to call every frame and
    /// safe to keep.</summary>
    public ElementId Id(string name) => Intern(name).Id;

    /// <summary>The id for item <paramref name="index"/> of <paramref name="name"/> (CLAY_IDI), for lists.</summary>
    public ElementId Id(string name, int index)
    {
        var interned = Intern(name).Name;
        ElementId id;
        ClayNative.ClayShim_HashStringWithOffset(&interned, (uint)index, 0, &id);
        return id;
    }

    /// <summary>An id unique only within the currently open element (CLAY_ID_LOCAL).</summary>
    public ElementId LocalId(string name, int index = 0)
    {
        EnsureOpen();
        var interned = Intern(name).Name;
        ElementId id;
        ClayNative.ClayShim_HashStringWithOffset(&interned, (uint)index, ClayNative.ClayShim_GetOpenElementId(), &id);
        return id;
    }

    private (ClayString Name, ElementId Id) Intern(string name)
    {
        if (_ids.TryGetValue(name, out var entry)) return entry;
        // Kept for the life of the process: ids are a small, fixed set of names.
        int length = Encoding.UTF8.GetByteCount(name);
        byte* chars = (byte*)NativeMemory.Alloc((nuint)System.Math.Max(length, 1));
        Encoding.UTF8.GetBytes(name, new Span<byte>(chars, length));
        var str = new ClayString { IsStaticallyAllocated = 1, Length = length, Chars = chars };
        ElementId id;
        ClayNative.ClayShim_HashString(&str, 0, &id);
        _ids[name] = entry = (str, id);
        return entry;
    }

    // ── Queries (against last frame's layout) ────────────────────────────────

    /// <summary>True if the pointer is over the element (or one of its floating children).</summary>
    public bool IsHovered(ElementId id) => ClayNative.ClayShim_PointerOver(&id) != 0;

    public bool IsHovered(string id) => IsHovered(Id(id));

    /// <summary>True the frame the left button goes down over the element.</summary>
    public bool Clicked(ElementId id) => _pointerPressed && IsHovered(id);

    public bool Clicked(string id) => Clicked(Id(id));

    /// <summary>True while the left button is held, having gone down anywhere.</summary>
    public bool PointerDown => _pointerDown;

    /// <summary>The element's position and size in last frame's layout.</summary>
    public ElementData GetElementData(ElementId id)
    {
        ElementData data;
        ClayNative.ClayShim_GetElementData(&id, &data);
        return data;
    }

    /// <summary>Scroll position of the currently open element, for its <see cref="ClipConfig.ChildOffset"/>.</summary>
    public UiVector2 ScrollOffset
    {
        get
        {
            UiVector2 offset;
            ClayNative.ClayShim_GetScrollOffset(&offset);
            return offset;
        }
    }

    // ── Clay callbacks ───────────────────────────────────────────────────────

    private static UiContext FromUserData(void* userData) => (UiContext)GCHandle.FromIntPtr((nint)userData).Target!;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MeasureText(ClayStringSlice* text, TextConfig* config, void* userData, UiDimensions* result)
    {
        try { *result = FromUserData(userData).Measure(new ReadOnlySpan<byte>(text->Chars, text->Length), *config); }
        catch (Exception e)
        {
            // An exception can't unwind through native frames; report it and measure nothing.
            Console.Error.WriteLine($"[ui] text measurement failed: {e}");
            *result = default;
        }
    }

    internal UiDimensions Measure(ReadOnlySpan<byte> utf8, in TextConfig config)
    {
        float lineHeight = config.LineHeight;
        if (_fonts.Count == 0)
            return new UiDimensions(utf8.Length * config.FontSize * 0.5f, lineHeight > 0 ? lineHeight : config.FontSize);

        UiFont font = _fonts[config.FontId < _fonts.Count ? config.FontId : 0];
        int px = PixelSize(config.FontSize);
        float width = font.MeasureWidth(utf8, px, config.LetterSpacing * Scale) / Scale;
        return new UiDimensions(width, lineHeight > 0 ? lineHeight : font.LineHeight(px) / Scale);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnClayError(ClayErrorData* error, void* userData)
    {
        try
        {
            string text = Encoding.UTF8.GetString(error->ErrorText.Chars, error->ErrorText.Length);
            string message = $"[ui] Clay {error->ErrorType}: {text}";
            // Most errors repeat every frame until the UI code is fixed; say each once.
            if (FromUserData(userData)._reportedErrors.Add(message))
                Console.Error.WriteLine(message);
        }
        catch (Exception e) { Console.Error.WriteLine($"[ui] Clay error (unreadable): {e.Message}"); }
    }

    // ── Debug panel ──────────────────────────────────────────────────────────

    public string DebugName => "Game UI";

    public void DrawDebugUi()
    {
        ImGui.Text($"Layout {LayoutSize.Width:F0} x {LayoutSize.Height:F0} units at scale {Scale}");
        int scale = ScaleOverride;
        if (ImGui.SliderInt("Scale override (0 = auto)", ref scale, 0, 6)) ScaleOverride = scale;
        ImGui.Text($"Render commands: {LastRenderCommands}, quads: {LastQuads}, draw calls: {LastDrawCalls}");
        ImGui.Text($"Atlas: {Atlas.SpriteCount} sprites, {Atlas.UsedHeight * 100 / Atlas.Size}% of {Atlas.Size}x{Atlas.Size} used");
        ImGui.Text($"Pointer over blocking UI: {WantsMouse}");
        bool debugView = ClayNative.ClayShim_IsDebugModeEnabled() != 0;
        if (ImGui.Checkbox("Clay inspector", ref debugView)) ClayNative.ClayShim_SetDebugModeEnabled(debugView ? 1 : 0);
        ImGui.TextDisabled("Clay's element inspector, drawn by the game UI on the right; needs the cursor free (F1). It is 400 units wide, so a scale override of 1 leaves more room.");
    }

    public void Dispose()
    {
        foreach (IMouse mouse in _input.Native.Mice)
        {
            mouse.Scroll -= OnScroll;
            mouse.MouseDown -= OnMouseDown;
        }
        foreach (var font in _fonts) font.Dispose();
        _frameText.Dispose();
        foreach (var (name, _) in _ids.Values) NativeMemory.Free(name.Chars);
        _ids.Clear();
        if (_memory != null) NativeMemory.AlignedFree(_memory);
        _memory = null;
        if (_self.IsAllocated) _self.Free();
    }
}

/// <summary>This frame's text as UTF-8, in native chunks that never move (Clay keeps pointers into them until the
/// frame is drawn) and are reused from the next frame on.</summary>
internal sealed unsafe class Utf8Arena : IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private readonly List<(nint Ptr, int Size)> _chunks = new();
    private int _chunk, _used;

    public ClayString Add(ReadOnlySpan<char> text)
    {
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte* dst = Allocate(max);
        int length = Encoding.UTF8.GetBytes(text, new Span<byte>(dst, max));
        _used -= max - length; // give back what the text didn't need
        return new ClayString { IsStaticallyAllocated = 0, Length = length, Chars = dst };
    }

    private byte* Allocate(int bytes)
    {
        while (_chunk < _chunks.Count && _used + bytes > _chunks[_chunk].Size)
        {
            _chunk++;
            _used = 0;
        }
        if (_chunk == _chunks.Count)
        {
            int size = System.Math.Max(ChunkSize, bytes);
            _chunks.Add(((nint)NativeMemory.Alloc((nuint)size), size));
            _used = 0;
        }
        byte* p = (byte*)_chunks[_chunk].Ptr + _used;
        _used += bytes;
        return p;
    }

    public void Reset()
    {
        _chunk = 0;
        _used = 0;
    }

    public void Dispose()
    {
        foreach (var (ptr, _) in _chunks) NativeMemory.Free((void*)ptr);
        _chunks.Clear();
    }
}
