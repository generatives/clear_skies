namespace ClearSkies.Engine.Core;

/// <summary>Configuration for an <see cref="EngineHost"/>. <paramref name="Headless"/>: no window, GPU, input or debug
/// UI; only the update stages run, on a plain timer (a dedicated server, or a bot).</summary>
public sealed record EngineOptions(string Title, int Width, int Height, bool LogGpuErrors, bool Headless = false);
