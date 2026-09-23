using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// A model's base-colour texture: a single-layer <c>texture_2d_array</c> with its own sampler, wrapped in a
/// bind group on the renderer's group-3 layout — the same slot the block texture array uses, so model draws just
/// swap group 3 for the duration of the draw. Created by <see cref="Renderer.UploadModel"/>.
/// </summary>
public sealed unsafe class ModelTexture : IDisposable
{
    private readonly WebGPU _api;
    internal Texture*     Texture   { get; private set; }
    internal TextureView* View      { get; private set; }
    internal Sampler*     Sampler   { get; private set; }
    internal BindGroup*   BindGroup { get; private set; }

    internal ModelTexture(WebGPU api, Texture* texture, TextureView* view, Sampler* sampler, BindGroup* bindGroup)
    {
        _api      = api;
        Texture   = texture;
        View      = view;
        Sampler   = sampler;
        BindGroup = bindGroup;
    }

    public void Dispose()
    {
        if (BindGroup != null) _api.BindGroupRelease(BindGroup);
        if (Sampler   != null) _api.SamplerRelease(Sampler);
        if (View      != null) _api.TextureViewRelease(View);
        if (Texture   != null) _api.TextureRelease(Texture);
        BindGroup = null; Sampler = null; View = null; Texture = null;
    }
}
