# Game UI

The player-facing UI (HUD, and later menus and inventories) is immediate mode: every frame, systems declare the UI
they want from scratch with plain code, and nothing is kept between frames except what the layout library remembers
by element id. ImGui stays the tool for debug panels; this is for the game.

- **Layout**: [Clay](https://github.com/nicbarker/clay), a C layout library built from `native/clay` into a small
  shared library (see `native/clay/README.md`). It does flexbox-style layout, floating elements, scrolling, hover
  tests and transitions, and outputs a flat list of render commands.
- **Bindings and API**: `src/ClearSkies.Engine/Ui`. `UiContext` is what game code talks to; the structs in
  `Clay/ClayTypes.cs` (`ElementDeclaration`, `TextConfig`, ...) are Clay's own configuration structs, filled in
  directly.
- **Rendering**: `UiRenderSystem`, one WebGPU pipeline drawing textured quads from one atlas texture (`UiAtlas`).
  Rounded corners and borders are cut out in the fragment shader, sprites can be nine-sliced, and text is rasterized per
  pixel size with stb_truetype (`UiFont`).
- **The HUD**: `src/ClearSkies.Game/Hud` (crosshair, hotbar), with art in `src/ClearSkies.Game/Resources/Ui` and the
  font (Pixelify Sans, OFL) in `Resources/Fonts`.

## Frame order

1. `UiContext.Update` (Input stage, registered after the ImGui controller) opens the layout. It sets the screen size
   and scale, and the pointer.
2. Logic and PreRender systems declare elements.
3. `UiRenderSystem.Render` (RenderHud stage) closes the layout and draws it. ImGui draws on top afterwards.

Declaring outside that window throws.

## Units and scale

Layouts are in layout units: framebuffer pixels divided by `UiContext.Scale`. The scale is a whole number, so pixel art
stays sharp. Automatically, it is the largest that keeps the screen at least 640x360 units (2 at 1280x720, 3 at 1080p,
4 at 1440p), so **design for a 640x360 screen**. A sprite texel is one layout unit. Font sizes are line heights in
layout units. The "Game UI" debug panel (F1 → Systems) can override the scale.

## Declaring UI

```csharp
using ClearSkies.Engine.Ui;

// A panel pinned to the top-left corner, with a title and a button.
var panel = ElementDeclaration.Anchored(AttachPoint.LeftTop, 8, 8);
panel.Layout = new LayoutConfig
{
    LayoutDirection = LayoutDirection.TopToBottom,
    Padding = Padding.All(6),
    ChildGap = 4,
};
panel.Image = ImageConfig.Sprite(ui.Atlas.Sprite("panel")); // nine-sliced background

using (ui.Element("status-panel", panel))
{
    ui.BlockPointer(); // clicks on this panel don't reach gameplay

    ui.Text($"Altitude {altitude:F0}", new TextConfig { FontSize = 10, TextColor = UiColor.White });

    var button = ui.Id("vent-button");
    if (ui.Clicked(button)) VentBallast();
    using (ui.Element(button, new ElementDeclaration
    {
        Layout = new LayoutConfig { Padding = Padding.Axes(6, 2) },
        BackgroundColor = ui.IsHovered(button) ? new UiColor(90, 70, 50) : new UiColor(60, 45, 35),
        CornerRadius = CornerRadius.All(3),
    }))
    {
        ui.Text("Vent", new TextConfig { FontSize = 10, TextColor = UiColor.White });
    }
}
```

Things to know:

- **Ids.** Anything hovered, clicked or looked up needs an id: `ui.Id("name")`, or `ui.Id("name", index)` for lists.
  Everything else can use the id-less `ui.Element(declaration)`.
- **Hover and clicks use last frame's layout.** That is what is on screen, so you can ask about an element before
  declaring it (as with the button above).
- **Placing top-level pieces.** Top-level HUD pieces should be floating elements attached to the screen
  (`ElementDeclaration.Anchored`). Otherwise they would be laid out next to each other in the root.
- **Colors** are 0–255 per channel, Clay's convention.
  - `BackgroundColor` draws a rectangle, or tints the element's image if it has one.
  - `OverlayColor` blends an element and its children towards a color. It works for highlights and fades.
- **Transitions.** `TransitionConfig` with `Handler = UiContext.EaseOut` animates changes to position, size and colors.
- **Pointer.** The UI only sees the pointer while the cursor is free (Esc or F1). While it's captured for mouse-look,
  nothing is hovered or clicked.

## Sprites and fonts

`UiAtlas.LoadSprites(directory)` loads every PNG in a folder, named after the file. A `name.9.png` is nine-sliced at a
third of its size; pass explicit `NineSlice` insets to `LoadSprite` for anything else. Sprites can also be made from
pixels (`AddSprite`), which is how the hotbar builds its block icons.

### Block icons

A block's inventory icon is `BlockDef.IconTexture`, a PNG in `src/ClearSkies.Game/Resources/Icons`. Model blocks get
theirs baked from their glTF by a tool: a CPU rasterizer at a fixed 3/4 angle and lighting, 32x32 with anti-aliased
edges and a dark outline. Rerun it after changing a model or adding a model block, and commit the PNGs:

```sh
dotnet run --project tools/ClearSkies.IconBaker                 # all model blocks
dotnet run --project tools/ClearSkies.IconBaker -- --block Lever  # just one; see Program.cs for size/angle options
```

Blocks without an `IconTexture` get one generated at startup: their side texture shrunk to 16x16, or a swatch of their
color.

Fonts are added with `ui.AddFont(UiFont.Load(path))`, and their index is the `TextConfig.FontId`. Font 0 is also what
Clay's inspector uses.

## Debugging

The "Game UI" debug panel shows:

- the scale
- render command, quad and draw call counts
- atlas usage
- whether the pointer is over UI

Its "Clay inspector" checkbox turns on Clay's built-in element inspector, which the game UI draws. Clay's errors (duplicate
ids, unbalanced elements, capacity) are logged to the console once each, prefixed `[ui]`.
