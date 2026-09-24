using ClearSkies.Engine.Rendering.Gltf;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// The GPU models of model blocks (<see cref="BlockDef.Model"/>), loaded from <c>{root}/{BlockDef.Model}</c> on
/// first use and shared by every placed block of that type. A model that fails to load is logged once and the
/// block then draws nothing (it still exists, collides and can be broken). Main thread only (uploads to the GPU).
/// </summary>
public sealed class BlockModelLibrary : IDisposable
{
    private readonly Renderer _renderer;
    private readonly string _root;
    private readonly GpuModel?[] _models = new GpuModel?[256];
    private readonly bool[] _tried = new bool[256];

    /// <param name="root">Folder <see cref="BlockDef.Model"/> paths are relative to (the game's Resources/Models).</param>
    public BlockModelLibrary(Renderer renderer, string root)
    {
        _renderer = renderer;
        _root     = root;
    }

    /// <summary>The model for <paramref name="id"/>, or null when it isn't a model block or its model failed to load.</summary>
    public GpuModel? Get(BlockId id)
    {
        int i = (byte)id;
        if (_tried[i]) return _models[i];
        _tried[i] = true;

        var path = BlockRegistry.Get(id).Model;
        if (path == null) return null;
        try
        {
            _models[i] = _renderer.UploadModel(GltfLoader.Load(Path.Combine(_root, path)));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[block-model] {id}: failed to load '{path}': {e.Message}");
        }
        return _models[i];
    }

    public void Dispose()
    {
        foreach (var m in _models) m?.Dispose();
    }
}
