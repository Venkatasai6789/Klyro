using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Klyro.Core.Security.RemoteAccess;

public record WindowInfo(IntPtr Handle, uint ProcessId, string ProcessName, string Title, string ClassName);

public interface IWindowQueryProvider
{
    IReadOnlyList<WindowInfo> GetTopLevelWindows();
}

public sealed class SystemWindowQueryProvider : IWindowQueryProvider
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public IReadOnlyList<WindowInfo> GetTopLevelWindows()
    {
        var windows = new List<WindowInfo>();
        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (IsWindowVisible(hWnd))
                    {
                        var titleBuilder = new StringBuilder(512);
                        GetWindowTextW(hWnd, titleBuilder, 512);
                        string title = titleBuilder.ToString();

                        var classBuilder = new StringBuilder(256);
                        GetClassNameW(hWnd, classBuilder, 256);
                        string className = classBuilder.ToString();

                        GetWindowThreadProcessId(hWnd, out uint pid);

                        string processName = "";
                        if (pid > 0)
                        {
                            try
                            {
                                using var proc = Process.GetProcessById((int)pid);
                                processName = proc.ProcessName;
                            }
                            catch { }
                        }

                        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(className))
                        {
                            windows.Add(new WindowInfo(hWnd, pid, processName, title, className));
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return windows;
    }
}
