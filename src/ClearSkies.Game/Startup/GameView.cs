using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Ui;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Startup;

/// <summary>
/// What a game shown in a window draws with: the texture atlas, block models and chunk meshes, other players' model,
/// the game UI, and the GPU light storage the world streams into, which is what limits how much of it loads
/// (<see cref="Budget"/>). Made before the <see cref="GameWorld"/>, which is given the parts it needs; the view's
/// systems come with <see cref="Systems.ViewSystems"/>.
/// </summary>
public sealed class GameView : IDisposable
{
    public GameView(WindowedEngineHost host, LaunchOptions options)
    {
        Host = host;
        host.Renderer.LoadTextureAtlas(
            Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.png"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.xml"));
        if (options.FrameRateCap is { } fps) host.CapFrameRate(fps);
        host.Input.CursorCaptured = false; // the F1 debug menu starts open, and F1 frees the cursor with it

        // Model blocks' glTF models (BlockDef.Model paths are relative to Resources/Models — see the csproj's link of the
        // blockbench folder), loaded on first use.
        BlockModels = new BlockModelLibrary(host.Renderer, Path.Combine(AppContext.BaseDirectory, "Resources", "Models"));
        Meshes = new ChunkMeshSystem(host.World, host.Renderer, BlockModels);
        PlayerModel = new PlayerModel(host.Renderer);

        // Game UI (immediate mode, laid out by Clay).
        Ui = new UiContext(host.Window, host.Input);
        Ui.AddFont(UiFont.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "PixelifySans.ttf")));
        Ui.Atlas.LoadSprites(Path.Combine(AppContext.BaseDirectory, "Resources", "Ui")); // the HUD's; name.9.png is nine-sliced

        // Shared GPU voxel storage for lighting (world and ships). Its light budget limits how much of the world loads.
        GridStore = new GridStore(host.Context, (int)((long)options.LightBudgetMb * 1024 * 1024 / GridStore.SlotBytes),
                                  LightBudget.WorldIndexDim(options.ViewDistance));
        host.Renderer.AttachGridStore(GridStore);
        Budget = new LightBudget(GridStore);
    }

    public WindowedEngineHost Host { get; }
    public BlockModelLibrary BlockModels { get; }
    public ChunkMeshSystem Meshes { get; }
    public PlayerModel PlayerModel { get; }
    public UiContext Ui { get; }
    public GridStore GridStore { get; }

    /// <summary>How much of the world loads: as much as the light storage holds.</summary>
    public IChunkBudget Budget { get; }

    /// <summary>Packs each loaded chunk for the light storage as it loads.</summary>
    public IChunkPreparer ChunkPreparer { get; } = new LightPacking();

    public void Dispose()
    {
        PlayerModel.Dispose();
        BlockModels.Dispose();
        Ui.Dispose();
        GridStore.Dispose();
    }
}
