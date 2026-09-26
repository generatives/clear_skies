using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Ui;
using ClearSkies.Engine.Voxels;
using Silk.NET.Input;

namespace ClearSkies.Game.Hud;

/// <summary>
/// The in-game HUD, declared each frame on the engine's immediate-mode <see cref="UiContext"/>: a crosshair, and
/// a hotbar along the bottom of the screen showing <see cref="PlayerInputSystem.PlaceableBlocks"/> with the one
/// left-click places highlighted. Number keys 1-9 and 0 pick the first ten slots; with the cursor free (Esc or F1)
/// slots can be clicked. Picking a block shows its name above the hotbar for a moment.
///
/// Sprites come from Resources/Ui (crosshair, slot, slot_selected, panel; see <see cref="LoadSprites"/>). Block icons
/// are made at startup from the block texture atlas, box-filtered down to 16x16 pixel art, or for untextured blocks
/// a bevelled swatch of their color.
/// </summary>
public sealed class HudUi : ISystem
{
    private const float SlotSize = 22f, IconSize = 16f;
    private const float NameShowSeconds = 1.5f, NameFadeSeconds = 0.5f;

    private static readonly Key[] SlotKeys =
    {
        Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
        Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0,
    };

    private static readonly UiColor TextColor = new(245, 232, 205);

    private readonly UiContext _ui;
    private readonly InputManager _input;
    private readonly PlayerInputSystem _player;
    private readonly UiSprite _crosshair, _slot, _slotSelected, _panel;
    private readonly UiSprite[] _icons;
    private readonly string[] _slotNumbers;

    private int _shownIndex = -1;
    private float _nameTimer;

    public HudUi(UiContext ui, InputManager input, PlayerInputSystem player, TextureAtlas? blockTextures)
    {
        _ui = ui;
        _input = input;
        _player = player;
        _crosshair = ui.Atlas.Sprite("crosshair");
        _slot = ui.Atlas.Sprite("slot");
        _slotSelected = ui.Atlas.Sprite("slot_selected");
        _panel = ui.Atlas.Sprite("panel");

        var blocks = PlayerInputSystem.PlaceableBlocks;
        _icons = new UiSprite[blocks.Count];
        _slotNumbers = new string[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            _icons[i] = BlockIcons.Create(ui.Atlas, BlockRegistry.Get(blocks[i]), blockTextures);
            _slotNumbers[i] = i < SlotKeys.Length ? ((i + 1) % 10).ToString() : "";
        }
    }

    /// <summary>Loads the HUD's sprites (every PNG in <paramref name="directory"/>; <c>name.9.png</c> files are
    /// nine-sliced at a third of their size).</summary>
    public static void LoadSprites(UiContext ui, string directory) => ui.Atlas.LoadSprites(directory);

    public void Update(float dt)
    {
        HandleSelection();
        if (_player.PlaceIndex != _shownIndex)
        {
            if (_shownIndex >= 0) _nameTimer = NameShowSeconds + NameFadeSeconds; // not on startup
            _shownIndex = _player.PlaceIndex;
        }
        _nameTimer = MathF.Max(0f, _nameTimer - dt);

        DeclareCrosshair();
        DeclareHotbar();
    }

    private void HandleSelection()
    {
        for (int i = 0; i < SlotKeys.Length && i < _icons.Length; i++)
            if (_input.WasKeyPressed(SlotKeys[i]))
                _player.PlaceIndex = i;

        for (int i = 0; i < _icons.Length; i++)
            if (_ui.Clicked(_ui.Id("hotbar-slot", i)))
                _player.PlaceIndex = i;
    }

    private void DeclareCrosshair()
    {
        var (w, h) = _ui.Atlas.SpriteSize(_crosshair);
        var crosshair = ElementDeclaration.Anchored(AttachPoint.CenterCenter);
        crosshair.Floating.PointerCaptureMode = PointerCaptureMode.Passthrough;
        crosshair.Layout.Sizing = Sizing.Fixed(w, h);
        crosshair.Image = ImageConfig.Sprite(_crosshair);
        using (_ui.Element("crosshair", crosshair)) { }
    }

