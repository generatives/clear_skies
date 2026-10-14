namespace ClearSkies.Engine.Core;

/// <summary>Configuration for a <see cref="WindowedEngineHost"/>'s window and GPU.</summary>
/// <param name="MsaaSamples">Multisample anti-aliasing samples per pixel for the world pass: 1 (off) or 4.</param>
public sealed record EngineOptions(string Title, int Width, int Height, bool LogGpuErrors, int MsaaSamples = 1);
