using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Ui;

/// <summary>
/// Verifies at startup that the C# mirrors in ClayTypes.cs match the native library: the shim version, and the size
/// and field offsets of every shared struct (ClayShim_LayoutInfo lists them in this same order). A mismatch —
/// clay.h updated without updating the mirrors, or a stale library — throws naming the struct and field, instead of
/// silently corrupting memory.
/// </summary>
internal static unsafe class ClayLayoutCheck
{
    private static readonly (string What, int Value)[] Expected =
    {
        Size<ClayString>(), Off<ClayString>("Length"), Off<ClayString>("Chars"),
        Size<ClayStringSlice>(), Off<ClayStringSlice>("Chars"), Off<ClayStringSlice>("BaseChars"),
        Size<ElementId>(), Off<ElementId>("Offset"), Off<ElementId>("BaseId"), Off<ElementId>("StringId"),
        Size<ClayElementIdArray>(), Off<ClayElementIdArray>("InternalArray"),
        Size<SizingAxis>(), Off<SizingAxis>("Type"),
        Size<LayoutConfig>(), Off<LayoutConfig>("Padding"), Off<LayoutConfig>("ChildGap"),
            Off<LayoutConfig>("ChildAlignment"), Off<LayoutConfig>("LayoutDirection"),
        Size<TextConfig>(), Off<TextConfig>("TextColor"), Off<TextConfig>("FontId"),
            Off<TextConfig>("LineHeight"), Off<TextConfig>("WrapMode"), Off<TextConfig>("TextAlignment"),
        Size<FloatingConfig>(), Off<FloatingConfig>("Expand"), Off<FloatingConfig>("ParentId"),
            Off<FloatingConfig>("ZIndex"), Off<FloatingConfig>("AttachPoints"), Off<FloatingConfig>("PointerCaptureMode"),
            Off<FloatingConfig>("AttachTo"), Off<FloatingConfig>("ClipTo"),
        Size<ClipConfig>(), Off<ClipConfig>("ChildOffset"),
        Size<BorderConfig>(), Off<BorderConfig>("Width"),
        Size<TransitionConfig>(), Off<TransitionConfig>("Duration"), Off<TransitionConfig>("Properties"),
            Off<TransitionConfig>("InteractionHandling"), Off<TransitionConfig>("Enter"), Off<TransitionConfig>("Exit"),
        Size<ElementDeclaration>(), Off<ElementDeclaration>("BackgroundColor"), Off<ElementDeclaration>("OverlayColor"),
            Off<ElementDeclaration>("CornerRadius"), Off<ElementDeclaration>("AspectRatio"), Off<ElementDeclaration>("Image"),
            Off<ElementDeclaration>("Floating"), Off<ElementDeclaration>("Custom"), Off<ElementDeclaration>("Clip"),
            Off<ElementDeclaration>("Border"), Off<ElementDeclaration>("Transition"), Off<ElementDeclaration>("UserData"),
        Size<ClayTextRenderData>(), Off<ClayTextRenderData>("TextColor"), Off<ClayTextRenderData>("FontId"),
            Off<ClayTextRenderData>("LineHeight"),
        Size<ClayImageRenderData>(), Off<ClayImageRenderData>("CornerRadius"), Off<ClayImageRenderData>("ImageData"),
        Size<ClayBorderRenderData>(), Off<ClayBorderRenderData>("CornerRadius"), Off<ClayBorderRenderData>("Width"),
        Size<ClayRenderData>(),
        Size<ClayRenderCommand>(), Off<ClayRenderCommand>("RenderData"), Off<ClayRenderCommand>("UserData"),
            Off<ClayRenderCommand>("Id"), Off<ClayRenderCommand>("ZIndex"), Off<ClayRenderCommand>("CommandType"),
        Size<ClayRenderCommandArray>(), Off<ClayRenderCommandArray>("InternalArray"),
        Size<ClayPointerData>(), Off<ClayPointerData>("State"),
        Size<ElementData>(), Off<ElementData>("Found"),
        Size<ClayScrollContainerData>(), Off<ClayScrollContainerData>("ScrollContainerDimensions"),
            Off<ClayScrollContainerData>("ContentDimensions"), Off<ClayScrollContainerData>("Config"),
            Off<ClayScrollContainerData>("Found"),
        Size<ClayErrorData>(), Off<ClayErrorData>("ErrorText"), Off<ClayErrorData>("UserData"),
    };

    private static (string, int) Size<T>() where T : unmanaged => ($"sizeof({typeof(T).Name})", Unsafe.SizeOf<T>());
    private static (string, int) Off<T>(string field) where T : unmanaged =>
        ($"offsetof({typeof(T).Name}.{field})", (int)Marshal.OffsetOf<T>(field));

    /// <summary>Throws if the native library can't be loaded or doesn't match these bindings.</summary>
    public static void Verify()
    {
        int version;
        try { version = ClayNative.ClayShim_Version(); }
        catch (DllNotFoundException e)
        {
            throw new DllNotFoundException(
                "The native Clay library wasn't found next to the executable. It's built from native/clay " +
                "(see native/clay/README.md) and copied there by ClearSkies.Engine.csproj.", e);
        }
        if (version != ClayNative.ShimVersion)
            throw new InvalidOperationException(
                $"Native Clay shim is version {version}, the bindings expect {ClayNative.ShimVersion}: rebuild native/clay.");

        int count = ClayNative.ClayShim_LayoutInfo(null, 0);
        var native = new int[count];
        fixed (int* p = native) ClayNative.ClayShim_LayoutInfo(p, count);
        if (count != Expected.Length)
            throw new InvalidOperationException(
                $"Native Clay reports {count} layout values, the bindings check {Expected.Length}: clay_shim.c's " +
                "ClayShim_LayoutInfo and ClayLayoutCheck.Expected have drifted apart.");

        var mismatches = new List<string>();
        for (int i = 0; i < count; i++)
            if (native[i] != Expected[i].Value)
                mismatches.Add($"{Expected[i].What}: native {native[i]}, C# {Expected[i].Value}");
        if (mismatches.Count > 0)
            throw new InvalidOperationException(
                "The C# Clay struct mirrors (Ui/Clay/ClayTypes.cs) don't match the native library:\n  " +
                string.Join("\n  ", mismatches));
    }
}
