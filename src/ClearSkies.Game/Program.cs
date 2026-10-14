using ClearSkies.Engine.Core;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Startup;

// Headless perf harnesses (see GenerationBenchmark): no GPU or window needed.
if (args.Contains("--benchmark-stream"))
{
    StreamingBenchmark.Run();
    return;
}
if (args.Contains("--benchmark"))
{
    GenerationBenchmark.Run();
    return;
}

// Background (non-blocking) full collections only while the game runs: a blocking one stops every thread for tens of
// milliseconds, a visible hitch. Needs concurrent GC, which is on by default.
System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

// --backend vulkan|dx12|metal|gl: the graphics API, instead of wgpu's choice (a window in the background can fare
// differently under each).
{
    int b = Array.IndexOf(args, "--backend");
    if (b >= 0 && b + 1 < args.Length)
        ClearSkies.Engine.Rendering.WebGpu.GpuContext.Backend = args[b + 1].ToLowerInvariant() switch
        {
            "vulkan" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.Vulkan,
            "dx12" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.DX12,
            "metal" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.Metal,
            "gl" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.GL,
            var other => throw new ArgumentException($"Unknown --backend {other}: vulkan, dx12, metal or gl."),
        };
}

// --gpu fast|low|software: the fastest GPU (default), the power-saving one (usually integrated), or the CPU software
// renderer, to try the game on a weaker machine.
{
    int g = Array.IndexOf(args, "--gpu");
    if (g >= 0 && g + 1 < args.Length)
        ClearSkies.Engine.Rendering.WebGpu.GpuContext.Adapter = args[g + 1].ToLowerInvariant() switch
        {
            "fast" => ClearSkies.Engine.Rendering.WebGpu.GpuAdapterChoice.Fast,
            "low" => ClearSkies.Engine.Rendering.WebGpu.GpuAdapterChoice.LowPower,
            "software" => ClearSkies.Engine.Rendering.WebGpu.GpuAdapterChoice.Software,
            var other => throw new ArgumentException($"Unknown --gpu {other}: fast, low or software."),
        };
}

var options = LaunchOptions.Parse(args);
if (options.Headless)
{
    // No window, GPU, input or UI: just the simulation and the network, on a timer.
    using var headless = new EngineHost();
    if (options.JoinAddress is not null) BotClientGame.Run(headless, options);
    else if (options.HostPort is not null) DedicatedServerGame.Run(headless, options);
    else Console.Error.WriteLine("--headless needs --host <port> (a dedicated server) or --join <address> (a bot).");
    return;
}

using var host = new WindowedEngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true, MsaaSamples: options.Msaa));
if (options.JoinAddress is not null) ClientGame.Run(host, options);
else if (options.HostPort is not null) HostGame.Run(host, options);
else SinglePlayerGame.Run(host, options);
