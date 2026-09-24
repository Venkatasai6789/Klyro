using System;
using System.IO;

namespace Klyro.Installer.Models;

public enum ShortcutPlacement
{
    DesktopAndStartMenu,
    DesktopOnly,
    StartMenuOnly,
    None
}

public enum InstallationScope
{
    AllUsers,
    CurrentUser
}

public class InstallOptions
{
    public bool TermsAccepted { get; set; } = false;
    public InstallationScope Scope { get; set; } = InstallationScope.AllUsers;
    public string InstallDirectory { get; set; } = @"C:\Klyro";
    public ShortcutPlacement ShortcutChoice { get; set; } = ShortcutPlacement.DesktopAndStartMenu;
    public bool UnblockFiles { get; set; } = true;
    public bool GrantUserPermissions { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
    public bool IsSilent { get; set; } = false;
    public bool IsAdministrator { get; set; } = false;

    public static string GetDefaultDirectory(InstallationScope scope)
    {
        return scope switch
        {
            InstallationScope.AllUsers => @"C:\Klyro",
            InstallationScope.CurrentUser => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Klyro"),
            _ => @"C:\Klyro"
        };
    }
}
