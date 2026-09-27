// Clay (clay.h) compiled into a shared library for the engine's C# bindings (src/ClearSkies.Engine/Ui/Clay).
//
// clay.h's public API is mostly macros over functions that take and return structs by value, and its callbacks
// receive structs by value too. Rather than rely on each platform's struct-by-value calling convention matching
// between the C compiler and .NET's marshaller, this shim re-exports everything the bindings use with pointer or
// primitive arguments only, and wraps Clay's callbacks in C trampolines that hand C# pointers. Only ClayShim_*
// symbols are exported; Clay's own functions stay internal to the library.
//
// ClayShim_LayoutInfo reports the size and field offsets of every struct the bindings share with Clay, so the C#
// side can verify its mirrors match this build at startup instead of corrupting memory when clay.h changes.
//
// Bump CLAY_SHIM_VERSION whenever an exported signature changes; the C# side checks it.

#define CLAY_IMPLEMENTATION
#include "clay.h"

#define CLAY_SHIM_VERSION 1

#if defined(_WIN32)
#define SHIM_API __declspec(dllexport)
#else
#define SHIM_API __attribute__((visibility("default")))
#endif

typedef void (*ClayShim_ErrorFn)(const Clay_ErrorData *error, void *userData);
typedef void (*ClayShim_MeasureTextFn)(const Clay_StringSlice *text, const Clay_TextElementConfig *config,
                                       void *userData, Clay_Dimensions *result);

// One Clay context per process is all the engine uses, so the callbacks live in statics.
static ClayShim_ErrorFn       shimErrorFn;
static void                  *shimErrorUserData;
static ClayShim_MeasureTextFn shimMeasureFn;

static void ClayShim_ErrorTrampoline(Clay_ErrorData error) {
    if (shimErrorFn) shimErrorFn(&error, shimErrorUserData);
}

static Clay_Dimensions ClayShim_MeasureTrampoline(Clay_StringSlice text, Clay_TextElementConfig *config, void *userData) {
    Clay_Dimensions result = { 0, 0 };
    if (shimMeasureFn) shimMeasureFn(&text, config, userData, &result);
    return result;
}

// ── Setup ───────────────────────────────────────────────────────────────────

SHIM_API int32_t ClayShim_Version(void) { return CLAY_SHIM_VERSION; }

SHIM_API uint32_t ClayShim_MinMemorySize(void) { return Clay_MinMemorySize(); }

SHIM_API void ClayShim_SetMaxElementCount(int32_t count) { Clay_SetMaxElementCount(count); }
SHIM_API int32_t ClayShim_GetMaxElementCount(void) { return Clay_GetMaxElementCount(); }
SHIM_API void ClayShim_SetMaxMeasureTextCacheWordCount(int32_t count) { Clay_SetMaxMeasureTextCacheWordCount(count); }
SHIM_API int32_t ClayShim_GetMaxMeasureTextCacheWordCount(void) { return Clay_GetMaxMeasureTextCacheWordCount(); }

// memory must stay valid (and unmoved) for as long as the returned context is used.
SHIM_API Clay_Context *ClayShim_Initialize(void *memory, uint64_t capacity, float width, float height,
                                           ClayShim_ErrorFn errorFn, void *errorUserData) {
    shimErrorFn = errorFn;
    shimErrorUserData = errorUserData;
    Clay_Arena arena = Clay_CreateArenaWithCapacityAndMemory((size_t)capacity, memory);
    Clay_ErrorHandler handler = { ClayShim_ErrorTrampoline, errorUserData };
    return Clay_Initialize(arena, (Clay_Dimensions) { width, height }, handler);
}

SHIM_API void ClayShim_SetMeasureTextFunction(ClayShim_MeasureTextFn fn, void *userData) {
    shimMeasureFn = fn;
    Clay_SetMeasureTextFunction(ClayShim_MeasureTrampoline, userData);
}

SHIM_API void ClayShim_ResetMeasureTextCache(void) { Clay_ResetMeasureTextCache(); }

SHIM_API void ClayShim_SetDebugModeEnabled(int32_t enabled) { Clay_SetDebugModeEnabled(enabled != 0); }
SHIM_API int32_t ClayShim_IsDebugModeEnabled(void) { return Clay_IsDebugModeEnabled(); }
SHIM_API void ClayShim_SetCullingEnabled(int32_t enabled) { Clay_SetCullingEnabled(enabled != 0); }

