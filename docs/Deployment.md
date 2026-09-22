# Klyro — University Enterprise Deployment Guide

This guide details the deployment, configuration, and security management of **Klyro Secure Assessment Browser** on university lab workstations running Windows 10/11 Enterprise or Education.

---

## 1. System Requirements

| Component | Minimum Specification | Recommended |
|---|---|---|
| **Operating System** | Windows 10 / 11 Enterprise or Education (Build 19041+) | Windows 11 Enterprise (22H2+ / Build 22621+) |
| **Architecture** | x64 or ARM64 | x64 |
| **Runtime** | Microsoft Edge WebView2 Runtime (Evergreen) | WebView2 Evergreen (System-Wide) |
| **Framework** | .NET 8.0 Runtime (Windows Desktop) | Self-contained or .NET 8.0 Desktop Runtime |
| **Display** | Single 1080p Monitor (1920x1080) | Multi-monitor disabled in kiosk policy |
| **Network** | University Wi-Fi (WPA2/3 Enterprise or Captive Portal) or Ethernet | Dual DHCP with default gateway access |

---

## 2. Architecture of the Workstation Kiosk

Klyro relies on a **layered defense model**. The application code is not the sole security perimeter:

```
+-------------------------------------------------------------+
| Layer 1: Windows Assigned Access / Shell Launcher v2        |
| - Replaces explorer.exe with Klyro.App.exe                  |
| - Standard non-admin user account (KLYRO-EXAM)              |
| - Suppresses Task Manager, Ctrl+Alt+Del, Start Menu, Run    |
+-------------------------------------------------------------+
| Layer 2: Windows Defender Application Control (WDAC) / SAC  |
| - Whitelists Klyro binaries & WebView2 dependencies         |
| - Blocks unauthorized executables (cmd, PowerShell, etc.)   |
+-------------------------------------------------------------+
| Layer 3: Native WinUI 3 Application Shell                   |
| - Hardened Window controls (No minimize/close/restore buttons) |
| - Native exam countdown timer & atomic session persistence  |
+-------------------------------------------------------------+
| Layer 4: Hardened WebView2 Contexts                         |
| - Canonical URL Allowlisting & Protocol Guards              |
| - Isolated InPrivate Dual-Profiles (Exam vs Portal Recovery) |
+-------------------------------------------------------------+
```

---

## 3. Deployment Options

### Option A: Automated PowerShell Deployment (Local or Intune Script)

The repository provides automated provisioning scripts under `src/Klyro.Installer/Provisioning/`:

1. **Deploying Workstation**:
   Run from an elevated PowerShell administrator prompt:
   ```powershell
   cd c:\path\to\Klyro\src\Klyro.Installer\Provisioning
   .\Deploy-KlyroKiosk.ps1 -ExamUser "KLYRO-EXAM" -KlyroSourceDir "C:\Deploy\Klyro"
   ```

   This script automatically:
   - Creates the standard `KLYRO-EXAM` user account with a secure fixed password.
   - Verifies/installs the Microsoft Edge WebView2 Evergreen runtime.
   - Copies Klyro binaries into `C:\Program Files\Klyro`.
   - Enables Windows Embedded Shell Launcher (`Client-EmbeddedShellLauncher`).
   - Maps the `KLYRO-EXAM` user SID to `C:\Program Files\Klyro\Klyro.App.exe` with auto-restart action (`0`).
   - Disables Fast User Switching via registry policies.

2. **Rollback / Teardown Workstation**:
   To return the workstation to normal student lab usage:
   ```powershell
   .\Remove-KlyroKiosk.ps1 -ExamUser "KLYRO-EXAM" -DeleteUser $true -RemoveBinaries $true
   ```

---

### Option B: Microsoft Intune / Endpoint Manager

For centralized management across thousands of university campus computers:

1. **Package Klyro as Win32 App (`.intunewin`)**:
   - Package `C:\Program Files\Klyro` folder using the Microsoft Win32 Content Prep Tool.
   - Install command: `powershell.exe -ExecutionPolicy Bypass -File .\Deploy-KlyroKiosk.ps1`
   - Uninstall command: `powershell.exe -ExecutionPolicy Bypass -File .\Remove-KlyroKiosk.ps1 -DeleteUser $true -RemoveBinaries $true`
   - Detection rule: File exists `C:\Program Files\Klyro\Klyro.App.exe`.

2. **Assigned Access Configuration XML**:
   - In Microsoft Intune, navigate to **Devices** > **Configuration Profiles** > **Create Profile**.
   - Platform: **Windows 10 and later**.
   - Profile type: **Templates** > **Kiosk**.
   - Kiosk mode: **Multi-app kiosk** or **Custom XML**.
   - Upload `src/Klyro.Installer/Kiosk/AssignedAccessConfiguration.xml`.

---

## 4. Code Signing & Smart App Control (SAC)

On Windows 11 systems with **Smart App Control (SAC)** enabled in *Enforcement Mode*, executables and DLLs must have a valid digital signature from a trusted Certification Authority (CA) or Microsoft reputation.

### Signing University Builds:
1. Obtain a code-signing certificate (EV Code Signing or internal University Enterprise PKI Root CA).
2. Sign all output binaries before deployment:
   ```powershell
   signtool.exe sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 "C:\Program Files\Klyro\Klyro.App.exe"
   signtool.exe sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 "C:\Program Files\Klyro\Klyro.Core.dll"
   signtool.exe sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 "C:\Program Files\Klyro\Klyro.Admin.dll"
   ```
3. If using internal University PKI, deploy the Root CA certificate to the **Trusted Root Certification Authorities** store via Active Directory Group Policy.

---

## 5. Configuration Customization

Workstations read their exam profile from `C:\Program Files\Klyro\config\default.json` (or an environment override via `KLYRO_CONFIG_PATH`).

### Sample Production Configuration:
```json
{
  "Version": "1.0.0",
  "Assessment": {
    "ProviderName": "CodeChef",
    "StartUrl": "https://www.codechef.com/contests",
    "AllowedOrigins": [
      "https://www.codechef.com",
      "https://cdn.codechef.com",
      "https://img.codechef.com"
    ]
  },
  "Network": {
    "CaptivePortalUrl": "http://172.16.103.254:8090/",
    "AllowedPortalOrigins": [
      "http://172.16.103.254:8090"
    ],
    "InternetProbeUrls": [
      "https://www.gstatic.com/generate_204",
      "http://captive.apple.com/hotspot-detect.html"
    ],
    "ProbeIntervalSeconds": 5,
    "ProbeTimeoutSeconds": 3,
    "ConsecutiveFailureThreshold": 3
  },
  "ExamPolicy": {
    "DurationMinutes": 180,
    "PauseTimerOnDisconnection": true,
    "AutoSaveIntervalSeconds": 30
  }
}
```

---

## 6. Pre-Exam Verification Checklist

Before seating candidates:
- [ ] Log in as `KLYRO-EXAM` to verify Klyro auto-launches full-screen.
- [ ] Verify Pre-flight checks show all 5 green checkmarks (Windows OS, WebView2, Config, Network Adapter, Assessment Endpoint).
- [ ] Run diagnostic check via CLI:
  ```powershell
  & "C:\Program Files\dotnet\dotnet.exe" "C:\Program Files\Klyro\Klyro.Admin.dll" diagnose
  ```
- [ ] Test the network captive portal injection using the built-in simulator drawer (Alt+F12 in development mode).
