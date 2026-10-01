using System.Runtime.InteropServices;

namespace ClearSkies.Engine.Core;

/// <summary>
/// Windows 11 runs processes whose window isn't in front in "efficiency mode" (EcoQoS): slower cores, coarser timers.
/// Two instances played side by side (a host and a client) would then have whichever isn't focused drop frames.
/// <see cref="OptOut"/> asks Windows not to; elsewhere it does nothing.
/// </summary>
public static class PowerThrottling
{
    private const int ProcessPowerThrottling = 4;              // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint ExecutionSpeed = 0x1;                   // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const uint IgnoreTimerResolution = 0x4;            // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION

    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    /// <summary>Asks for 1 ms timer resolution on Windows (the default is ~15.6 ms), so a frame cap's sleeps land on
    /// time. Elsewhere it already is.</summary>
    public static void FineTimer()
    {
        if (OperatingSystem.IsWindows()) timeBeginPeriod(1);
    }

    public static void OptOut()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) return;
        // Control the flags, with them clear: never throttle execution speed, always honour timer resolution.
        var state = new State { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<State>()))
            Console.WriteLine($"[engine] couldn't opt out of power throttling (error {Marshal.GetLastWin32Error()})");
    }
}
