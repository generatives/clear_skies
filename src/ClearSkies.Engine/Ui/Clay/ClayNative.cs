using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Ui;

/// <summary>
/// P/Invoke declarations for the native Clay library's shim (native/clay/clay_shim.c): every call takes pointers and
/// primitives only, never a struct by value, so nothing depends on how a platform passes structs. Booleans cross as
/// ints. The library ships prebuilt in native/clay/bin and is copied next to the executable (see the engine csproj).
/// </summary>
internal static unsafe class ClayNative
{
    private const string Lib = "clay";

    /// <summary>Must equal CLAY_SHIM_VERSION in clay_shim.c; bumped whenever an export's signature changes.</summary>
    public const int ShimVersion = 1;

    // Callbacks from native code, as unmanaged function pointers to [UnmanagedCallersOnly] methods.
    // void (*)(const Clay_ErrorData*, void* userData)
    // void (*)(const Clay_StringSlice*, const Clay_TextElementConfig*, void* userData, Clay_Dimensions* result)

    [DllImport(Lib)] public static extern int ClayShim_Version();
    [DllImport(Lib)] public static extern int ClayShim_LayoutInfo(int* values, int capacity);

    [DllImport(Lib)] public static extern uint ClayShim_MinMemorySize();
    [DllImport(Lib)] public static extern void ClayShim_SetMaxElementCount(int count);
    [DllImport(Lib)] public static extern int ClayShim_GetMaxElementCount();
    [DllImport(Lib)] public static extern void ClayShim_SetMaxMeasureTextCacheWordCount(int count);
    [DllImport(Lib)] public static extern int ClayShim_GetMaxMeasureTextCacheWordCount();

    [DllImport(Lib)]
    public static extern void* ClayShim_Initialize(void* memory, ulong capacity, float width, float height,
        delegate* unmanaged[Cdecl]<ClayErrorData*, void*, void> errorFn, void* errorUserData);

    [DllImport(Lib)]
    public static extern void ClayShim_SetMeasureTextFunction(
        delegate* unmanaged[Cdecl]<ClayStringSlice*, TextConfig*, void*, UiDimensions*, void> fn, void* userData);

    [DllImport(Lib)] public static extern void ClayShim_ResetMeasureTextCache();
    [DllImport(Lib)] public static extern void ClayShim_SetDebugModeEnabled(int enabled);
    [DllImport(Lib)] public static extern int ClayShim_IsDebugModeEnabled();
    [DllImport(Lib)] public static extern void ClayShim_SetCullingEnabled(int enabled);

    [DllImport(Lib)] public static extern void ClayShim_SetLayoutDimensions(float width, float height);
    [DllImport(Lib)] public static extern void ClayShim_SetPointerState(float x, float y, int pointerDown);
    [DllImport(Lib)] public static extern void ClayShim_UpdateScrollContainers(int enableDragScrolling, float scrollX, float scrollY, float deltaTime);
    [DllImport(Lib)] public static extern void ClayShim_BeginLayout();
    [DllImport(Lib)] public static extern void ClayShim_EndLayout(float deltaTime, ClayRenderCommandArray* result);

    [DllImport(Lib)] public static extern void ClayShim_OpenElement(ElementId* id);
    [DllImport(Lib)] public static extern void ClayShim_ConfigureOpenElement(ElementDeclaration* declaration);
    [DllImport(Lib)] public static extern void ClayShim_CloseElement();
    [DllImport(Lib)] public static extern void ClayShim_OpenTextElement(ClayString* text, TextConfig* config);

    [DllImport(Lib)] public static extern void ClayShim_HashString(ClayString* key, uint seed, ElementId* result);
    [DllImport(Lib)] public static extern void ClayShim_HashStringWithOffset(ClayString* key, uint offset, uint seed, ElementId* result);
    [DllImport(Lib)] public static extern uint ClayShim_GetOpenElementId();

    [DllImport(Lib)] public static extern int ClayShim_Hovered();
    [DllImport(Lib)] public static extern int ClayShim_PointerOver(ElementId* id);
    [DllImport(Lib)] public static extern void ClayShim_GetPointerOverIds(ClayElementIdArray* result);
    [DllImport(Lib)] public static extern void ClayShim_GetElementData(ElementId* id, ElementData* result);
    [DllImport(Lib)] public static extern void ClayShim_GetScrollContainerData(ElementId* id, ClayScrollContainerData* result);
    [DllImport(Lib)] public static extern void ClayShim_GetScrollOffset(UiVector2* result);
    [DllImport(Lib)] public static extern void* ClayShim_EaseOutHandler();
}
