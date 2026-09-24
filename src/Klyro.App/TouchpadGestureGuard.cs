using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Klyro_App;

/// <summary>
/// Intercepts and neutralizes Windows Precision Touchpad multi-finger gestures
/// (3-finger swipe up/down/left/right, 4-finger gestures, edge swipes, and multi-finger taps)
/// via Win32 SetGestureConfig, RegisterTouchpadCapableWindow, RegisterTouchWindow, Window Subclassing,
/// WinRT TouchpadGesturesController, and Registry policies.
/// </summary>
public sealed class TouchpadGestureGuard : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchpadCapableWindow(IntPtr hWnd, bool fEnable);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchpadCapableThread(bool fEnable);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchWindow(IntPtr hWnd, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTURECONFIG
    {
        public uint dwID;
        public uint dwWant;
        public uint dwBlock;
    }

    private const uint GC_ALLGESTURES = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetGestureConfig(
        IntPtr hWnd,
        uint dwReserved,
        uint cIDs,
        [In] GESTURECONFIG[] pGestureConfig,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string? lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    private const int HWND_BROADCAST = 0xffff;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    // Window Subclassing for WM_GESTURE & WM_GESTURENOTIFY suppression
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private const uint WM_GESTURE = 0x0119;
    private const uint WM_GESTURENOTIFY = 0x011A;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_TASKLIST = 0xF130;

    private static readonly UIntPtr SubclassId = new(9876);
    private readonly SubclassProc _subclassProc;
    private IntPtr _hookedHwnd;
    private bool _isSubclassed;

    private object? _controller;
    private readonly Dictionary<string, object?> _originalPtpSettings = new();
    private object? _originalEdgeUiUserSetting;
    private bool _isDisposed;

    public bool IsActive => _controller != null || _isSubclassed;

    public TouchpadGestureGuard()
    {
        _subclassProc = WndProcSubclass;
    }

    public void Enable(IntPtr hWnd = default)
    {
        try
        {
            // 1. Opt window and thread into Precision Touchpad capability and apply gesture configurations
            try
            {
                RegisterTouchpadCapableThread(true);
                if (hWnd != IntPtr.Zero)
                {
                    _hookedHwnd = hWnd;
                    RegisterTouchpadCapableWindow(hWnd, true);
                    RegisterTouchWindow(hWnd, 0);
                    ConfigureGestureSuppression(hWnd);

                    // Subclass window to swallow WM_GESTURE and gesture-induced WM_SYSCOMMAND
                    if (!_isSubclassed)
                    {
                        _isSubclassed = SetWindowSubclass(hWnd, _subclassProc, SubclassId, IntPtr.Zero);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Win32 window gesture setup warning: {ex.Message}");
            }

            // 2. Activate WinRT TouchpadGesturesController if available
            InitializeTouchpadGesturesController();

            // 3. Enforce registry policies to disable 3-finger, 4-finger, and edge swipes
            ApplyTouchpadRegistryPolicies();

            Log("TouchpadGestureGuard successfully enabled across WinRT controller, Win32 PTP capability, Window Subclassing, and Registry policies.");
        }
        catch (Exception ex)
        {
            Log($"Failed to fully enable TouchpadGestureGuard: {ex.Message}");
        }
    }

    private IntPtr WndProcSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        // 1. Swallow touch gestures (pinch-zoom, two-finger pan/rotate)
        if (uMsg == WM_GESTURE)
        {
            return IntPtr.Zero; // Handled, suppress gesture
        }

        // 2. Intercept gesture notification to re-enforce gesture blocking
        if (uMsg == WM_GESTURENOTIFY)
        {
            ConfigureGestureSuppression(hWnd);
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        // 3. Prevent minimize or tasklist triggered by trackpad gestures (e.g. 3-finger swipe down)
        if (uMsg == WM_SYSCOMMAND)
        {
            int cmd = (int)((long)wParam & 0xFFF0);
            if (cmd == SC_MINIMIZE || cmd == SC_TASKLIST)
            {
                return IntPtr.Zero; // Swallow minimize/tasklist attempt from gesture
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static void ConfigureGestureSuppression(IntPtr hWnd)
    {
        try
        {
            if (hWnd != IntPtr.Zero)
            {
                var configs = new GESTURECONFIG[]
                {
                    new()
                    {
                        dwID = 0, // Global configuration for all gestures
                        dwWant = 0,
                        dwBlock = GC_ALLGESTURES
                    }
                };

                SetGestureConfig(hWnd, 0, (uint)configs.Length, configs, (uint)Marshal.SizeOf<GESTURECONFIG>());
            }
        }
        catch { }
    }

    private void InitializeTouchpadGesturesController()
    {
        try
        {
            var controllerType = Type.GetType("Windows.UI.Input.TouchpadGesturesController, Windows, ContentType=WindowsRuntime")
                ?? Type.GetType("Windows.UI.Input.TouchpadGesturesController, Windows.Foundation, ContentType=WindowsRuntime");

            if (controllerType != null)
            {
                var isSupportedMethod = controllerType.GetMethod("IsSupported", BindingFlags.Public | BindingFlags.Static);
                bool isSupported = (bool)(isSupportedMethod?.Invoke(null, null) ?? false);

                if (isSupported)
                {
                    var createMethod = controllerType.GetMethod("CreateForProcess", BindingFlags.Public | BindingFlags.Static);
                    _controller = createMethod?.Invoke(null, null);

                    if (_controller != null)
                    {
                        var kindsType = Type.GetType("Windows.UI.Input.TouchpadGlobalGestureKinds, Windows, ContentType=WindowsRuntime")
                            ?? Type.GetType("Windows.UI.Input.TouchpadGlobalGestureKinds, Windows.Foundation, ContentType=WindowsRuntime");

                        if (kindsType != null)
                        {
                            // Combine 3-finger, 4-finger, and 5-finger manipulations (swipes) and actions (taps)
                            object allKinds = Enum.Parse(kindsType, "ThreeFingerManipulations, FourFingerManipulations, FiveFingerManipulations, ThreeFingerActions, FourFingerActions, FiveFingerActions");
                            controllerType.GetProperty("SupportedGestures")?.SetValue(_controller, allKinds);
                        }

                        // Enable controller: system will route all multi-finger swipes to this process instead of Windows Shell
                        controllerType.GetProperty("Enabled")?.SetValue(_controller, true);

                        Log("WinRT TouchpadGesturesController activated: 3-finger & 4-finger swipes routed to process and suppressed.");
                    }
                }
                else
                {
                    Log("WinRT TouchpadGesturesController is not supported on this device.");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WinRT TouchpadGesturesController activation exception: {ex.Message}");
        }
    }

    private void ApplyTouchpadRegistryPolicies()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // A. PrecisionTouchPad Settings (HKCU)
                string ptpKeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
                string[] ptpValues = {
                    "ThreeFingerSlideEnabled",
                    "FourFingerSlideEnabled",
                    "ThreeFingerTapEnabled",
                    "FourFingerTapEnabled",
                    "EnableEdgy",
                    "ThreeFingerSlideUp",
                    "ThreeFingerSlideDown",
                    "ThreeFingerSlideLeft",
                    "ThreeFingerSlideRight",
                    "FourFingerSlideUp",
                    "FourFingerSlideDown",
                    "FourFingerSlideLeft",
                    "FourFingerSlideRight",
                    "ThreeFingerSlides",
                    "FourFingerSlides",
                    "ThreeFingerTaps",
                    "FourFingerTaps",
                    "ZoomEnabled"
                };

                foreach (var valName in ptpValues)
                {
                    try
                    {
                        var orig = Registry.GetValue(ptpKeyPath, valName, null);
                        if (!_originalPtpSettings.ContainsKey(valName))
                        {
                            _originalPtpSettings[valName] = orig;
                        }
                        Registry.SetValue(ptpKeyPath, valName, 0, RegistryValueKind.DWord);
                    }
                    catch { }
                }

                // B. EdgeUI Policy (HKCU & HKLM) - Disables screen edge swipe
                string edgeUiSubPath = @"Software\Policies\Microsoft\Windows\EdgeUI";
                try
                {
                    _originalEdgeUiUserSetting = Registry.GetValue(@"HKEY_CURRENT_USER\" + edgeUiSubPath, "AllowEdgeSwipe", null);
                    using var edgeKey = Registry.CurrentUser.CreateSubKey(edgeUiSubPath);
                    edgeKey?.SetValue("AllowEdgeSwipe", 0, RegistryValueKind.DWord);
                }
                catch { }

                try
                {
                    using var lmKey = Registry.LocalMachine.CreateSubKey(edgeUiSubPath);
                    lmKey?.SetValue("AllowEdgeSwipe", 0, RegistryValueKind.DWord);
                }
                catch { }

                // C. Broadcast WM_SETTINGCHANGE so system and input components reload settings immediately
                BroadcastSettingsChange("PrecisionTouchPad");
                BroadcastSettingsChange("Policy");
            }
        }
        catch (Exception ex)
        {
            Log($"ApplyTouchpadRegistryPolicies warning: {ex.Message}");
        }
    }

    private static void BroadcastSettingsChange(string? section)
    {
        try
        {
            SendMessageTimeout(
                (IntPtr)HWND_BROADCAST,
                WM_SETTINGCHANGE,
                UIntPtr.Zero,
                section,
                SMTO_ABORTIFHUNG,
                150,
                out _);
        }
        catch { }
    }

    public void RestoreTouchpadRegistryPolicies()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string ptpKeyPath = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
                using (var ptpKey = Registry.CurrentUser.OpenSubKey(ptpKeyPath, writable: true))
                {
                    if (ptpKey != null)
                    {
                        foreach (var kvp in _originalPtpSettings)
                        {
                            try
                            {
                                if (kvp.Value == null)
                                {
                                    ptpKey.DeleteValue(kvp.Key, throwOnMissingValue: false);
                                }
                                else
                                {
                                    ptpKey.SetValue(kvp.Key, kvp.Value, RegistryValueKind.DWord);
                                }
                            }
                            catch { }
                        }
                    }
                }
                _originalPtpSettings.Clear();

                // Restore EdgeUI policy
                string edgeUiSubPath = @"Software\Policies\Microsoft\Windows\EdgeUI";
                using (var edgeKey = Registry.CurrentUser.OpenSubKey(edgeUiSubPath, writable: true))
                {
                    if (edgeKey != null)
                    {
                        try
                        {
                            if (_originalEdgeUiUserSetting == null)
                            {
                                edgeKey.DeleteValue("AllowEdgeSwipe", throwOnMissingValue: false);
                            }
                            else
                            {
                                edgeKey.SetValue("AllowEdgeSwipe", _originalEdgeUiUserSetting, RegistryValueKind.DWord);
                            }
                        }
                        catch { }
                    }
                }

                // Broadcast restored settings
                BroadcastSettingsChange("PrecisionTouchPad");
                BroadcastSettingsChange("Policy");
            }
        }
        catch (Exception ex)
        {
            Log($"RestoreTouchpadRegistryPolicies warning: {ex.Message}");
        }
    }

    public void Disable()
    {
        try
        {
            if (_isSubclassed && _hookedHwnd != IntPtr.Zero)
            {
                RemoveWindowSubclass(_hookedHwnd, _subclassProc, SubclassId);
                _isSubclassed = false;
            }

            if (_controller != null)
            {
                var controllerType = _controller.GetType();
                controllerType.GetProperty("Enabled")?.SetValue(_controller, false);
                _controller = null;
            }

            RestoreTouchpadRegistryPolicies();
            Log("TouchpadGestureGuard successfully disabled and settings restored.");
        }
        catch { }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            Disable();
            _isDisposed = true;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "boot.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [TouchpadGestureGuard] {message}\n");
        }
        catch { }
    }
}
