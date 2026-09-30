namespace ClearSkies.Engine.Core;

/// <summary>Configuration for a <see cref="WindowedEngineHost"/>'s window and GPU.</summary>
public sealed record EngineOptions(string Title, int Width, int Height, bool LogGpuErrors);
