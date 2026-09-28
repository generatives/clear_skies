using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Each tick, from the local player's <see cref="PlayerInput"/>: what the player does to blocks, as commands. Left
/// click places <see cref="PlaceIndex"/> of <see cref="PlaceableBlocks"/> against the targeted face and right click
/// breaks the targeted block (both <see cref="EditVoxels"/>), with a brush in creative mode; G spawns a one-block grid.
/// Left-clicking an <see cref="Interactive"/> block uses it instead: <see cref="BlockInteraction"/>s are published for
/// it until the button is released, with the mouse moving the control (whose system sends SetLever or SetWheel) rather
/// than the view, which follows whatever point the control reports the player has hold of
/// (<see cref="InteractionFocus"/>). Aims from the player's true eye and look, not the drawn camera. Split from the
/// old PlayerInputSystem; the targeted-face highlight is <see cref="BlockTargetSystem"/>.
/// </summary>
public sealed class BlockActionSystem : ISystem, IDisposable, IDebugUiSystem
{
    private readonly World _world;
    private readonly CommandSystem _commands;
    private readonly EditLimits _limits;
    private readonly GridSelection _selection;
    private readonly EntitySet _players;
    private readonly EntitySet _volumes;
    private readonly IDisposable _focusSubscription;

    // The Interactive block being used, from the click on it until the button is released (see BlockInteraction),
    // and the last ray sent for it.
    private bool _interacting;
    private Entity _interactBlock;
    private Vector3D<float> _interactOrigin, _interactDir;
    private Vector3D<float>? _focus; // what the block's control last said the player has hold of

    private static readonly BlockId[] Placeable =
        { BlockId.Stone, BlockId.Wood, BlockId.Grass, BlockId.Dirt, BlockId.Lamp, BlockId.RedLamp, BlockId.GreenLamp,
          BlockId.BlueLamp, BlockId.Fan, BlockId.Buoyant, BlockId.Lever, BlockId.SteeringWheel };
    private static readonly string[] PlaceableNames = Array.ConvertAll(Placeable, id => BlockRegistry.Get(id).Name);

    /// <summary>The blocks the player can place, in hotbar order.</summary>
    public static IReadOnlyList<BlockId> PlaceableBlocks => Placeable;

    private int _placeIndex;
    private BlockId PlaceBlock => Placeable[_placeIndex];

    /// <summary>Which of <see cref="PlaceableBlocks"/> left-click places (the hotbar sets it; L cycles it).</summary>
    public int PlaceIndex
    {
        get => _placeIndex;
        set => _placeIndex = ((value % Placeable.Length) + Placeable.Length) % Placeable.Length;
    }

    private int _brushRadius;

    /// <summary>The brush's radius in blocks (0 = single blocks), up to what the game mode allows.</summary>
    public int BrushRadius
    {
        get => System.Math.Min(_brushRadius, _limits.MaxBrushRadius);
        set => _brushRadius = System.Math.Clamp(value, 0, EditLimits.CreativeBrushRadius);
    }

    /// <summary>Whether the player is using a control (the view is locked to it).</summary>
    public bool Interacting => _interacting;

