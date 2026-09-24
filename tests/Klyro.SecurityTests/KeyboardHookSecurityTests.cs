using Klyro.Core.Security;
using Xunit;

namespace Klyro.SecurityTests;

public class KeyboardHookSecurityTests
{
    private const int WM_KEYDOWN = KeyboardHookManager.WM_KEYDOWN;
    private const int WM_KEYUP = KeyboardHookManager.WM_KEYUP;
    private const int WM_SYSKEYDOWN = KeyboardHookManager.WM_SYSKEYDOWN;

    private const uint VK_TAB = KeyboardHookManager.VK_TAB;
    private const uint VK_ESCAPE = KeyboardHookManager.VK_ESCAPE;
    private const uint VK_G = KeyboardHookManager.VK_G;
    private const uint VK_R = KeyboardHookManager.VK_R;
    private const uint VK_D = KeyboardHookManager.VK_D;
    private const uint VK_LWIN = KeyboardHookManager.VK_LWIN;
    private const uint VK_RWIN = KeyboardHookManager.VK_RWIN;
    private const uint VK_F4 = KeyboardHookManager.VK_F4;
    private const uint VK_F11 = KeyboardHookManager.VK_F11;
    private const uint VK_F12 = KeyboardHookManager.VK_F12;
    private const uint LLKHF_ALTDOWN = KeyboardHookManager.LLKHF_ALTDOWN;

    [Theory]
    [InlineData(WM_KEYDOWN)]
    [InlineData(WM_KEYUP)]
    public void ShouldBlockKeystroke_BlocksWindowsKeysAlone(int msg)
    {
        // Left Windows Key
        bool blockedLWin = KeyboardHookManager.ShouldBlockKeystroke(
            msg, VK_LWIN, 0, isCtrlDown: false, isAltDown: false, isWinDown: false, out var isExit1);
        Assert.True(blockedLWin, "Left Windows key must be blocked unconditionally.");
        Assert.False(isExit1);

        // Right Windows Key
        bool blockedRWin = KeyboardHookManager.ShouldBlockKeystroke(
            msg, VK_RWIN, 0, isCtrlDown: false, isAltDown: false, isWinDown: false, out var isExit2);
        Assert.True(blockedRWin, "Right Windows key must be blocked unconditionally.");
        Assert.False(isExit2);
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksWinG_GameBarShortcut()
    {
        // When Windows key is held down and 'G' is pressed (Win+G)
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_KEYDOWN, VK_G, 0, isCtrlDown: false, isAltDown: false, isWinDown: true, out var isExit);

        Assert.True(blocked, "Win+G shortcut must be intercepted and blocked.");
        Assert.False(isExit);
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksWinAltG_GameBarRecordShortcut()
    {
        // When Windows+Alt are held and 'G' is pressed (Win+Alt+G)
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_SYSKEYDOWN, VK_G, LLKHF_ALTDOWN, isCtrlDown: false, isAltDown: true, isWinDown: true, out var isExit);

        Assert.True(blocked, "Win+Alt+G Game Bar record shortcut must be blocked.");
        Assert.False(isExit);
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksWinAltR_ScreenRecordShortcut()
    {
        // When Windows+Alt are held and 'R' is pressed (Win+Alt+R)
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_SYSKEYDOWN, VK_R, LLKHF_ALTDOWN, isCtrlDown: false, isAltDown: true, isWinDown: true, out var isExit);

        Assert.True(blocked, "Win+Alt+R Game Bar record toggle shortcut must be blocked.");
        Assert.False(isExit);
    }

    [Theory]
    [InlineData(VK_G)] // Win+G (Game Bar)
    [InlineData(VK_D)] // Win+D (Show Desktop)
    [InlineData(VK_R)] // Win+R (Run dialog)
    [InlineData(VK_TAB)] // Win+Tab (Task View)
    [InlineData(0x45)] // Win+E (File Explorer)
    [InlineData(0x4C)] // Win+L (Lock)
    public void ShouldBlockKeystroke_BlocksAllKeystrokes_WhenWindowsKeyIsDown(uint vkCode)
    {
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_KEYDOWN, vkCode, 0, isCtrlDown: false, isAltDown: false, isWinDown: true, out var isExit);

