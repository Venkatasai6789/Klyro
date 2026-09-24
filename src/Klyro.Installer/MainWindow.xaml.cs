using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Klyro.Installer.Models;
using Klyro.Installer.Services;

namespace Klyro.Installer;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private readonly InstallOptions _options = new();
    private readonly InstallerEngine _engine = new();

    private readonly SolidColorBrush _brushAccent = new(Color.FromRgb(0x25, 0x63, 0xEB));
    private readonly SolidColorBrush _brushMuted = new(Color.FromRgb(0x1E, 0x29, 0x3B));
    private readonly SolidColorBrush _brushSuccess = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private readonly SolidColorBrush _brushTextWhite = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _brushTextSecondary = new(Color.FromRgb(0x94, 0xA3, 0xB8));

    public MainWindow()
    {
        InitializeComponent();

        txtTermsContent.Text = TermsAndConditionsText.FullText;

        // Detect Administrator elevation status
        bool isAdmin = InstallerEngine.CheckIsAdministrator();
        _options.IsAdministrator = isAdmin;

        if (isAdmin)
        {
            bannerAdminActive.Visibility = Visibility.Visible;
            bannerAdminRequired.Visibility = Visibility.Collapsed;
            rbScopeAllUsers.IsChecked = true;
            _options.Scope = InstallationScope.AllUsers;
            txtInstallDir.Text = InstallOptions.GetDefaultDirectory(InstallationScope.AllUsers);
        }
        else
        {
            bannerAdminActive.Visibility = Visibility.Collapsed;
            bannerAdminRequired.Visibility = Visibility.Visible;
            // Default to current user for non-elevated sessions to prevent permission failure, but keep all-users option visible
            rbScopeCurrentUser.IsChecked = true;
            _options.Scope = InstallationScope.CurrentUser;
            txtInstallDir.Text = InstallOptions.GetDefaultDirectory(InstallationScope.CurrentUser);
        }

        UpdateDriveSpace();

        _engine.ProgressChanged += (percent, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (percent >= 0)
                {
                    installProgressBar.Value = percent;
                    txtPercent.Text = $"{percent}%";
                    txtCurrentStatus.Text = msg;
                    txtLiveLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
                    scrollLog.ScrollToEnd();
                }
                else
                {
                    txtCurrentStatus.Text = msg;
                    txtLiveLog.Text += $"[{DateTime.Now:HH:mm:ss}] [ERROR] {msg}\n";
                    scrollLog.ScrollToEnd();
                }
            });
        };

        UpdateStepUI();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 3 && panelInstallProgress.Visibility == Visibility.Visible && installProgressBar.Value < 100)
        {
            var res = MessageBox.Show(this,
                "Installation is currently in progress. Aborting now may leave incomplete files.\n\nAre you sure you want to cancel?",
                "Cancel Installation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;
        }
        else if (_currentStep < 4)
        {
            var res = MessageBox.Show(this,
                "Setup is not complete. If you exit now, Klyro will not be installed.\n\nDo you want to exit Setup?",
                "Exit Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;
        }

        Application.Current.Shutdown(0);
    }

    private void RbTerms_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (btnNext == null) return;
        btnNext.IsEnabled = (rbAcceptTerms.IsChecked == true);
    }

    private void BtnElevate_Click(object sender, RoutedEventArgs e)
    {
        if (InstallerEngine.TryElevate())
        {
            Application.Current.Shutdown(0);
        }
    }

    private void RbScope_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (txtInstallDir == null) return;

        if (rbScopeAllUsers?.IsChecked == true)
        {
            _options.Scope = InstallationScope.AllUsers;
            txtInstallDir.Text = InstallOptions.GetDefaultDirectory(InstallationScope.AllUsers);
        }
        else if (rbScopeCurrentUser?.IsChecked == true)
        {
            _options.Scope = InstallationScope.CurrentUser;
            txtInstallDir.Text = InstallOptions.GetDefaultDirectory(InstallationScope.CurrentUser);
        }

        UpdateDriveSpace();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Klyro Installation Folder",
                InitialDirectory = Directory.Exists(txtInstallDir.Text) ? txtInstallDir.Text : @"C:\"
            };

            if (dialog.ShowDialog(this) == true)
            {
                txtInstallDir.Text = dialog.FolderName;
                UpdateDriveSpace();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open folder picker: {ex.Message}", "Browse Folder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateDriveSpace()
    {
        try
        {
            string root = Path.GetPathRoot(txtInstallDir.Text) ?? @"C:\";
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                txtDriveSpace.Text = $"Available: {freeGb:F1} GB on Drive {root.TrimEnd('\\')}";
            }
        }
        catch
        {
            txtDriveSpace.Text = "Available: > 50 GB";
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            _options.TermsAccepted = (rbAcceptTerms.IsChecked == true);
            if (!_options.TermsAccepted) return;

            _currentStep = 2;
            UpdateStepUI();
        }
        else if (_currentStep == 2)
        {
            _options.Scope = (rbScopeAllUsers.IsChecked == true) ? InstallationScope.AllUsers : InstallationScope.CurrentUser;
            _options.InstallDirectory = string.IsNullOrWhiteSpace(txtInstallDir.Text) 
                ? InstallOptions.GetDefaultDirectory(_options.Scope) 
                : txtInstallDir.Text.Trim();

            // Shortcut radial buttons selection
            if (rbShortcutBoth.IsChecked == true)
            {
                _options.ShortcutChoice = ShortcutPlacement.DesktopAndStartMenu;
            }
            else if (rbShortcutDesktop.IsChecked == true)
            {
                _options.ShortcutChoice = ShortcutPlacement.DesktopOnly;
            }
            else if (rbShortcutStartMenu.IsChecked == true)
            {
                _options.ShortcutChoice = ShortcutPlacement.StartMenuOnly;
            }
            else
            {
                _options.ShortcutChoice = ShortcutPlacement.None;
            }

            _options.UnblockFiles = (chkUnblockFiles.IsChecked == true);
            _options.GrantUserPermissions = (chkGrantPermissions.IsChecked == true);

            // Populate summary on Step 3
            lblSummaryScope.Text = _options.Scope == InstallationScope.AllUsers ? "System-Wide (All Users)" : "Current User Only";
            lblSummaryAdmin.Text = _options.IsAdministrator ? "Verified (Elevated)" : "Standard User (User Level)";
            lblSummaryAdmin.Foreground = _options.IsAdministrator ? _brushSuccess : (SolidColorBrush)FindResource("AccentGlowBrush");
            lblSummaryTarget.Text = _options.InstallDirectory;
            lblSummaryShortcuts.Text = _options.ShortcutChoice switch
            {
                ShortcutPlacement.DesktopAndStartMenu => "Desktop and Start Menu",
                ShortcutPlacement.DesktopOnly => "Desktop Only",
                ShortcutPlacement.StartMenuOnly => "Start Menu Only",
                _ => "None (No shortcuts)"
            };

            _currentStep = 3;
            UpdateStepUI();
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 2)
        {
            _currentStep = 1;
            UpdateStepUI();
        }
        else if (_currentStep == 3)
        {
            _currentStep = 2;
            UpdateStepUI();
        }
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        // If system-wide installation to C:\Klyro is selected without elevation, give user chance to elevate
        if (_options.Scope == InstallationScope.AllUsers && !_options.IsAdministrator)
        {
            var res = MessageBox.Show(this,
                "Installing system-wide to C:\\Klyro requires Administrator privileges.\n\n" +
                "Would you like to elevate and restart Setup as Administrator now?\n\n" +
                "(Click 'No' to switch to Current User mode in your AppData directory)",
                "Administrator Privileges Required",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                if (InstallerEngine.TryElevate())
                {
                    Application.Current.Shutdown(0);
                    return;
                }
            }
            else if (res == MessageBoxResult.No)
            {
                rbScopeCurrentUser.IsChecked = true;
                _options.Scope = InstallationScope.CurrentUser;
                _options.InstallDirectory = InstallOptions.GetDefaultDirectory(InstallationScope.CurrentUser);
                txtInstallDir.Text = _options.InstallDirectory;
                lblSummaryScope.Text = "Current User Only";
                lblSummaryTarget.Text = _options.InstallDirectory;
            }
            else
            {
                return;
            }
        }

        // Switch to progress view
        panelPreInstallSummary.Visibility = Visibility.Collapsed;
        panelInstallProgress.Visibility = Visibility.Visible;

        txtScreen3Title.Text = "Installing Klyro...";
        txtScreen3Subtitle.Text = "Please wait while Klyro Secure Assessment Browser is deployed.";

        btnBack.IsEnabled = false;
        btnInstall.IsEnabled = false;
        btnCancel.IsEnabled = false;

        bool success = await _engine.InstallAsync(_options);

        if (success)
        {
            _currentStep = 4;
            lblFinishedScope.Text = _options.Scope == InstallationScope.AllUsers ? "System-Wide (All Users)" : "Current User Only";
            lblFinishedExePath.Text = Path.Combine(_options.InstallDirectory, "Klyro.App.exe");
            lblFinishedShortcuts.Text = _options.ShortcutChoice switch
            {
                ShortcutPlacement.DesktopAndStartMenu => "Desktop and Start Menu",
                ShortcutPlacement.DesktopOnly => "Desktop Icon",
                ShortcutPlacement.StartMenuOnly => "Start Menu Program",
                _ => "None"
            };

            UpdateStepUI();
        }
        else
        {
            MessageBox.Show(this,
                "Installation encountered an issue. Please review the live activity log.",
                "Installation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            btnCancel.IsEnabled = true;
            btnBack.IsEnabled = true;
        }
    }

    private void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        if (chkLaunchApp.IsChecked == true)
        {
            string exe = Path.Combine(_options.InstallDirectory, "Klyro.App.exe");
            if (File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        WorkingDirectory = _options.InstallDirectory,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        Application.Current.Shutdown(0);
    }

    private void UpdateStepUI()
    {
        // Visibility of Screens
        gridScreen1.Visibility = (_currentStep == 1) ? Visibility.Visible : Visibility.Collapsed;
        gridScreen2.Visibility = (_currentStep == 2) ? Visibility.Visible : Visibility.Collapsed;
        gridScreen3.Visibility = (_currentStep == 3) ? Visibility.Visible : Visibility.Collapsed;
        gridScreen4.Visibility = (_currentStep == 4) ? Visibility.Visible : Visibility.Collapsed;

        // Navigation Buttons
        btnBack.Visibility = (_currentStep == 2 || _currentStep == 3) ? Visibility.Visible : Visibility.Collapsed;
        btnNext.Visibility = (_currentStep == 1 || _currentStep == 2) ? Visibility.Visible : Visibility.Collapsed;
        btnInstall.Visibility = (_currentStep == 3 && panelPreInstallSummary.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        btnFinish.Visibility = (_currentStep == 4) ? Visibility.Visible : Visibility.Collapsed;
        btnCancel.Visibility = (_currentStep == 4) ? Visibility.Collapsed : Visibility.Visible;

        if (_currentStep == 1)
        {
            btnNext.IsEnabled = (rbAcceptTerms.IsChecked == true);
        }
        else if (_currentStep == 2)
        {
            btnNext.IsEnabled = true;
        }

        // Sidebar Navigation Indicators
        UpdateSidebarRail();
    }

    private void UpdateSidebarRail()
    {
        SetStepIndicator(1, stepBadge1, stepBadgeText1, stepTitle1, _currentStep);
        SetStepIndicator(2, stepBadge2, stepBadgeText2, stepTitle2, _currentStep);
        SetStepIndicator(3, stepBadge3, stepBadgeText3, stepTitle3, _currentStep);
        SetStepIndicator(4, stepBadge4, stepBadgeText4, stepTitle4, _currentStep);
    }

    private void SetStepIndicator(int stepNum, System.Windows.Controls.Border badge, System.Windows.Controls.TextBlock badgeText, System.Windows.Controls.TextBlock title, int currentStep)
    {
        if (currentStep > stepNum)
        {
            // Completed step
            badge.Background = _brushSuccess;
            badgeText.Text = "✓";
            badgeText.Foreground = _brushTextWhite;
            title.Foreground = _brushTextWhite;
            title.FontWeight = FontWeights.SemiBold;
        }
        else if (currentStep == stepNum)
        {
            // Active step
            badge.Background = _brushAccent;
            badgeText.Text = stepNum.ToString();
            badgeText.Foreground = _brushTextWhite;
            title.Foreground = _brushTextWhite;
            title.FontWeight = FontWeights.Bold;
        }
        else
        {
            // Pending step
            badge.Background = _brushMuted;
            badgeText.Text = stepNum.ToString();
            badgeText.Foreground = _brushTextSecondary;
            title.Foreground = _brushTextSecondary;
            title.FontWeight = FontWeights.Normal;
        }
    }
}
