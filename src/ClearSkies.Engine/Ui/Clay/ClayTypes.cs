using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Ui;

// Blittable mirrors of clay.h's structs (native/clay/clay.h), field for field and in the same order, so they can be
// handed to Clay by pointer. C bools are bytes here (a C# bool isn't blittable) and Clay's packed enums are byte
// enums. ClayLayoutCheck compares every size and offset against the native build at startup, so edit these only
// alongside clay.h.
//
// The element configuration structs (ElementDeclaration and everything it contains, TextConfig) are the public API
// of UiContext: game code fills them in directly, the same way Clay's C API is used. The render command structs are internal
// to UiRenderSystem.

/// <summary>An RGBA color with each channel 0-255, Clay's convention (e.g. <c>new UiColor(255, 255, 255)</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct UiColor
{
    public float R, G, B, A;

    public UiColor(float r, float g, float b, float a = 255f) { R = r; G = g; B = b; A = a; }

    public static readonly UiColor White = new(255, 255, 255);
    public static readonly UiColor Black = new(0, 0, 0);
    public static readonly UiColor Transparent = new(0, 0, 0, 0);

    /// <summary>The same color with alpha <paramref name="a"/> (0-255).</summary>
    public readonly UiColor WithAlpha(float a) => new(R, G, B, a);

    public readonly bool IsZero => R == 0 && G == 0 && B == 0 && A == 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct UiVector2
{
    public float X, Y;
    public UiVector2(float x, float y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
public struct UiDimensions
{
    public float Width, Height;
    public UiDimensions(float width, float height) { Width = width; Height = height; }
}

[StructLayout(LayoutKind.Sequential)]
public struct UiBoundingBox
{
    public float X, Y, Width, Height;
}

/// <summary>A UTF-8 string Clay reads but doesn't copy: the memory must outlive the frame's layout and rendering
/// (UiContext allocates these; see <see cref="UiContext.Text"/> and <see cref="UiContext.Id(string)"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ClayString
{
    /// <summary>Non-zero only if <see cref="Chars"/> lives for the whole program: Clay then caches text measurements
    /// by pointer rather than by content. UiContext's per-frame text is reused memory, so it leaves this 0.</summary>
    public byte IsStaticallyAllocated;
    public int Length;
    public byte* Chars;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayStringSlice
{
    public int Length;
    public byte* Chars;
    public byte* BaseChars;
}

/// <summary>A hashed element id, from <see cref="UiContext.Id(string)"/> and friends. Only <see cref="Id"/> identifies the
/// element; the rest is what it was hashed from, which Clay's debug view displays.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ElementId
{
    public uint Id;
    public uint Offset;
    public uint BaseId;
    public ClayString StringId;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayElementIdArray
{
    public int Capacity;
    public int Length;
    public ElementId* InternalArray;
}

/// <summary>Corner rounding in layout units: each corner is rounded by a circle of that radius inset into it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CornerRadius
{
    public float TopLeft, TopRight, BottomLeft, BottomRight;

    public CornerRadius(float all) { TopLeft = TopRight = BottomLeft = BottomRight = all; }
    public static CornerRadius All(float radius) => new(radius);
}

// ── Layout ──────────────────────────────────────────────────────────────────

public enum LayoutDirection : byte { LeftToRight, TopToBottom }
public enum AlignX : byte { Left, Right, Center }
public enum AlignY : byte { Top, Bottom, Center }
public enum SizingType : byte { Fit, Grow, Percent, Fixed }

[StructLayout(LayoutKind.Sequential)]
public struct ChildAlignment
{
    public AlignX X;
    public AlignY Y;
    public ChildAlignment(AlignX x, AlignY y) { X = x; Y = y; }
    public static readonly ChildAlignment Center = new(AlignX.Center, AlignY.Center);
}

[StructLayout(LayoutKind.Sequential)]
public struct SizingMinMax
{
    public float Min, Max;
}

/// <summary>How an element is sized along one axis: <see cref="Fit"/> its children (the default), <see cref="Grow"/>
/// into the parent's free space, a <see cref="Percent"/> of the parent, or <see cref="Fixed"/> units.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct SizingAxis
{
    [FieldOffset(0)] public SizingMinMax MinMax; // Fit and Grow: bounds on the final size (Max 0 means unbounded)
    [FieldOffset(0)] public float Percent;       // Percent: 0-1 of the parent's size minus its padding and gaps
    [FieldOffset(8)] public SizingType Type;

    public static SizingAxis Fit(float min = 0, float max = 0) => new() { MinMax = { Min = min, Max = max }, Type = SizingType.Fit };
    public static SizingAxis Grow(float min = 0, float max = 0) => new() { MinMax = { Min = min, Max = max }, Type = SizingType.Grow };
    public static SizingAxis Fixed(float size) => new() { MinMax = { Min = size, Max = size }, Type = SizingType.Fixed };
    public static SizingAxis PercentOf(float fraction) => new() { Percent = fraction, Type = SizingType.Percent };
}

[StructLayout(LayoutKind.Sequential)]
public struct Sizing
{
    public SizingAxis Width, Height;

    public Sizing(SizingAxis width, SizingAxis height) { Width = width; Height = height; }
    public static Sizing Fixed(float width, float height) => new(SizingAxis.Fixed(width), SizingAxis.Fixed(height));
    public static Sizing Grow => new(SizingAxis.Grow(), SizingAxis.Grow());
}

/// <summary>Space in layout units between an element's edge and its children.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Padding
{
    public ushort Left, Right, Top, Bottom;

    public Padding(ushort left, ushort right, ushort top, ushort bottom) { Left = left; Right = right; Top = top; Bottom = bottom; }
    public static Padding All(ushort padding) => new(padding, padding, padding, padding);
    public static Padding Axes(ushort horizontal, ushort vertical) => new(horizontal, horizontal, vertical, vertical);
}

[StructLayout(LayoutKind.Sequential)]
public struct LayoutConfig
{
    public Sizing Sizing;
    public Padding Padding;
    public ushort ChildGap; // between children, along LayoutDirection
    public ChildAlignment ChildAlignment;
    public LayoutDirection LayoutDirection;
}

// ── Text ────────────────────────────────────────────────────────────────────

public enum TextWrapMode : byte { Words, Newlines, None }
public enum TextAlignment : byte { Left, Center, Right }

/// <summary>Clay_TextElementConfig: how a <see cref="UiContext.Text"/> element looks and wraps. <see cref="FontId"/> indexes
/// <see cref="UiContext.Fonts"/> (Clay's own debug view uses font 0); <see cref="FontSize"/> is the line's pixel height in
/// layout units.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TextConfig
{
    public void* UserData;
    public UiColor TextColor;
    public ushort FontId;
    public ushort FontSize;
    public ushort LetterSpacing;
    public ushort LineHeight; // 0: the font's own line height at FontSize
    public TextWrapMode WrapMode;
    public TextAlignment TextAlignment;
}

// ── Element configs ─────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public struct AspectRatioConfig
{
    public float AspectRatio; // width / height
}

/// <summary>Makes the element an image: set with <see cref="Sprite"/>. The element's
/// <see cref="ElementDeclaration.BackgroundColor"/> tints it (left at zero, it's drawn untinted).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ImageConfig
{
    public void* ImageData; // UiSprite handle + 1, so null means "no image"

    public static ImageConfig Sprite(UiSprite sprite) => new() { ImageData = (void*)(nint)(sprite.Index + 1) };
}

public enum AttachPoint : byte
{
    LeftTop, LeftCenter, LeftBottom,
    CenterTop, CenterCenter, CenterBottom,
    RightTop, RightCenter, RightBottom,
}

[StructLayout(LayoutKind.Sequential)]
public struct AttachPoints
{
    public AttachPoint Element; // the point on this element...
    public AttachPoint Parent;  // ...placed at this point on what it's attached to
}

public enum PointerCaptureMode : byte { Capture, Passthrough }
public enum AttachTo : byte { None, Parent, ElementWithId, Root }
public enum ClipTo : byte { None, AttachedParent }

/// <summary>Floating elements sit above the layout (in <see cref="ZIndex"/> order) without taking up space in it,
/// positioned by <see cref="AttachPoints"/> relative to what they're <see cref="AttachTo">attached to</see>. Attached
/// to the root, they're how screen-anchored HUD pieces are placed: see <see cref="ElementDeclaration.Anchored"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FloatingConfig
{
    public UiVector2 Offset;
    public UiDimensions Expand;
    public uint ParentId; // with AttachTo.ElementWithId: that element's ElementId.Id
    public short ZIndex;
    public AttachPoints AttachPoints;
    public PointerCaptureMode PointerCaptureMode;
    public AttachTo AttachTo;
    public ClipTo ClipTo;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CustomConfig
{
    public void* CustomData;
}

/// <summary>Clips children that overflow on the chosen axes, offset by <see cref="ChildOffset"/> (a scroll container
/// passes <see cref="UiContext.ScrollOffset"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ClipConfig
{
    public byte Horizontal;
    public byte Vertical;
    public UiVector2 ChildOffset;
}

[StructLayout(LayoutKind.Sequential)]
public struct BorderWidth
{
    public ushort Left, Right, Top, Bottom;
    public ushort BetweenChildren; // lines between children, drawn as rectangles

    public static BorderWidth All(ushort width) => new() { Left = width, Right = width, Top = width, Bottom = width };
}

[StructLayout(LayoutKind.Sequential)]
public struct BorderConfig
{
    public UiColor Color;
    public BorderWidth Width;
}

/// <summary>Clay_TransitionProperty: which properties a transition animates.</summary>
[Flags]
public enum TransitionProperty
{
    None = 0,
    X = 1, Y = 2, Position = X | Y,
    Width = 4, Height = 8, Dimensions = Width | Height,
    BoundingBox = Position | Dimensions,
    BackgroundColor = 16,
    OverlayColor = 32,
    CornerRadius = 64,
    BorderColor = 128,
    BorderWidth = 256,
    Border = BorderColor | BorderWidth,
}

public enum TransitionInteractionHandling : byte { DisableWhileTransitioningPosition, AllowWhileTransitioningPosition }

/// <summary>Animates changes to the element's <see cref="Properties"/> over <see cref="Duration"/> seconds, eased by
/// <see cref="Handler"/> (<see cref="UiContext.EaseOut"/>, Clay's built-in curve). Clay's enter/exit transitions (function
/// pointers that set the initial or final state) aren't exposed yet; leave them null.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TransitionConfig
{
    public void* Handler;
    public float Duration;
    public TransitionProperty Properties;
    public TransitionInteractionHandling InteractionHandling;
    public TransitionEnter Enter;
    public TransitionExit Exit;

    [StructLayout(LayoutKind.Sequential)]
    public struct TransitionEnter
    {
        public void* SetInitialState;
        public byte Trigger;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TransitionExit
    {
        public void* SetFinalState;
        public byte Trigger;
        public byte SiblingOrdering;
    }
}

/// <summary>
/// Clay_ElementDeclaration: everything about one element. Fill in only what's needed; zero is the default for every
/// field (fit-to-children sizing, left-to-right, no background, not floating). Opened with
/// <see cref="UiContext.Element(string, in ElementDeclaration)"/>. A background color alone draws a rectangle; with
/// <see cref="Image"/> set it tints the image instead.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ElementDeclaration
{
    public LayoutConfig Layout;
    public UiColor BackgroundColor;
    /// <summary>Blends this element and all its children towards this color by its alpha (like an image editor's
    /// "color overlay").</summary>
    public UiColor OverlayColor;
    public CornerRadius CornerRadius;
    public AspectRatioConfig AspectRatio;
    public ImageConfig Image;
    public FloatingConfig Floating;
    public CustomConfig Custom;
    public ClipConfig Clip;
    public BorderConfig Border;
    public TransitionConfig Transition;
    public void* UserData;

    /// <summary>A floating element pinned to <paramref name="anchor"/> of the screen (the layout root), nudged by
    /// <paramref name="offset"/> — e.g. <c>Anchored(AttachPoint.CenterBottom, 0, -8)</c> for a hotbar. Set the rest of
    /// the declaration on the returned value.</summary>
    public static ElementDeclaration Anchored(AttachPoint anchor, float offsetX = 0, float offsetY = 0, short zIndex = 0) => new()
    {
        Floating = new FloatingConfig
        {
            AttachTo = AttachTo.Root,
            AttachPoints = new AttachPoints { Element = anchor, Parent = anchor },
            Offset = new UiVector2(offsetX, offsetY),
            ZIndex = zIndex,
        },
    };
}

// ── Render commands (internal) ──────────────────────────────────────────────

internal enum RenderCommandType : byte
{
    None, Rectangle, Border, Text, Image, ScissorStart, ScissorEnd, OverlayColorStart, OverlayColorEnd, Custom,
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClayTextRenderData
{
    public ClayStringSlice StringContents;
    public UiColor TextColor;
    public ushort FontId;
    public ushort FontSize;
    public ushort LetterSpacing;
    public ushort LineHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClayRectangleRenderData
{
    public UiColor BackgroundColor;
    public CornerRadius CornerRadius;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayImageRenderData
{
    public UiColor BackgroundColor;
    public CornerRadius CornerRadius;
    public void* ImageData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClayBorderRenderData
{
    public UiColor Color;
    public CornerRadius CornerRadius;
    public BorderWidth Width;
}

[StructLayout(LayoutKind.Explicit)]
internal struct ClayRenderData
{
    [FieldOffset(0)] public ClayRectangleRenderData Rectangle;
    [FieldOffset(0)] public ClayTextRenderData Text;
    [FieldOffset(0)] public ClayImageRenderData Image;
    [FieldOffset(0)] public ClayImageRenderData Custom; // same layout as Clay_CustomRenderData
    [FieldOffset(0)] public ClayBorderRenderData Border;
    [FieldOffset(0)] public UiColor OverlayColor;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayRenderCommand
{
    public UiBoundingBox BoundingBox;
    public ClayRenderData RenderData;
    public void* UserData;
    public uint Id;
    public short ZIndex;
    public RenderCommandType CommandType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayRenderCommandArray
{
    public int Capacity;
    public int Length;
    public ClayRenderCommand* InternalArray;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ClayPointerData
{
    public UiVector2 Position;
    public byte State;
}

/// <summary>An element's layout from the most recent frame: <see cref="BoundingBox"/> is in layout units from the
/// top-left of the screen. <see cref="Found"/> is 0 if no element had that id.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ElementData
{
    public UiBoundingBox BoundingBox;
    public byte Found;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayScrollContainerData
{
    public UiVector2* ScrollPosition;
    public UiDimensions ScrollContainerDimensions;
    public UiDimensions ContentDimensions;
    public ClipConfig Config;
    public byte Found;
}

internal enum ClayErrorType : byte
{
    TextMeasurementFunctionNotProvided,
    ArenaCapacityExceeded,
    ElementsCapacityExceeded,
    TextMeasurementCapacityExceeded,
    DuplicateId,
    FloatingContainerParentNotFound,
    PercentageOver1,
    InternalError,
    UnbalancedOpenClose,
    HashMapCapacityExceeded,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClayErrorData
{
    public ClayErrorType ErrorType;
    public ClayString ErrorText;
    public void* UserData;
}
