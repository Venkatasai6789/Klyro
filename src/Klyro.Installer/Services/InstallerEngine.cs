using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Installer.Models;

namespace Klyro.Installer.Services;

public class InstallerEngine
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string name);

    public event Action<int, string>? ProgressChanged;

    /// <summary>
    /// Checks whether the current process is running with elevated Administrator privileges.
    /// </summary>
    public static bool CheckIsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to restart the installer with elevated Administrator privileges using the Windows UAC prompt.
    /// </summary>
    public static bool TryElevate(string? arguments = null)
    {
        try
        {
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
            {
                return false;
            }

            var psi = new ProcessStartInfo(currentExe)
            {
                Verb = "runas",
                UseShellExecute = true,
                Arguments = arguments ?? string.Empty
            };

            Process.Start(psi);
            return true;
        }
        catch
        {
            // User declined UAC prompt or elevation failed
            return false;
        }
    }

    public async Task<bool> InstallAsync(InstallOptions options, CancellationToken ct = default)
    {
        try
        {
            bool isAdmin = CheckIsAdministrator();
            options.IsAdministrator = isAdmin;

            // 0. Verify destination write permissions and administrator requirements
            Report(5, "Verifying installation permissions...");
            string targetDir = options.InstallDirectory;

            bool canWrite = await Task.Run(() => TestDirectoryWriteAccess(targetDir), ct);
            if (!canWrite)
            {
                if (!isAdmin)
                {
                    Report(-1, $"Administrative privileges required to install to '{targetDir}'. Please run Setup as Administrator or choose 'Install for Current User'.");
                    return false;
                }
                else
                {
                    Report(-1, $"Access to directory '{targetDir}' is restricted. Please select a valid target folder.");
                    return false;
                }
            }

            // 1. Stop any running Klyro instances
            Report(15, "Stopping existing Klyro processes...");
            await Task.Run(() => StopKlyroProcesses(), ct);
            await Task.Delay(300, ct);

            // 2. Prepare destination directory
            Report(30, $"Preparing destination folder {targetDir}...");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 3. Deploy binaries (first try embedded or co-located payload, then local source directory)
            Report(45, "Deploying application binaries and components...");
            bool payloadExtracted = await Task.Run(() => TryExtractPayload(targetDir), ct);

            if (!payloadExtracted)
            {
                Report(50, "Locating application binaries and packages...");
                string sourceDir = await Task.Run(() => ResolveSourceBinariesDirectory(), ct);
                Report(60, "Deploying application binaries and assets...");
                await Task.Run(() => CopyDirectory(sourceDir, targetDir), ct);
            }

            // Copy config directory if available in repo
            string configSource = FindConfigSource();
            if (!string.IsNullOrEmpty(configSource) && Directory.Exists(configSource))
            {
                string targetConfig = Path.Combine(targetDir, "config");
                Directory.CreateDirectory(targetConfig);
                CopyDirectory(configSource, targetConfig);
            }
            await Task.Delay(300, ct);

            // 5. Unblock files and configure ACL permissions
            Report(75, "Applying security permissions and unblocking files...");
            await Task.Run(() =>
            {
                if (options.UnblockFiles)
                {
                    UnblockDirectoryFiles(targetDir);
                }

                if (options.GrantUserPermissions)
                {
                    if (isAdmin)
                    {
                        GrantUsersAccess(targetDir);
                    }
                    else
                    {
                        // Standard user mode: inherited user profile permissions are already active
                    }
                }
            }, ct);

            // 6. Create shortcuts based on radial button choice
            Report(90, "Configuring Windows shortcuts...");
            await Task.Run(() => ConfigureShortcuts(options), ct);
            await Task.Delay(300, ct);

            // 7. Refresh Windows Explorer shell
            try
            {
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }

            Report(100, "Installation completed successfully!");
            return true;
        }
        catch (Exception ex)
        {
            Report(-1, $"Installation failed: {ex.Message}");
            return false;
        }
    }

    private void Report(int percent, string message)
    {
        ProgressChanged?.Invoke(percent, message);
    }

    private static bool TestDirectoryWriteAccess(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string testFile = Path.Combine(directory, $".klyro_write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StopKlyroProcesses()
    {
        var targetNames = new[] { "Klyro.App", "msedgewebview2" };
        foreach (var name in targetNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private bool TryExtractPayload(string targetDir)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string[] resourceNames = assembly.GetManifestResourceNames();
            string? payloadResource = resourceNames.FirstOrDefault(r => r.EndsWith("app.payload.zip", StringComparison.OrdinalIgnoreCase));

            Stream? stream = null;
            if (!string.IsNullOrEmpty(payloadResource))
            {
                stream = assembly.GetManifestResourceStream(payloadResource);
            }

            if (stream == null)
            {
                string localZip = Path.Combine(AppContext.BaseDirectory, "app.payload.zip");
                if (File.Exists(localZip))
                {
                    stream = File.OpenRead(localZip);
                }
            }

            if (stream == null) return false;

            using (stream)
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                int total = archive.Entries.Count;
                int count = 0;

                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(Path.Combine(targetDir, entry.FullName));
                        continue;
                    }

                    string destPath = Path.Combine(targetDir, entry.FullName);
                    string? parentDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    entry.ExtractToFile(destPath, overwrite: true);
                    count++;

                    if (count % 20 == 0 || count == total)
                    {
                        int pct = 45 + (int)((count / (double)total) * 28);
                        Report(pct, $"Extracting {entry.Name} ({count}/{total})...");
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveSourceBinariesDirectory()
    {
        string baseDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDir, "publish"),
            Path.Combine(baseDir, @"..\..\..\..\artifacts\publish"),
            Path.Combine(baseDir, @"..\..\..\..\..\artifacts\publish"),
            Path.Combine(baseDir, @"..\..\artifacts\publish"),
            Path.Combine(baseDir, @"artifacts\publish"),
            Path.Combine(baseDir, @"..\..\..\src\Klyro.App\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64"),
            Path.Combine(baseDir, @"..\..\..\src\Klyro.App\bin\Debug\net8.0-windows10.0.26100.0\win-x64"),
            Path.Combine(baseDir, @"..\..\..\src\Klyro.App\bin\x86\Debug\net8.0-windows10.0.26100.0\win-x86"),
            @"C:\Klyro"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (Directory.Exists(full) && File.Exists(Path.Combine(full, "Klyro.App.exe")))
                {
                    return full;
                }
            }
            catch { }
        }

        string? repoRoot = FindRepoRoot(baseDir);
        if (repoRoot != null)
        {
            string projPath = Path.Combine(repoRoot, "src", "Klyro.App", "Klyro.App.csproj");
            string outputPublish = Path.Combine(repoRoot, "artifacts", "publish");

            if (File.Exists(projPath))
            {
                try
                {
                    Directory.CreateDirectory(outputPublish);
                    var psi = new ProcessStartInfo("dotnet", $"publish \"{projPath}\" -c Debug -r win-x64 --self-contained true -o \"{outputPublish}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(90000);

                    if (File.Exists(Path.Combine(outputPublish, "Klyro.App.exe")))
                    {
                        return outputPublish;
                    }
                }
                catch { }
            }
        }

        if (Directory.Exists(@"C:\Klyro") && File.Exists(@"C:\Klyro\Klyro.App.exe"))
        {
            return @"C:\Klyro";
        }

        throw new FileNotFoundException("Could not find compiled Klyro application binaries (Klyro.App.exe). Please build the solution first or ensure artifacts/publish exists.");
    }

    private static string? FindRepoRoot(string startDir)
    {
        var cur = new DirectoryInfo(startDir);
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur.FullName, "Klyro.sln")))
            {
                return cur.FullName;
            }
            cur = cur.Parent;
        }
        return null;
    }

    private static string FindConfigSource()
    {
        string baseDir = AppContext.BaseDirectory;
        string? repo = FindRepoRoot(baseDir);
        if (repo != null)
        {
            string cfg = Path.Combine(repo, "config");
            if (Directory.Exists(cfg)) return cfg;
        }
        return string.Empty;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        if (string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\'), Path.GetFullPath(targetDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(targetDir, relative);
            try
            {
                File.Copy(file, dest, true);
            }
            catch { }
        }
    }

    private static void UnblockDirectoryFiles(string directory)
    {
        try
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    string ads = $"{file}:Zone.Identifier";
                    DeleteFile(ads);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void GrantUsersAccess(string directory)
    {
        try
        {
            var dirInfo = new DirectoryInfo(directory);
            var sec = dirInfo.GetAccessControl();
            var usersGroup = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            sec.AddAccessRule(new FileSystemAccessRule(usersGroup, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            dirInfo.SetAccessControl(sec);
        }
        catch { }
    }

    private static void ConfigureShortcuts(InstallOptions options)
    {
        string exePath = Path.Combine(options.InstallDirectory, "Klyro.App.exe");
        string iconPath = Path.Combine(options.InstallDirectory, "Assets", "AppIcon.ico");

        string desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string desktopLnk = Path.Combine(desktopFolder, "Klyro Secure Assessment Browser.lnk");

        string startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        string startLnk = Path.Combine(startMenuFolder, "Klyro Secure Assessment Browser.lnk");

        bool wantDesktop = options.ShortcutChoice == ShortcutPlacement.DesktopAndStartMenu || options.ShortcutChoice == ShortcutPlacement.DesktopOnly;
        bool wantStartMenu = options.ShortcutChoice == ShortcutPlacement.DesktopAndStartMenu || options.ShortcutChoice == ShortcutPlacement.StartMenuOnly;

        if (wantDesktop)
        {
            CreateShortcut(desktopLnk, exePath, options.InstallDirectory, iconPath, "Klyro - Secure Assessment Browser");
        }
        else
        {
            try { if (File.Exists(desktopLnk)) File.Delete(desktopLnk); } catch { }
        }

        if (wantStartMenu)
        {
            CreateShortcut(startLnk, exePath, options.InstallDirectory, iconPath, "Klyro - Secure Assessment Browser");
        }
        else
        {
            try { if (File.Exists(startLnk)) File.Delete(startLnk); } catch { }
        }
    }

    private static void CreateShortcut(string lnkPath, string targetPath, string workingDir, string iconPath, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = description;
            if (File.Exists(iconPath))
            {
                shortcut.IconLocation = $"{iconPath},0";
            }
            shortcut.Save();
        }
        catch { }
    }
}