// ── Per frame ───────────────────────────────────────────────────────────────

SHIM_API void ClayShim_SetLayoutDimensions(float width, float height) {
    Clay_SetLayoutDimensions((Clay_Dimensions) { width, height });
}

SHIM_API void ClayShim_SetPointerState(float x, float y, int32_t pointerDown) {
    Clay_SetPointerState((Clay_Vector2) { x, y }, pointerDown != 0);
}

SHIM_API void ClayShim_UpdateScrollContainers(int32_t enableDragScrolling, float scrollX, float scrollY, float deltaTime) {
    Clay_UpdateScrollContainers(enableDragScrolling != 0, (Clay_Vector2) { scrollX, scrollY }, deltaTime);
}

SHIM_API void ClayShim_BeginLayout(void) { Clay_BeginLayout(); }

SHIM_API void ClayShim_EndLayout(float deltaTime, Clay_RenderCommandArray *result) {
    *result = Clay_EndLayout(deltaTime);
}

// ── Element declaration ─────────────────────────────────────────────────────

// id == NULL opens an element with an automatically generated id (CLAY_AUTO_ID).
SHIM_API void ClayShim_OpenElement(const Clay_ElementId *id) {
    if (id) Clay__OpenElementWithId(*id);
    else Clay__OpenElement();
}

SHIM_API void ClayShim_ConfigureOpenElement(const Clay_ElementDeclaration *declaration) {
    Clay__ConfigureOpenElementPtr(declaration);
}

SHIM_API void ClayShim_CloseElement(void) { Clay__CloseElement(); }

SHIM_API void ClayShim_OpenTextElement(const Clay_String *text, const Clay_TextElementConfig *config) {
    Clay__OpenTextElement(*text, *config);
}

// ── Ids ─────────────────────────────────────────────────────────────────────

// CLAY_SID / CLAY_SID_LOCAL: seed is 0, or the parent element's id for a local id.
SHIM_API void ClayShim_HashString(const Clay_String *key, uint32_t seed, Clay_ElementId *result) {
    *result = Clay__HashString(*key, seed);
}

// CLAY_SIDI / CLAY_SIDI_LOCAL.
SHIM_API void ClayShim_HashStringWithOffset(const Clay_String *key, uint32_t offset, uint32_t seed, Clay_ElementId *result) {
    *result = Clay__HashStringWithOffset(*key, offset, seed);
}

SHIM_API uint32_t ClayShim_GetOpenElementId(void) { return Clay_GetOpenElementId(); }

// ── Queries ─────────────────────────────────────────────────────────────────

SHIM_API int32_t ClayShim_Hovered(void) { return Clay_Hovered(); }

SHIM_API int32_t ClayShim_PointerOver(const Clay_ElementId *id) { return Clay_PointerOver(*id); }

SHIM_API void ClayShim_GetPointerOverIds(Clay_ElementIdArray *result) { *result = Clay_GetPointerOverIds(); }

SHIM_API void ClayShim_GetElementData(const Clay_ElementId *id, Clay_ElementData *result) {
    *result = Clay_GetElementData(*id);
}

SHIM_API void ClayShim_GetScrollContainerData(const Clay_ElementId *id, Clay_ScrollContainerData *result) {
    *result = Clay_GetScrollContainerData(*id);
}

SHIM_API void ClayShim_GetScrollOffset(Clay_Vector2 *result) { *result = Clay_GetScrollOffset(); }

// Clay's built-in ease-out transition handler, for Clay_TransitionElementConfig.handler.
SHIM_API void *ClayShim_EaseOutHandler(void) { return (void *)Clay_EaseOut; }

// ── Layout self-check ───────────────────────────────────────────────────────