    public BlockActionSystem(World world, CommandSystem commands, EditLimits limits, GridSelection selection)
    {
        _world = world;
        _commands = commands;
        _limits = limits;
        _selection = selection;
        _players = world.GetEntities().With<LocalPlayer>().With<PlayerInput>().With<Transform>().With<MouseLookComponent>().AsSet();
        _volumes = world.GetEntities().With<ChunkGrid>().With<Transform>().With<NetId>().AsSet();
        _focusSubscription = world.Subscribe<InteractionFocus>((in InteractionFocus f) => _focus = f.Point);
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity player in _players.GetEntities())
        {
            Act(player);
            return;
        }
    }

    private void Act(Entity player)
    {
        ref readonly var input = ref player.Get<PlayerInput>();
        if (!input.Aiming || player.Has<Piloting>())
        {
            EndInteraction(player);
            return;
        }

        var origin = EyeOf(player);
        var dir = BlockRaycast.Direction(input.Yaw, input.Pitch);

        // Using an Interactive block: it has the left button until that comes up, following the ray wherever it
        // points (even off the block, so a drag can overshoot), with no targeting or editing meanwhile.
        if (_interacting)
        {
            if (_interactBlock.IsAlive && input.IsHeld(PlayerButtons.Primary))
            {
                Publish(player, InteractionPhase.Held, origin, dir, new Vector2D<float>(input.MouseDelta.X, input.MouseDelta.Y));
                return;
            }
            EndInteraction(player);
        }

        if (input.WasPressed(PlayerButtons.SpawnGrid)) SpawnGrid(origin, dir);

        if (input.WasPressed(PlayerButtons.CycleBlock))
        {
            PlaceIndex = _placeIndex + 1;
            Console.WriteLine($"[place] selected block: {PlaceBlock}");
        }

        bool place = input.WasPressed(PlayerButtons.Primary), dig = input.WasPressed(PlayerButtons.Secondary);
        if (!place && !dig) return;
        if (BlockRaycast.Nearest(_volumes, origin, dir, _limits.Reach) is not { } hit) return;
        uint volumeId = hit.Root.Get<NetId>().Value;
        uint editor = player.Has<NetId>() ? player.Get<NetId>().Value : 0;
        bool isGrid = hit.Root.Has<DynamicGrid>();

        if (place && hit.Volume.TryGetBlockEntity(hit.Block.X, hit.Block.Y, hit.Block.Z, out var block) && block.Has<Interactive>())
        {
            _interacting = true;
            _interactBlock = block;
            SetLookLocked(player, true);
            Publish(player, InteractionPhase.Began, origin, dir, Vector2D<float>.Zero);
        }
        else if (place)
        {
            var t = hit.Block + hit.Normal;
            if (hit.Volume.GetBlock(t.X, t.Y, t.Z) != BlockId.Air) return;
            // Bottom on the face it was placed against: its top points away from that face, so e.g. a Fan placed
            // against a ship's east wall faces east, away from the ship. Then its north face turns towards the player
            // as far as it can while keeping that: onto whichever axis across the face is nearest the direction to
            // the eye.
            var towards = hit.OriginInVolume - (new Vector3D<float>(t.X, t.Y, t.Z) + new Vector3D<float>(0.5f));
            var orientation = BlockRegistry.Get(PlaceBlock).PlaceOriented
                ? BlockOrientation.Placed(DirectionExtensions.FromNormal(hit.Normal), towards)
                : BlockOrientation.Upright;
            var op = BrushRadius == 0 ? VoxelOp.SetBlock(t, PlaceBlock, orientation) : VoxelOp.FillBox(t, BrushRadius, PlaceBlock, orientation);
            _commands.Send(new EditVoxels { Volume = volumeId, Editor = editor, Ops = new[] { op } });
            if (isGrid) _selection.Select(hit.Root);
            Console.WriteLine($"[place] {PlaceBlock} in {(isGrid ? "grid" : "world")} ({t.X},{t.Y},{t.Z})");
        }
        else
        {
            var op = BrushRadius == 0 ? VoxelOp.SetBlock(hit.Block, BlockId.Air, BlockOrientation.Upright)
                                      : VoxelOp.FillBox(hit.Block, BrushRadius, BlockId.Air, BlockOrientation.Upright);
            _commands.Send(new EditVoxels { Volume = volumeId, Editor = editor, Ops = new[] { op } });
            if (isGrid) _selection.Select(hit.Root);
            Console.WriteLine($"[break] {(isGrid ? "grid" : "world")} ({hit.Block.X},{hit.Block.Y},{hit.Block.Z})");
        }
    }

    private void SpawnGrid(Vector3D<float> eye, Vector3D<float> dir)
    {
        var spawn = eye + dir * 3f;
        DynamicGridFactory.SpawnSingleBlock(_world, _selection, new PhysVec(spawn.X, spawn.Y, spawn.Z), BlockId.Stone);
        Console.WriteLine($"[spawn] grid at ({spawn.X:0.0},{spawn.Y:0.0},{spawn.Z:0.0})");
    }

    private static Vector3D<float> EyeOf(Entity player)
    {
        var p = player.Get<Transform>().Position;
        if (player.Has<CharacterControllerComponent>())
        {
            ref readonly var cc = ref player.Get<CharacterControllerComponent>();
            p += new Vector3D<float>(0, cc.Character.EyeOffset(cc.EyeHeight), 0);
        }
        return p;
    }

    // ── interaction ──────────────────────────────────────────────────────────

    private void Publish(Entity player, InteractionPhase phase, Vector3D<float> origin, Vector3D<float> dir, Vector2D<float> mouseDelta)
    {
        _interactOrigin = origin;
        _interactDir = dir;
        _focus = null;
        _world.Publish(new BlockInteraction(_interactBlock, phase, origin, dir, mouseDelta));
        if (phase != InteractionPhase.Ended && _focus is { } focus) LookAt(player, origin, focus);
    }

    /// <summary>Ends the current interaction, if any, telling its block with the last ray it was sent.</summary>
    private void EndInteraction(Entity player)
    {
        if (!_interacting) return;
        _interacting = false;
        SetLookLocked(player, false);
        Publish(player, InteractionPhase.Ended, _interactOrigin, _interactDir, Vector2D<float>.Zero);
        _interactBlock = default;
    }

    private static void SetLookLocked(Entity player, bool locked)
    {
        if (locked) player.Set(new LookLockedComponent());
        else if (player.Has<LookLockedComponent>()) player.Remove<LookLockedComponent>();
    }

    /// <summary>Turns the player to look straight at <paramref name="point"/> from <paramref name="eye"/>, keeping the
    /// mouse-look angles in step so looking around resumes from there.</summary>
    private static void LookAt(Entity player, Vector3D<float> eye, Vector3D<float> point)
    {
        var toPoint = point - eye;
        if (toPoint.LengthSquared < 1e-8f) return;
        toPoint = Vector3D.Normalize(toPoint);
        ref var look = ref player.Get<MouseLookComponent>();
        float limit = MathF.PI / 2f - 0.01f;
        look.TurnTo(MathF.Atan2(-toPoint.X, -toPoint.Z),
                    System.Math.Clamp(MathF.Asin(System.Math.Clamp(toPoint.Y, -1f, 1f)), -limit, limit));
        player.Get<Transform>().Rotation = look.BodyRotation;
    }

    public void Dispose() => _focusSubscription.Dispose();

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Player Input";

    public void DrawDebugUi()
    {
        ImGui.Combo("Place block", ref _placeIndex, PlaceableNames, PlaceableNames.Length);
        ImGui.TextDisabled("(or press L to cycle)");
        bool creative = _limits.Mode == GameMode.Creative;
        if (ImGui.Checkbox("Creative mode", ref creative)) _limits.Mode = creative ? GameMode.Creative : GameMode.Survival;
        ImGui.TextDisabled($"Reach {_limits.Reach:0} blocks; brush up to radius {_limits.MaxBrushRadius}");
        int radius = BrushRadius;
        if (ImGui.SliderInt("Brush radius", ref radius, 0, _limits.MaxBrushRadius)) BrushRadius = radius;
    }
}