        Assert.True(blocked, $"Key code 0x{vkCode:X2} with Windows key modifier must be blocked.");
        Assert.False(isExit);
    }

    [Theory]
    [InlineData(VK_G)] // 'G' key
    [InlineData(VK_R)] // 'R' key
    [InlineData(VK_D)] // 'D' key
    [InlineData(0x41)] // 'A' key
    [InlineData(0x30)] // '0' key
    public void ShouldBlockKeystroke_AllowsNormalTyping_WhenWindowsKeyIsNotDown(uint vkCode)
    {
        // Normal typing without Win, Alt, or Ctrl modifiers
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_KEYDOWN, vkCode, 0, isCtrlDown: false, isAltDown: false, isWinDown: false, out var isExit);

        Assert.False(blocked, $"Normal typing key code 0x{vkCode:X2} must not be blocked.");
        Assert.False(isExit);
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksAltTabAndAltEsc()
    {
        bool blockedAltTab = KeyboardHookManager.ShouldBlockKeystroke(
            WM_SYSKEYDOWN, VK_TAB, LLKHF_ALTDOWN, isCtrlDown: false, isAltDown: true, isWinDown: false, out var isExit1);
        Assert.True(blockedAltTab, "Alt+Tab must be blocked.");
        Assert.False(isExit1);

        bool blockedAltEsc = KeyboardHookManager.ShouldBlockKeystroke(
            WM_SYSKEYDOWN, VK_ESCAPE, LLKHF_ALTDOWN, isCtrlDown: false, isAltDown: true, isWinDown: false, out var isExit2);
        Assert.True(blockedAltEsc, "Alt+Esc must be blocked.");
        Assert.False(isExit2);
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksAltF4_AndSignalsExitRequested()
    {
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_SYSKEYDOWN, VK_F4, LLKHF_ALTDOWN, isCtrlDown: false, isAltDown: true, isWinDown: false, out var isExit);

        Assert.True(blocked, "Alt+F4 must be intercepted.");
        Assert.True(isExit, "Alt+F4 must request authorization exit prompt.");
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksCtrlEsc()
    {
        bool blocked = KeyboardHookManager.ShouldBlockKeystroke(
            WM_KEYDOWN, VK_ESCAPE, 0, isCtrlDown: true, isAltDown: false, isWinDown: false, out var isExit);

        Assert.True(blocked, "Ctrl+Esc must be blocked.");
        Assert.False(isExit);
    }

    [Fact]
    public void ShouldBlockKeystroke_BlocksF11AndF12()
    {
        bool blockedF11 = KeyboardHookManager.ShouldBlockKeystroke(
            WM_KEYDOWN, VK_F11, 0, isCtrlDown: false, isAltDown: false, isWinDown: false, out var isExit1);
        Assert.True(blockedF11, "F11 must be blocked.");
        Assert.False(isExit1);

        bool blockedF12 = KeyboardHookManager.ShouldBlockKeystroke(
            WM_KEYDOWN, VK_F12, 0, isCtrlDown: false, isAltDown: false, isWinDown: false, out var isExit2);
        Assert.True(blockedF12, "F12 must be blocked.");
        Assert.False(isExit2);
    }

    [Fact]
    public void SecurityWatchdog_IncludesXboxGameBarProcessesInProhibitedList()
    {
        using var watchdog = new SecurityWatchdog();
        var prohibited = watchdog.ProhibitedProcesses;

        Assert.Contains("gamebar", prohibited);
        Assert.Contains("gamebarft", prohibited);
        Assert.Contains("gamebarpresencewriter", prohibited);
        Assert.Contains("xboxgamingoverlay", prohibited);
        Assert.Contains("xboxgamingoverlayhost", prohibited);
    }

    [Fact]
    public void ApplyAndRestoreGameDVRRestrictions_ConfiguresAndCleansUpProtocolRedirections()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return;

        // Apply
        KeyboardHookManager.ApplyGameDVRRestrictions();

        using (var cmdKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\ms-gamebar\shell\open\command"))
        {
            Assert.NotNull(cmdKey);
            var val = cmdKey.GetValue("") as string;
            Assert.Equal("rundll32.exe", val);
        }

        using (var overlayCmdKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\ms-gamingoverlay\shell\open\command"))
        {
            Assert.NotNull(overlayCmdKey);
            var val = overlayCmdKey.GetValue("") as string;
            Assert.Equal("rundll32.exe", val);
        }

        // Clean up
        KeyboardHookManager.RestoreGameDVRRestrictions();

        using (var cleanKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\ms-gamebar"))
        {
            Assert.Null(cleanKey);
        }
    }
}
