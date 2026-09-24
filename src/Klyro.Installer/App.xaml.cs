using System;
using System.Linq;
using System.Windows;
using Klyro.Installer.Models;
using Klyro.Installer.Services;

namespace Klyro.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        bool isSilent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("/silent", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            var options = new InstallOptions
            {
                TermsAccepted = true,
                IsSilent = true,
                ShortcutChoice = ShortcutPlacement.DesktopAndStartMenu
            };

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                {
                    options.InstallDirectory = args[i].Substring(6).Trim('"', '\'');
                }
                else if (args[i].Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase))
                {
                    options.ShortcutChoice = ShortcutPlacement.None;
                }
            }

            var engine = new InstallerEngine();
            bool ok = await engine.InstallAsync(options);
            Shutdown(ok ? 0 : 1);
            return;
        }

        // GUI mode: display MainWindow
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
