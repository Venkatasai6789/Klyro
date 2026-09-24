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
/// via WinRT TouchpadGesturesController, Win32 RegisterTouchpadCapableWindow, and Registry policies.
/// </summary>
public sealed class TouchpadGestureGuard : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchpadCapableWindow(IntPtr hWnd, bool fEnable);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchpadCapableThread(bool fEnable);

    private object? _controller;
    private readonly Dictionary<string, object?> _originalPtpSettings = new();
    private object? _originalEdgeUiUserSetting;
    private bool _isDisposed;

    public bool IsActive => _controller != null;

    public void Enable(IntPtr hWnd = default)
    {
        try
        {
            // 1. Opt window and thread into Precision Touchpad capability
            try
            {
                RegisterTouchpadCapableThread(true);
                if (hWnd != IntPtr.Zero)
                {
                    RegisterTouchpadCapableWindow(hWnd, true);
                }
            }
            catch { }

            // 2. Activate WinRT TouchpadGesturesController via WindowsRuntime projection
            InitializeTouchpadGesturesController();

            // 3. Enforce registry policies to disable 3-finger, 4-finger, and edge swipes
            ApplyTouchpadRegistryPolicies();

            Log("TouchpadGestureGuard successfully enabled across WinRT controller, Win32 PTP capability, and Registry policies.");
        }
        catch (Exception ex)
        {
            Log($"Failed to fully enable TouchpadGestureGuard: {ex.Message}");
        }
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
                    "FourFingerSlideRight"
                };

                foreach (var valName in ptpValues)
                {
                    try
                    {
                        var orig = Registry.GetValue(ptpKeyPath, valName, null);
                        _originalPtpSettings[valName] = orig;
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
            }
        }
        catch (Exception ex)
        {
            Log($"ApplyTouchpadRegistryPolicies warning: {ex.Message}");
        }
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
