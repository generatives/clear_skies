namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Where the world wants clouds: 0 in open sky up to 1 around land, per world (x, z). <see cref="CloudLayer"/> blends
/// its cloud coverage between <see cref="SkySettings.CloudCoverageOpen"/> and
/// <see cref="SkySettings.CloudCoverageIslands"/> by it. Sampled on a coarse grid from worker threads, so
/// implementations must be thread-safe; it should vary smoothly over hundreds of blocks.
/// </summary>
public interface ICloudDensityMap
{
    float Density(float x, float z);
}
