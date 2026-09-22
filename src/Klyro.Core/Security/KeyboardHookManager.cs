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
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint VK_TAB = 0x09;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_F4 = 0x73;
    private const uint VK_F11 = 0x7A;
    private const uint VK_F12 = 0x7B;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_CONTROL = 0x11;

    private const uint LLKHF_ALTDOWN = 0x20;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
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

    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    private static KeyboardHookManager? _instance;

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
            // 1. Enforce GameDVR suppression in current user registry
            ApplyGameDVRRestrictions();

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
        }
        catch { }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isAltDown = (kbd.flags & LLKHF_ALTDOWN) != 0;
            bool isCtrlDown = (GetKeyState((int)VK_CONTROL) & 0x8000) != 0;

            // Block Windows Keys unconditionally (blocks Win+G, Win+D, Win+R, Win+Tab, etc.)
            if (kbd.vkCode == VK_LWIN || kbd.vkCode == VK_RWIN)
            {
                return (IntPtr)1;
            }

            // Block Alt+Tab and Alt+Esc (blocks task switching)
            if (isAltDown && (kbd.vkCode == VK_TAB || kbd.vkCode == VK_ESCAPE))
            {
                return (IntPtr)1;
            }

            // Intercept Alt+F4 to prompt for exit PIN rather than terminating abruptly
            if (isAltDown && kbd.vkCode == VK_F4)
            {
                if (wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    _instance?.ExitRequested?.Invoke(_instance, EventArgs.Empty);
                }
                return (IntPtr)1;
            }

            // Block Ctrl+Esc (blocks Start Menu)
            if (isCtrlDown && kbd.vkCode == VK_ESCAPE)
            {
                return (IntPtr)1;
            }

            // Block F11 (full screen toggle attempt) and F12 (DevTools)
            if (kbd.vkCode == VK_F11 || kbd.vkCode == VK_F12)
            {
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void ApplyGameDVRRestrictions()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Disable Game Bar recording & overlay shortcuts
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // Non-fatal if permissions are restricted
        }
    }

    public void Dispose()
    {
        UninstallHook();
    }
}
