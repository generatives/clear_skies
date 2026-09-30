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

var options = LaunchOptions.Parse(args);
using var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));
if (options.JoinAddress is not null) ClientGame.Run(host, options);
else if (options.HostPort is not null) HostGame.Run(host, options);
else SinglePlayerGame.Run(host, options);