// Sizes and field offsets of the shared structs, in the order the C# side lists them (ClayLayoutCheck.cs). Returns
// how many values there are; writes at most capacity of them.
SHIM_API int32_t ClayShim_LayoutInfo(int32_t *out, int32_t capacity) {
#define SIZE(T)       (int32_t)sizeof(T)
#define OFF(T, field) (int32_t)offsetof(T, field)
    const int32_t values[] = {
        SIZE(Clay_String), OFF(Clay_String, length), OFF(Clay_String, chars),
        SIZE(Clay_StringSlice), OFF(Clay_StringSlice, chars), OFF(Clay_StringSlice, baseChars),
        SIZE(Clay_ElementId), OFF(Clay_ElementId, offset), OFF(Clay_ElementId, baseId), OFF(Clay_ElementId, stringId),
        SIZE(Clay_ElementIdArray), OFF(Clay_ElementIdArray, internalArray),
        SIZE(Clay_SizingAxis), OFF(Clay_SizingAxis, type),
        SIZE(Clay_LayoutConfig), OFF(Clay_LayoutConfig, padding), OFF(Clay_LayoutConfig, childGap),
            OFF(Clay_LayoutConfig, childAlignment), OFF(Clay_LayoutConfig, layoutDirection),
        SIZE(Clay_TextElementConfig), OFF(Clay_TextElementConfig, textColor), OFF(Clay_TextElementConfig, fontId),
            OFF(Clay_TextElementConfig, lineHeight), OFF(Clay_TextElementConfig, wrapMode),
            OFF(Clay_TextElementConfig, textAlignment),
        SIZE(Clay_FloatingElementConfig), OFF(Clay_FloatingElementConfig, expand), OFF(Clay_FloatingElementConfig, parentId),
            OFF(Clay_FloatingElementConfig, zIndex), OFF(Clay_FloatingElementConfig, attachPoints),
            OFF(Clay_FloatingElementConfig, pointerCaptureMode), OFF(Clay_FloatingElementConfig, attachTo),
            OFF(Clay_FloatingElementConfig, clipTo),
        SIZE(Clay_ClipElementConfig), OFF(Clay_ClipElementConfig, childOffset),
        SIZE(Clay_BorderElementConfig), OFF(Clay_BorderElementConfig, width),
        SIZE(Clay_TransitionElementConfig), OFF(Clay_TransitionElementConfig, duration),
            OFF(Clay_TransitionElementConfig, properties), OFF(Clay_TransitionElementConfig, interactionHandling),
            OFF(Clay_TransitionElementConfig, enter), OFF(Clay_TransitionElementConfig, exit),
        SIZE(Clay_ElementDeclaration), OFF(Clay_ElementDeclaration, backgroundColor), OFF(Clay_ElementDeclaration, overlayColor),
            OFF(Clay_ElementDeclaration, cornerRadius), OFF(Clay_ElementDeclaration, aspectRatio),
            OFF(Clay_ElementDeclaration, image), OFF(Clay_ElementDeclaration, floating), OFF(Clay_ElementDeclaration, custom),
            OFF(Clay_ElementDeclaration, clip), OFF(Clay_ElementDeclaration, border), OFF(Clay_ElementDeclaration, transition),
            OFF(Clay_ElementDeclaration, userData),
        SIZE(Clay_TextRenderData), OFF(Clay_TextRenderData, textColor), OFF(Clay_TextRenderData, fontId),
            OFF(Clay_TextRenderData, lineHeight),
        SIZE(Clay_ImageRenderData), OFF(Clay_ImageRenderData, cornerRadius), OFF(Clay_ImageRenderData, imageData),
        SIZE(Clay_BorderRenderData), OFF(Clay_BorderRenderData, cornerRadius), OFF(Clay_BorderRenderData, width),
        SIZE(Clay_RenderData),
        SIZE(Clay_RenderCommand), OFF(Clay_RenderCommand, renderData), OFF(Clay_RenderCommand, userData),
            OFF(Clay_RenderCommand, id), OFF(Clay_RenderCommand, zIndex), OFF(Clay_RenderCommand, commandType),
        SIZE(Clay_RenderCommandArray), OFF(Clay_RenderCommandArray, internalArray),
        SIZE(Clay_PointerData), OFF(Clay_PointerData, state),
        SIZE(Clay_ElementData), OFF(Clay_ElementData, found),
        SIZE(Clay_ScrollContainerData), OFF(Clay_ScrollContainerData, scrollContainerDimensions),
            OFF(Clay_ScrollContainerData, contentDimensions), OFF(Clay_ScrollContainerData, config),
            OFF(Clay_ScrollContainerData, found),
        SIZE(Clay_ErrorData), OFF(Clay_ErrorData, errorText), OFF(Clay_ErrorData, userData),
    };
#undef SIZE
#undef OFF
    int32_t count = (int32_t)(sizeof(values) / sizeof(values[0]));
    for (int32_t i = 0; i < count && i < capacity; i++) out[i] = values[i];
    return count;
}