    private void DeclareHotbar()
    {
        // A column anchored to the bottom centre: the selected block's name, then the bar of slots.
        using (_ui.Element("hotbar", ElementDeclaration.Anchored(AttachPoint.CenterBottom, 0, -6) with
        {
            Layout = new LayoutConfig
            {
                LayoutDirection = LayoutDirection.TopToBottom,
                ChildAlignment = new ChildAlignment(AlignX.Center, AlignY.Bottom),
                ChildGap = 3,
            },
        }))
        {
            DeclareBlockName();

            using (_ui.Element("hotbar-bar", new ElementDeclaration
            {
                Layout = new LayoutConfig { Padding = Padding.All(4), ChildGap = 1 },
                Image = ImageConfig.Sprite(_panel),
            }))
            {
                _ui.BlockPointer();
                for (int i = 0; i < _icons.Length; i++)
                    DeclareSlot(i);
            }
        }
    }

    private void DeclareSlot(int i)
    {
        var id = _ui.Id("hotbar-slot", i);
        bool selected = i == _player.PlaceIndex;
        bool hovered = !selected && _ui.IsHovered(id);
        using (_ui.Element(id, new ElementDeclaration
        {
            Layout = new LayoutConfig
            {
                Sizing = Sizing.Fixed(SlotSize, SlotSize),
                ChildAlignment = ChildAlignment.Center,
            },
            Image = ImageConfig.Sprite(selected ? _slotSelected : _slot),
            // Lightens the slot and its icon under the pointer.
            OverlayColor = hovered ? new UiColor(255, 244, 220, 45) : default,
        }))
        {
            using (_ui.Element(new ElementDeclaration
            {
                Layout = new LayoutConfig { Sizing = Sizing.Fixed(IconSize, IconSize) },
                Image = ImageConfig.Sprite(_icons[i]),
            })) { }

            if (_slotNumbers[i].Length > 0)
            {
                // The key that picks this slot, on a small dark badge over the icon's top-left corner.
                using (_ui.Element(new ElementDeclaration
                {
                    Layout = new LayoutConfig { Padding = new Padding(2, 1, 0, 0) },
                    BackgroundColor = new UiColor(20, 15, 12, 190),
                    CornerRadius = new CornerRadius { BottomRight = 3 },
                    Floating = new FloatingConfig
                    {
                        AttachTo = AttachTo.Parent,
                        AttachPoints = new AttachPoints { Element = AttachPoint.LeftTop, Parent = AttachPoint.LeftTop },
                        Offset = new UiVector2(3, 3),
                        PointerCaptureMode = PointerCaptureMode.Passthrough,
                    },
                }))
                {
                    _ui.Text(_slotNumbers[i], new TextConfig
                    {
                        FontSize = 8,
                        TextColor = selected ? new UiColor(255, 236, 170) : new UiColor(200, 190, 170, 210),
                    });
                }
            }
        }
    }

    private void DeclareBlockName()
    {
        // Holds its height even when hidden, so the bar doesn't jump when the name appears.
        float alpha = System.Math.Clamp(_nameTimer / NameFadeSeconds, 0f, 1f);
        string name = BlockRegistry.Get(PlayerInputSystem.PlaceableBlocks[_player.PlaceIndex]).Name;
        using (_ui.Element("hotbar-name", new ElementDeclaration
        {
            Layout = new LayoutConfig { Padding = Padding.Axes(5, 1) },
            BackgroundColor = new UiColor(20, 15, 12, 170 * alpha),
            CornerRadius = CornerRadius.All(3),
        }))
        {
            _ui.Text(name, new TextConfig { FontSize = 10, TextColor = TextColor.WithAlpha(255 * alpha) });
        }
    }
}
