using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WoadRaiders.Server;

/// <summary>
/// Raises the OS timer resolution for the lifetime of the returned handle. On
/// Windows the default resolution (~15.6 ms) makes the game loop's Thread.Sleep(1)
/// wake ~14 ms late, so 30 Hz ticks arrive in bursts; 1 ms resolution pulls that
/// down to ~2 ms and the cadence smooths out. A no-op elsewhere — Linux already
/// schedules short sleeps at ~1 ms.
/// </summary>
internal static class FrameTimer
{
    public static IDisposable HighResolution() =>
        OperatingSystem.IsWindows() ? new WindowsTimerPeriod(1) : NoOp.Instance;

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsTimerPeriod : IDisposable
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

        private readonly uint _period;

        public WindowsTimerPeriod(uint period)
        {
            _period = period;
            timeBeginPeriod(period);
        }

        public void Dispose() => timeEndPeriod(_period);
    }
}
