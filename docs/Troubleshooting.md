# Klyro — Troubleshooting & Invigilator Triage Guide

This guide provides practical triage procedures for exam invigilators, proctors, and university IT helpdesk staff during live examinations using Klyro.

---

## 1. Quick Triage Summary

| Symptom | Root Cause | Immediate Action |
|---|---|---|
| **Yellow bar: "Network issue detected"** | Wi-Fi packet drop or transient jitter (< 3 probe failures). | Do nothing. Wait 15 seconds. If probes pass, notice disappears automatically. |
| **Modal overlay appears: "Campus Wi-Fi Login Required"** | Captive portal session timed out (`172.16.103.254:8090`). | Instruct student to enter their university credentials in the overlay and click Login. |
| **Overlay does not close after entering credentials** | Gateway has not completed internet route provisioning. | Wait 5 seconds; Klyro's `PortalAuthenticationMonitor` verifies internet HTTP 204 connectivity before dismissing modal. Do not refresh. |
| **Red bar: "Assessment service unreachable"** | DNS failure, campus firewall block, or CodeChef outage. | Verify Wi-Fi icon in notification bar. Check if invigilator laptop can reach `codechef.com`. |
| **Student accidentally closed the window / power glitch** | Machine crash or reboot. | Log back in to `KLYRO-EXAM`. Klyro auto-launches, checks session store (`session.json`), and resumes exam with accurate remaining time. |
| **Application won't open / Smart App Control blocked** | Binary not signed or blocked by Windows SAC. | Run using signed binary or execute via `& "dotnet.exe" Klyro.Admin.dll` for diagnostics. |

---

## 2. Captive Portal Recovery Behavior

Klyro's dual-WebView architecture protects student exam work during network disruptions:

```
[ Normal Exam State ]
  ExamWebView: ACTIVE (Student typing code in CodeChef)
  RecoveryOverlay: HIDDEN

       |
       | Wi-Fi captive portal session expires
       v

[ Captive Portal Triggered (3 failed 204 probes) ]
  ExamWebView: FROZEN / BLURRED (Preserves DOM, editor state, and unsubmitted code)
  ExamTimer: PAUSED (Configurable via PauseTimerOnDisconnection)
  RecoveryOverlay: DISPLAYED (Navigated to http://172.16.103.254:8090/)

       |
       | Student enters credentials & authenticates
       v

[ Verification Phase ]
  RecoveryController executes independent background 204 probe
  PortalAuthenticationMonitor confirms Internet connectivity

       |
       | Verified PASS
       v

[ Resumed Exam State ]
  RecoveryOverlay: HIDDEN & CLEARED
  ExamWebView: UNBLURRED (Focus restored, zero reload required)
  ExamTimer: RESUMED
```

> **IMPORTANT**: The primary assessment WebView (`ExamWebView`) is **NEVER closed or reloaded** during network failure or captive portal re-authentication. The student's unsaved code in the browser editor is completely safe.

---

## 3. Invigilator Keyboard Shortcuts & Emergency Access

To prevent student cheating, standard shortcuts (`Ctrl+Shift+I`, `F12`, `Ctrl+N`, `Ctrl+T`, `Ctrl+W`) are intercepted and suppressed.

For invigilators and technical proctors:

- **Diagnostics Drawer (Development Mode)**:
  `Alt + F12` toggles the Network Simulation & Diagnostics drawer.
  *Note: Only available when `"DevelopmentMode": true` in `config.json`.*

- **Invigilator Emergency Exit**:
  In case of workstation malfunction, the invigilator can log off the kiosk session:
  1. Press `Ctrl + Alt + Delete`.
  2. Select **Sign Out** or **Switch User**.
  3. Sign into the workstation using the administrative account (`IT-ADMIN`).
  4. Workstation files and crash logs remain preserved under `C:\ProgramData\Klyro\`.

---

## 4. Administrative Diagnostic Tool (`Klyro.Admin`)

The command-line administrative utility `Klyro.Admin.dll` is installed alongside the application in `C:\Program Files\Klyro\`.

### Available Commands:

1. **Run Full System Pre-flight Diagnostics**:
   ```powershell
   & "$env:ProgramFiles\dotnet\dotnet.exe" "C:\Program Files\Klyro\Klyro.Admin.dll" diagnose
   ```
   Checks Windows build, WebView2 runtime, active network adapters, configuration integrity, and assessment endpoints.

2. **Validate Configuration File**:
   ```powershell
   & "$env:ProgramFiles\dotnet\dotnet.exe" "C:\Program Files\Klyro\Klyro.Admin.dll" validate "C:\Program Files\Klyro\config\default.json"
   ```

3. **Export Diagnostic Telemetry Bundle**:
   ```powershell
   & "$env:ProgramFiles\dotnet\dotnet.exe" "C:\Program Files\Klyro\Klyro.Admin.dll" export --output "C:\Temp\klyro-diagnostic.json"
   ```

---

## 5. File & Log Locations

| Artifact | Location | Purpose |
|---|---|---|
| **Application Executable** | `C:\Program Files\Klyro\Klyro.App.exe` | Main application shell |
| **Global Configuration** | `C:\Program Files\Klyro\config\default.json` | Assessment endpoints & probe policies |
| **Audit Logs** | `C:\ProgramData\Klyro\audit.log` | Security audit trail (logins, blocks, state transitions) |
| **Session State** | `C:\ProgramData\Klyro\session.json` | Atomic session persistence for crash recovery |
| **WebView2 Exam Profile** | `%LOCALAPPDATA%\Klyro\WebView2_Exam` | Isolated InPrivate cache for assessment |
| **WebView2 Portal Profile** | `%LOCALAPPDATA%\Klyro\WebView2_Portal` | Ephemeral profile for captive portal login |

---

## 6. Audit Trail Interpretation

Audit records in `C:\ProgramData\Klyro\audit.log` are formatted as single-line JSON objects with ISO 8601 timestamps:

```json
{"Timestamp":"2026-09-22T08:14:02.124Z","Type":"NavigationBlocked","Message":"Blocked navigation to untrusted URI: https://google.com","TargetUri":"https://google.com","IsSecurityViolation":true}
{"Timestamp":"2026-09-22T08:15:30.410Z","Type":"NetworkStateChanged","Message":"Network changed from ONLINE to PORTAL_REQUIRED","IsSecurityViolation":false}
{"Timestamp":"2026-09-22T08:16:12.890Z","Type":"PortalAuthenticated","Message":"Captive portal re-authentication confirmed. Restoring assessment session.","IsSecurityViolation":false}
```

*Note: In compliance with university privacy regulations and security policies, student Wi-Fi passwords and CodeChef session cookies are **NEVER logged or saved**.*
