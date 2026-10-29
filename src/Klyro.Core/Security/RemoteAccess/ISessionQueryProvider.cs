using System;
using System.Runtime.InteropServices;

namespace Klyro.Core.Security.RemoteAccess;

public sealed class WtsSessionInfo
{
    public bool QuerySucceeded { get; init; }
    public string? WinStationName { get; init; }
    public string? ClientName { get; init; }
    public ushort? ProtocolType { get; init; }
    public int? ConnectState { get; init; }
    public string? SessionNameEnv { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Indicates whether the active session is connected to the local physical console.
    /// In Windows, a session running at the physical monitor has WinStationName == "Console" or %SESSIONNAME% == "Console".
    /// </summary>
    public bool IsConsoleStation =>
        string.Equals(WinStationName, "Console", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SessionNameEnv, "Console", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Indicates whether the remote terminal session is in a disconnected state (client dropped connection).
    /// </summary>
    public bool IsDisconnected =>
        ConnectState.HasValue && ConnectState.Value == (int)NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected;

    /// <summary>
    /// Indicates whether the session is currently active and interactive.
    /// </summary>
    public bool IsActive =>
        ConnectState.HasValue && ConnectState.Value == (int)NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive;

    /// <summary>
    /// Indicates whether the session is being remotely shadowed or controlled via Terminal Services.
    /// </summary>
    public bool IsShadowed =>
        ConnectState.HasValue && ConnectState.Value == (int)NativeMethods.WTS_CONNECTSTATE_CLASS.WTSShadow;
}

public interface ISessionQueryProvider
{
    bool IsRemoteSessionMetricSet();
    bool IsRemoteControlMetricSet();
    WtsSessionInfo GetCurrentWtsSessionInfo();
}

public sealed class WindowsSessionQueryProvider : ISessionQueryProvider
{
    public bool IsRemoteSessionMetricSet()
    {
        try
        {
            return NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRemoteControlMetricSet()
    {
        try
        {
            return NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTECONTROL) != 0;
        }
        catch
        {
            return false;
        }
    }

    public WtsSessionInfo GetCurrentWtsSessionInfo()
    {
        try
        {
            string? winStation = QueryString(NativeMethods.WTS_INFO_CLASS.WTSWinStationName);
            string? clientName = QueryString(NativeMethods.WTS_INFO_CLASS.WTSClientName);
            ushort? protocol = QueryUShort(NativeMethods.WTS_INFO_CLASS.WTSClientProtocolType);
            int? connectState = QueryInt(NativeMethods.WTS_INFO_CLASS.WTSConnectState);
            string? sessionNameEnv = null;
            try
            {
                sessionNameEnv = Environment.GetEnvironmentVariable("SESSIONNAME");
            }
            catch { }

            return new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = winStation,
                ClientName = clientName,
                ProtocolType = protocol,
                ConnectState = connectState,
                SessionNameEnv = sessionNameEnv
            };
        }
        catch (Exception ex)
        {
            return new WtsSessionInfo
            {
                QuerySucceeded = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static string? QueryString(NativeMethods.WTS_INFO_CLASS infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NativeMethods.WTSQuerySessionInformationW(
                    NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                    NativeMethods.WTS_CURRENT_SESSION,
                    infoClass,
                    out buffer,
                    out _))
            {
                return Marshal.PtrToStringUni(buffer);
            }
        }
        catch { }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
        return null;
    }

    private static ushort? QueryUShort(NativeMethods.WTS_INFO_CLASS infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NativeMethods.WTSQuerySessionInformationW(
                    NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                    NativeMethods.WTS_CURRENT_SESSION,
                    infoClass,
                    out buffer,
                    out int bytes) && bytes >= 2)
            {
                return (ushort)Marshal.ReadInt16(buffer);
            }
        }
        catch { }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
        return null;
    }

    private static int? QueryInt(NativeMethods.WTS_INFO_CLASS infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NativeMethods.WTSQuerySessionInformationW(
                    NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                    NativeMethods.WTS_CURRENT_SESSION,
                    infoClass,
                    out buffer,
                    out int bytes) && bytes >= 4)
            {
                return Marshal.ReadInt32(buffer);
            }
        }
        catch { }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
        return null;
    }
}
