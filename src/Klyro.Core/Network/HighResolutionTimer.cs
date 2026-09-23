using System;
using System.Runtime.InteropServices;

namespace Klyro.Core.Network;

/// <summary>
/// Sets Windows OS timer interrupt resolution to 1 millisecond using winmm.dll timeBeginPeriod.
/// This enables true 1ms precision for Thread.Sleep(1) and WaitableTimer synchronization
/// without spinning the CPU in busy-wait loops.
/// </summary>
public sealed class HighResolutionTimer : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private readonly uint _periodMs;
    private bool _isActive;
    private bool _isDisposed;

    public bool IsActive => _isActive;
    public uint PeriodMs => _periodMs;

    public HighResolutionTimer(uint periodMs = 1)
    {
        _periodMs = periodMs;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                uint result = TimeBeginPeriod(_periodMs);
                _isActive = (result == 0); // TIMERR_NOERROR = 0
            }
        }
        catch
        {
            _isActive = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_isActive && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                TimeEndPeriod(_periodMs);
            }
            catch { }
            _isActive = false;
        }
    }
}
