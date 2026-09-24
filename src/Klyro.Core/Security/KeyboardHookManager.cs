using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    private static KeyboardHookManager? _instance;

    private static volatile bool _isLWinDown;
    private static volatile bool _isRWinDown;

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
            // 1. Enforce GameDVR suppression in registry and terminate active Game Bar processes
            ApplyGameDVRRestrictions();
            TerminateGameBarProcesses();

            // 2. Install low-level keyboard hook
            _proc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule?.ModuleName), 0);
        }
        catch
        {
            // Fail safely without crashing the host process if permissions or platform restricts hooks
        }
    }

    public void UninstallHook()
    {
        try
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _proc = null;
            }

            _isLWinDown = false;
            _isRWinDown = false;

            RestoreGameDVRRestrictions();
        }
        catch { }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();

            // Track Win key transitions
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

            // Sync with physical state to ensure flags do not remain stuck if an up event occurred outside the hook
            bool isPhysicalWinDown = (GetAsyncKeyState((int)VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState((int)VK_RWIN) & 0x8000) != 0;
            if (!isPhysicalWinDown)
            {
                _isLWinDown = false;
                _isRWinDown = false;
            }

            bool isWinDown = _isLWinDown || _isRWinDown || isPhysicalWinDown;
            bool isAltDown = (kbd.flags & LLKHF_ALTDOWN) != 0 || (GetAsyncKeyState((int)VK_MENU) & 0x8000) != 0;
            bool isCtrlDown = (GetKeyState((int)VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0;

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
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "ShowStartupPanel", 0, RegistryValueKind.DWord);

                // Disable GameDVR in Windows Policies (HKCU)
                Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);

                // Disable GameDVR in Machine Policies if elevated (HKLM)
                try
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
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
            string[] gameBarProcs = { "GameBar", "GameBarFT", "GameBarPresenceWriter", "XboxGamingOverlay", "XboxGamingOverlayHost" };
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

    public void Dispose()
    {
        UninstallHook();
    }
}
