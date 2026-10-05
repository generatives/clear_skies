using System.Globalization;

namespace ClearSkies.Game.Startup;

/// <summary>The command line, read once. Which game it starts: <see cref="JoinAddress"/> joins someone else's,
/// <see cref="HostPort"/> hosts one others can join, and neither plays alone.</summary>
public sealed record LaunchOptions
{
    /// <summary>--name: who the local player is (players are known by name). This machine's user name by default; a
    /// second instance on one machine with another name is another player.</summary>
    public string PlayerName { get; init; } = Environment.UserName;

    /// <summary>--join address[:port] (port 7777 by default).</summary>
    public string? JoinAddress { get; init; }

    /// <summary>--host port.</summary>
    public int? HostPort { get; init; }

    /// <summary>--world: the save, Saves/Worlds/&lt;name&gt;.db.</summary>
    public string WorldName { get; init; } = "Default";

    /// <summary>--seed: a new world's seed (after that, the save's).</summary>
    public ulong NewWorldSeed { get; init; } = 1337;

    /// <summary>--fps N: vsync off, at most N frames a second, so a window in the background doesn't slow down (for
    /// playing a host and a client side by side).</summary>
    public int? FrameRateCap { get; init; }

    /// <summary>--camera x,y,z[,yaw,pitch]: start the camera here instead of at the spawn, e.g. to reproduce a view for
    /// a screenshot.</summary>
    public float[]? Camera { get; init; }

    /// <summary>--light-budget-mb N: how much GPU light storage the loaded world may use (3 KB per 8³ brick of surface).
    /// Chunks load closest-first until it's spent; only surfaces use it, so solid stone inside an island is nearly free.
    /// Lower it for a software renderer whose small max buffer size can't hold it (the store also shrinks it to fit).</summary>
    public int LightBudgetMb { get; init; } = 512;

    /// <summary>--view-distance N: how far out (in blocks, horizontally) islands stream, if the budget reaches. The GPU's
    /// world index covers it both ways at 2 bytes per chunk position (~48 MB at 10000).
    /// 2000 by default; headless, 256 (only what colliders need).</summary>
    public float ViewDistance { get; init; } = 2000f;

    /// <summary>--checker-bounce N: each surface voxel fires only 1/N of the bounce and AO rays (2 or 4), neighbours firing
    /// the rest, to compare its cost and look against the default (1, all of them). Also in the GPU Lighting panel.</summary>
    public int CheckerBounce { get; init; } = 1;

    /// <summary>--headless: no window, GPU, input or UI; just the simulation and the network, on a timer. Hosting, it's a
    /// dedicated server (no player of its own); joining, a player that stands where it spawns (a bot, for testing).
    /// </summary>
    public bool Headless { get; init; }

    /// <summary>--flight-test: fly once the world has loaded, then quit.</summary>
    public bool FlightTest { get; init; }

    public static LaunchOptions Parse(string[] args)
    {
        string? Value(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool headless = args.Contains("--headless");
        var o = new LaunchOptions { Headless = headless, ViewDistance = headless ? 256f : 2000f };
        if (Value("--name") is { Length: > 0 } name) o = o with { PlayerName = name };
        if (Value("--join") is { Length: > 0 } join) o = o with { JoinAddress = join };
        if (Value("--host") is { } port) o = o with { HostPort = int.Parse(port, CultureInfo.InvariantCulture) };
        if (Value("--world") is { Length: > 0 } world) o = o with { WorldName = world };
        if (ulong.TryParse(Value("--seed"), out var seed)) o = o with { NewWorldSeed = seed };
        if (int.TryParse(Value("--fps"), out int fps) && fps > 0) o = o with { FrameRateCap = fps };
        if (Value("--camera") is { } camera)
            o = o with { Camera = camera.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() };
        if (Value("--light-budget-mb") is { } budget) o = o with { LightBudgetMb = int.Parse(budget, CultureInfo.InvariantCulture) };
        if (int.TryParse(Value("--checker-bounce"), out int checker)) o = o with { CheckerBounce = checker };
        if (Value("--view-distance") is { } view) o = o with { ViewDistance = float.Parse(view, CultureInfo.InvariantCulture) };
        return o with { FlightTest = args.Contains("--flight-test") };
    }
}
