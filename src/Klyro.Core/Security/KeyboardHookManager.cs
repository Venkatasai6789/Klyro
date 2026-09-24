using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace Klyro.Core.Security;

/// <summary>
/// Manages a low-level Windows keyboard hook (WH_KEYBOARD_LL) to suppress
/// operating system escapes (Windows Key / Win+G, Alt+Tab, Alt+Esc, Ctrl+Esc)
/// and enforces GameDVR suppression during examination sessions.
/// </summary>
public sealed class KeyboardHookManager : IDisposable
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const uint VK_TAB = 0x09;
    public const uint VK_ESCAPE = 0x1B;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12; // Alt
    public const uint VK_D = 0x44;
    public const uint VK_G = 0x47;
    public const uint VK_R = 0x52;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;
    public const uint VK_F4 = 0x73;
    public const uint VK_F11 = 0x7A;
    public const uint VK_F12 = 0x7B;
    public const uint VK_APPS = 0x5D;
    public const uint VK_F21 = 0x84;
    public const uint VK_F22 = 0x85;
    public const uint VK_F23 = 0x86;
    public const uint VK_F24 = 0x87;
    public const uint VK_BROWSER_BACK = 0xA6;
    public const uint VK_BROWSER_FORWARD = 0xA7;
    public const uint VK_BROWSER_REFRESH = 0xA8;
    public const uint VK_BROWSER_STOP = 0xA9;
    public const uint VK_BROWSER_SEARCH = 0xAA;
    public const uint VK_BROWSER_FAVORITES = 0xAB;
    public const uint VK_BROWSER_HOME = 0xAC;

    public const uint LLKHF_ALTDOWN = 0x20;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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

    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    private static KeyboardHookManager? _instance;

    private static volatile bool _isLWinDown;
    private static volatile bool _isRWinDown;

    private static CancellationTokenSource? _processKillerCts;
    private static Thread? _processKillerThread;

    public bool IsHookInstalled => _hookId != IntPtr.Zero;

    public event EventHandler? ExitRequested;

    public KeyboardHookManager()
    {
        _instance = this;
    }

    public void InstallHook()
    {
        if (_hookId != IntPtr.Zero) return;

        try
        {
            // 1. Enforce GameDVR suppression in registry, protocol handlers, and terminate active Game Bar processes
            ApplyGameDVRRestrictions();
            ApplyTouchpadRestrictions();
            TerminateGameBarProcesses();
            StartGameBarProcessKiller();

            // 2. Install low-level keyboard hook
            _proc = HookCallback;
            IntPtr hModule = IntPtr.Zero;
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule != null)
                {
                    hModule = GetModuleHandle(curModule.ModuleName);
                }
            }
            catch { }

            if (hModule == IntPtr.Zero)
            {
                hModule = GetModuleHandle(null);
            }

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hModule, 0);
            if (_hookId == IntPtr.Zero)
            {
                // Fallback to IntPtr.Zero if module handle failed
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            }

            try
            {
                var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "boot.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [KeyboardHookManager] Hook installed. HookId={_hookId}, LastError={Marshal.GetLastWin32Error()}\n");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "boot.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [KeyboardHookManager FATAL] {ex}\n");
            }
            catch { }
        }
    }

    public void UninstallHook()
    {
        try
        {
            StopGameBarProcessKiller();

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _proc = null;
            }

            _isLWinDown = false;
            _isRWinDown = false;

            RestoreGameDVRRestrictions();
            RestoreTouchpadRestrictions();
        }
        catch { }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();

            // Track Win key transitions directly from messages
            if (kbd.vkCode == VK_LWIN)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    _isLWinDown = true;
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    _isLWinDown = false;
            }
            else if (kbd.vkCode == VK_RWIN)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    _isRWinDown = true;
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    _isRWinDown = false;
            }

            bool isWinDown = _isLWinDown || _isRWinDown
                || ((GetAsyncKeyState((int)VK_LWIN) & 0x8000) != 0)
                || ((GetAsyncKeyState((int)VK_RWIN) & 0x8000) != 0);

            bool isAltDown = (kbd.flags & LLKHF_ALTDOWN) != 0 || ((GetAsyncKeyState((int)VK_MENU) & 0x8000) != 0);
            bool isCtrlDown = ((GetKeyState((int)VK_CONTROL) & 0x8000) != 0) || ((GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0);

            // Special fast-path for 'G' key when Win is down: aggressively terminate any Game Bar process and swallow
            if (kbd.vkCode == VK_G && isWinDown)
            {
                TerminateGameBarProcesses();
                return (IntPtr)1;
            }

            if (ShouldBlockKeystroke(msg, kbd.vkCode, kbd.flags, isCtrlDown, isAltDown, isWinDown, out bool isExitRequested))
            {
                if (isExitRequested)
                {
                    _instance?.ExitRequested?.Invoke(_instance, EventArgs.Empty);
                }
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public static bool ShouldBlockKeystroke(
        int wParam,
        uint vkCode,
        uint flags,
        bool isCtrlDown,
        bool isAltDown,
        bool isWinDown,
        out bool isExitRequested)
    {
        isExitRequested = false;

        // 1. Block Windows Keys unconditionally (blocks Win key press & release: VK_LWIN and VK_RWIN)
        if (vkCode == VK_LWIN || vkCode == VK_RWIN)
        {
            return true;
        }

        // 2. Explicitly block Windows+G (Xbox Game Bar), Win+Alt+G, Win+Alt+R, and any other Win+key combination
        if (isWinDown)
        {
            return true;
        }

        // 3. Block Alt+Tab and Alt+Esc (blocks task switching)
        if (isAltDown && (vkCode == VK_TAB || vkCode == VK_ESCAPE))
        {
            return true;
        }

        // 4. Intercept Alt+F4 to prompt for exit PIN rather than terminating abruptly
        if (isAltDown && vkCode == VK_F4)
        {
            if (wParam == WM_SYSKEYDOWN)
            {
                isExitRequested = true;
            }
            return true;
        }

        // 5. Block Ctrl+Esc (blocks Start Menu)
        if (isCtrlDown && vkCode == VK_ESCAPE)
        {
            return true;
        }

        // 6. Block F11 (full screen toggle attempt) and F12 (DevTools)
        if (vkCode == VK_F11 || vkCode == VK_F12)
        {
            return true;
        }

        // 7. Block synthetic / hardware keys triggered by trackpad gestures (F21-F24, Browser navigation keys, Context Apps key)
        if (vkCode is VK_APPS or VK_F21 or VK_F22 or VK_F23 or VK_F24 or
            VK_BROWSER_BACK or VK_BROWSER_FORWARD or VK_BROWSER_REFRESH or
            VK_BROWSER_STOP or VK_BROWSER_SEARCH or VK_BROWSER_FAVORITES or VK_BROWSER_HOME)
        {
            return true;
        }

        return false;
    }

    public static void ApplyGameDVRRestrictions()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Disable Game Bar recording & overlay shortcuts in GameDVR
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AllowHotkeys", 0, RegistryValueKind.DWord);

                // Disable GameDVR in GameConfigStore
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_EFSEFeatureFlags", 0, RegistryValueKind.DWord);

                // Disable GameBar auto mode and nexus shortcut
                try
                {
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode", 0, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", 0, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "ShowStartupPanel", 0, RegistryValueKind.DWord);
                }
                catch { }

                // Disable GameDVR in Windows Policies if permissions permit (HKCU)
                try
                {
                    Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
                }
                catch { }

                // Disable GameDVR in Machine Policies if elevated (HKLM)
                try
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
                }
                catch { }

                // Hijack ms-gamebar, ms-gamingoverlay, ms-gamebarservices protocols in HKCU to harmless rundll32.exe
                try
                {
                    string[] protocols = { "ms-gamebar", "ms-gamingoverlay", "ms-gamebarservices" };
                    foreach (var proto in protocols)
                    {
                        using var protoKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{proto}");
                        protoKey?.SetValue("", $"URL:{proto}", RegistryValueKind.String);
                        protoKey?.SetValue("URL Protocol", "", RegistryValueKind.String);
                        protoKey?.SetValue("NoOpenWith", "", RegistryValueKind.String);

                        using var cmdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{proto}\shell\open\command");
                        cmdKey?.SetValue("", "rundll32.exe", RegistryValueKind.String);
                    }
                }
                catch { }

                // Explorer DisabledHotkeys (disable Win+G in Explorer shell)
                try
                {
                    var currentDisabled = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisabledHotkeys", "") as string;
                    if (string.IsNullOrEmpty(currentDisabled))
                    {
                        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisabledHotkeys", "G", RegistryValueKind.String);
                    }
                    else if (!currentDisabled.Contains('G', StringComparison.OrdinalIgnoreCase))
                    {
                        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisabledHotkeys", currentDisabled + "G", RegistryValueKind.String);
                    }
                }
                catch { }

                // Disable Windows keys hotkeys in Policies\Explorer
                try
                {
                    Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoWinKeys", 1, RegistryValueKind.DWord);
                }
                catch { }
            }
        }
        catch
        {
            // Non-fatal if permissions are restricted
        }
    }

    public static void RestoreGameDVRRestrictions()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Remove protocol redirections
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ms-gamebar", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ms-gamingoverlay", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ms-gamebarservices", throwOnMissingSubKey: false);
                }
                catch { }

                // Remove NoWinKeys policy on teardown
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", writable: true);
                    key?.DeleteValue("NoWinKeys", throwOnMissingValue: false);
                }
                catch { }

                // Remove G from DisabledHotkeys
                try
                {
                    var currentDisabled = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisabledHotkeys", "") as string;
                    if (!string.IsNullOrEmpty(currentDisabled) && currentDisabled.Contains('G', StringComparison.OrdinalIgnoreCase))
                    {
                        var cleaned = currentDisabled.Replace("G", "", StringComparison.OrdinalIgnoreCase).Replace("g", "");
                        if (string.IsNullOrEmpty(cleaned))
                        {
                            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: true);
                            key?.DeleteValue("DisabledHotkeys", throwOnMissingValue: false);
                        }
                        else
                        {
                            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisabledHotkeys", cleaned, RegistryValueKind.String);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public static void TerminateGameBarProcesses()
    {
        try
        {
            string[] gameBarProcs = { "GameBar", "GameBarFT", "GameBarFtServer", "GameBarPresenceWriter", "XboxGamingOverlay", "XboxGamingOverlayHost", "bchat" };
            foreach (var procName in gameBarProcs)
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
        }
        catch { }
    }

    private static void StartGameBarProcessKiller()
    {
        if (_processKillerThread != null) return;

        _processKillerCts = new CancellationTokenSource();
        _processKillerThread = new Thread(() =>
        {
            while (!_processKillerCts.IsCancellationRequested)
            {
                try
                {
                    TerminateGameBarProcesses();
                    Thread.Sleep(500);
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Klyro_GameBar_Killer",
            Priority = ThreadPriority.Lowest
        };
        _processKillerThread.Start();
    }

    private static void StopGameBarProcessKiller()
    {
        try
        {
            _processKillerCts?.Cancel();
            _processKillerThread = null;
        }
        catch { }
    }

    private static readonly System.Collections.Generic.Dictionary<string, object?> _originalPtpSettings = new();
    private static object? _originalEdgeUiUserSetting;

    public static void ApplyTouchpadRestrictions()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // A. Suppress multi-finger swipe & tap gestures in Precision Touchpad settings
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

                // B. Disable screen & trackpad edge swipes (Action Center, Notifications, Widgets)
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
        catch { }
    }

    public static void RestoreTouchpadRestrictions()
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
        catch { }
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

    public void Dispose()
    {
        UninstallHook();
    }
}
