namespace ClearSkies.Engine.Core;

/// <summary>Configuration for an <see cref="EngineHost"/>.</summary>
public sealed record EngineOptions(string Title, int Width, int Height, bool LogGpuErrors);
