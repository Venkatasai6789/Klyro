# Klyro Secure Assessment Browser — Master Technical Documentation & Architecture Reference

**Version:** 1.0.0 LTS  
**Target Platform:** Windows 10 / Windows 11 (x64) Enterprise / Education  
**Runtime:** .NET 8.0 LTS (WinUI 3 / Windows App SDK) & Microsoft Edge WebView2 (Evergreen)  
**Security Standard:** University Kiosk / Defense-in-Depth Academic Integrity Architecture  

---

## 1. Executive Summary & System Philosophy

### 1.1. Problem Statement
High-stakes university programming examinations (hosted on platforms such as CodeChef, HackerRank, or institutional LMS environments) face two distinct and often conflicting challenges:
1. **Academic Integrity Vulnerabilities:** Standard commercial web browsers allow candidate workstations to leak examination contents, spawn unmonitored external tabs, execute developer tools (`F12`), access AI assistance, bypass browser locks via operating system chords (`Win+G`, `Alt+Tab`, `Win+D`), or launch unauthorized local software.
2. **Network Volatility & Captive Portal Session Expirations:** University campus Wi-Fi infrastructure (such as `KLU_WIFI` or departmental laboratory access points) frequently forces DHCP lease renewals, client re-associations, or captive portal session timeouts (e.g., `http://172.16.103.254:8090/`). Standard browsers respond to captive portal drops by abruptly navigating the active tab to the login screen, destroying unsaved student code, clearing contest editor state, and terminating session cookies mid-exam.

### 1.2. The Klyro Solution
**Klyro** is a purpose-built, locked-down examination browser engineered specifically to resolve both problems concurrently through an uncompromising defense-in-depth architecture:
- **Security Violations Fail Closed:** Any navigation outside canonical origin boundaries, unauthorized protocol invocations (`file:`, `cmd:`, `powershell:`, `mailto:`), file download attempts, popup creations (`window.open`), or inspection requests are intercepted at the native API level and canceled before reaching the network or OS runtime.
- **Network Failures Fail Gracefully:** A network drop, radio jitter, or captive portal session expiration never closes or navigates the assessment page away. Instead, Klyro maintains the exam page in an immutable suspended DOM state while an autonomous background engine and an isolated secondary WebView handle campus Wi-Fi re-authentication without session disruption.

---

## 2. Complete End-to-End System Topology

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                               MANAGED WINDOWS WORKSTATION                              │
│  ┌──────────────────────────────────────────────────────────────────────────────────┐  │
│  │ Windows Shell Launcher v2 / App Control (WDAC) / Local Standard User (KLYRO-EXAM) │  │
│  └────────────────────────────────────────┬─────────────────────────────────────────┘  │
│                                           │ Launches as primary Windows Shell          │
│                                           ▼                                            │
│  ┌──────────────────────────────────────────────────────────────────────────────────┐  │
│  │                           KLYRO NATIVE WINUI 3 SHELL                             │  │
│  │  - SingleInstance Mutex (`Global\Klyro_Secure_Assessment_Browser_SingleInstance`) │  │
│  │  - Low-Level Keyboard Hook (`WH_KEYBOARD_LL`: Win+G, Alt+Tab, Alt+F4, F11, F12)   │  │
│  │  - AppWindow Fullscreen Presenter (No Taskbar, No Window Chrome)                 │  │
│  │  - Invigilator Exit PIN Authorization Interceptor (Default: 270905)               │  │
│  │  - Non-Blocking Asynchronous Audit Logger (System.Threading.Channels)            │  │
│  │  - Atomic Crash Recovery Session Store (`session_recovery.json`)                 │  │
│  │                                                                                  │  │
│  │  ┌─────────────────────────────────┐      ┌───────────────────────────────────┐  │  │
│  │  │     EXAM RUNTIME SUBSYSTEM      │      │     NETWORK ENGINE SUBSYSTEM      │  │  │
│  │  │                                 │      │                                   │  │  │
│  │  │ ┌─────────────────────────────┐ │      │ ┌───────────────────────────────┐ │  │  │
│  │  │ │ ExamWebView (WinUI 3 Host)  │ │      │ │ Autonomous NetworkMonitor     │ │  │  │
│  │  │ │ Origin: codechef.com        │ │      │ │ 4 Concurrent Probes (2000ms)  │ │  │  │
│  │  │ │ Profile: Profiles/Exam      │ │      │ └───────────────┬───────────────┘ │  │  │
│  │  │ └──────────────┬──────────────┘ │      │                 │                 │  │  │
│  │  │                ▼                │      │                 ▼                 │  │  │
│  │  │ ┌─────────────────────────────┐ │      │ ┌───────────────────────────────┐ │  │  │
│  │  │ │ NavigationGuard             │ │      │ │ NetworkStateMachine           │ │  │  │
│  │  │ │ FrameNavigationGuard        │ │      │ │ ONLINE | DEGRADED | OFFLINE   │ │  │  │
│  │  │ │ PopupGuard                  │ │      │ │ PORTAL_REQUIRED | CHEF_DOWN   │ │  │  │
│  │  │ │ DownloadGuard               │ │      │ └───────────────┬───────────────┘ │  │  │
│  │  │ │ ProtocolGuard               │ │      │                 │                 │  │  │
│  │  │ └─────────────────────────────┘ │      │                 ▼                 │  │  │
│  │  │                │                │      │ ┌───────────────────────────────┐ │  │  │
│  │  │                │ Preserved DOM  │      │ │ WifiNetworkManager            │ │  │  │
│  │  │                │ State          │      │ │ Campus AP Auto-Discovery      │ │  │  │
│  │  │                ▼                │      │ │ Dynamic WLAN XML Provisioning │ │  │  │
│  │  │ ┌─────────────────────────────┐ │      │ └───────────────┬───────────────┘ │  │  │
│  │  │ │ CodeChef Contest Code / DOM │ │      │                 │                 │  │  │
│  │  │ └─────────────────────────────┘ │      │                 ▼                 │  │  │
│  │  └─────────────────────────────────┘      │ ┌───────────────────────────────┐ │  │  │
│  │                                           │ │ RecoveryController            │ │  │  │
│  │                                           │ │ Background Portal Throttle    │ │  │  │
│  │                                           │ │ Suppression Engine            │ │  │  │
│  │                                           │ └───────────────┬───────────────┘ │  │  │
│  │                                           └─────────────────┼─────────────────┘  │  │
│  │                                                             │                    │  │
│  │                                                             ▼                    │  │
│  │                                           ┌───────────────────────────────────┐  │  │
│  │                                           │ RecoveryWebView (Captive Portal)  │  │  │
│  │                                           │ Origin: 172.16.103.254:8090/*     │  │  │
│  │                                           │ Profile: Profiles/Recovery        │  │  │
│  │                                           │ Active in Background (Unfrozen)   │  │  │
│  │                                           └───────────────────────────────────┘  │  │
│  └──────────────────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────┬─────────────────────────────────────────────┘
                                           │
                                           ▼
                      CAMPUS WIRELESS & NETWORK CONTROLLER TOPOLOGY
  ┌───────────────────────┐    ┌──────────────────────────┐    ┌─────────────────────────┐
  │ Local Default Gateway │◄──►│ Campus Captive Portal    │◄──►│ Global Internet / WAN   │
  │ (e.g., 172.16.100.1)  │    │ (172.16.103.254:8090)    │    │ (HTTP 204 / CodeChef)   │
  └───────────────────────┘    └──────────────────────────┘    └─────────────────────────┘
```

---

## 3. Comprehensive Subsystem & File Breakdown (A to Z)

The Klyro solution is organized into clear architectural layers across `Klyro.Core`, `Klyro.App`, configuration profiles, deployment scripts, and automated test suites.

```
Klyro/
├── config/                     // JSON configuration environments
├── docs/                       // Master architectural and deployment documentation
├── src/
│   ├── Klyro.Core/             // Core security, networking, recovery, and exam engines
│   │   ├── Browser/            // WebView2 guards and lifecycle interfaces
│   │   ├── Configuration/      // Strongly-typed options, loader, and validator
│   │   ├── Exam/               // Exam state machine, timer, and session tracking
│   │   ├── Health/             // Environmental pre-flight checks and diagnostics
│   │   ├── Logging/            // Lock-free non-blocking audit logging
│   │   ├── Network/            // Multi-probe monitoring, Wi-Fi management, telemetry
│   │   ├── Persistence/        // Atomic session storage and recovery records
│   │   ├── Recovery/           // Captive portal orchestration and auth verification
│   │   └── Security/           // Allowlist manager, protocol policy, keyboard hook
│   ├── Klyro.App/              // WinUI 3 presentation shell and UI views
│   └── Klyro.Admin/            // Administrative CLI diagnostic utility
└── tests/
    ├── Klyro.SecurityTests/     // Unit tests for origin matching, popups, downloads
    ├── Klyro.NetworkTests/      // Unit tests for multi-probe and Wi-Fi manager
    ├── Klyro.RecoveryTests/     // Unit tests for recovery lifecycle and allowlists
    └── Klyro.UnitTests/         // Unit tests for timers, state machines, preflight
```

---

### 3.1. Browser Subsystem (`Klyro.Core/Browser`)

The Browser subsystem encapsulates Microsoft Edge WebView2 security event interception. It decouples the UI representation from security policies, ensuring all navigations fail closed unless explicitly authorized.

#### 1. [`NavigationGuard.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Browser/NavigationGuard.cs)
- **Role:** Primary gatekeeper for top-level window navigation.
- **Implementation:**
  - Evaluates every `NavigationStarting` event triggered on `ExamWebView` and `RecoveryWebView`.
  - Dispatches calls to `AllowlistManager.IsExamNavigationAllowed` or `AllowlistManager.IsRecoveryNavigationAllowed`.
  - Returns a structured `NavigationDecision` object containing `IsAllowed` and `Reason`.
  - If `IsAllowed == false`, the native shell executes `e.Cancel = true`, stops loading animations, and logs a `NAVIGATION_BLOCKED` audit event.

#### 2. [`FrameNavigationGuard.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Browser/FrameNavigationGuard.cs)
- **Role:** Sub-frame / child `iframe` navigation security.
- **Implementation:**
  - Intercepts `FrameNavigationStarting` events.
  - Prevents clickjacking, covert iframe tunneling, and unauthorized credential exfiltration via embedded third-party frames.
  - Context-aware: supports both Exam context (allowing legitimate contest IDE widgets) and Recovery context (strictly locked to the portal origin).

#### 3. [`PopupGuard.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Browser/PopupGuard.cs)
- **Role:** Window spawning and tab creation interdiction.
- **Implementation:**
  - Intercepts WebView2 `NewWindowRequested` events.
  - Inspects `SecurityOptions.AllowNewWindows` and `SecurityOptions.AllowNewTabs` (both default to `false`).
  - Sets `e.Handled = true` unconditionally in examination mode, completely suppressing `window.open()`, `target="_blank"`, and `Ctrl+Click` window creations.
  - Generates audit event `POPUP_BLOCKED`.

#### 4. [`DownloadGuard.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Browser/DownloadGuard.cs)
- **Role:** File download and local filesystem writing interdiction.
- **Implementation:**
  - Intercepts WebView2 `DownloadStarting` events.
  - Gated by `SecurityOptions.AllowDownloads` (default `false`).
  - When disabled, immediately executes `e.Handled = true` and `e.Cancel = true`, preventing candidates from downloading executables, source archives, or triggering "Save As" file picker dialogs that expose the host filesystem.
  - Emits `DOWNLOAD_BLOCKED` audit records.

#### 5. [`ProtocolGuard.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Browser/ProtocolGuard.cs)
- **Role:** Operating system custom scheme handler blocker.
- **Implementation:**
  - Evaluates URI scheme before any protocol resolution occurs.
  - Blocks dangerous schemes: `mailto:`, `tel:`, `file:`, `ms-settings:`, `ms-windows-store:`, `cmd:`, `powershell:`, `javascript:`, `data:`.
  - Permits strictly `https:` and `http:`, plus internal benign `about:blank`.

#### 6. [`IWebViewHost.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Browser/IWebViewHost.cs)
- **Role:** Architectural abstraction decoupling the browser security engine from WinUI 3 UI threads for unit and mock testing.

---

### 3.2. Security Subsystem (`Klyro.Core/Security`)

#### 1. [`AllowlistManager.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Security/AllowlistManager.cs)
- **Role:** High-performance, spoof-resilient canonical origin matcher.
- **Key Logic:**
  - Uses an internal struct `UriOrigin` containing canonicalized `(Scheme, Host, Port)`.
  - Normalizes hostnames and compares exact origins using strict case-insensitive hashing.
  - **Lookalike Domain Detection:** Inspects requests for spoofed domains (e.g., `codechef.com.attacker.org` or `evil-codechef.com`) and generates targeted audit alerts.
  - **Google OAuth Integration:** When Google authentication is permitted for CodeChef single sign-on, `IsGoogleAuthDomain` safely permits essential identity endpoints:
    - `accounts.google.*`, `myaccount.google.*`
    - `oauth2.googleapis.com`, `apis.google.com`, `content.googleapis.com`
    - `ssl.gstatic.com`, `www.gstatic.com`, `fonts.gstatic.com`, `fonts.googleapis.com`
    - `accounts.youtube.com` (required for Google Account session verification)
  - **Production HTTPS Enforcement:** Prohibits plaintext `http:` schemes for the exam origin in production environments while permitting the university captive portal HTTP origin (`http://172.16.103.254:8090`).

#### 2. [`KeyboardHookManager.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Security/KeyboardHookManager.cs)
- **Role:** Low-level Windows OS shortcut interception and GameDVR suppression.
- **Key Logic:**
  - Installs a native Win32 `WH_KEYBOARD_LL` (low-level keyboard hook) via `SetWindowsHookEx`.
  - **Intercepted Keys & Actions:**
    - `VK_LWIN` & `VK_RWIN` (Windows Keys): Intercepted and swallowed (`return (IntPtr)1`). Blocks `Win+G` (Game Bar), `Win+D` (Show Desktop), `Win+R` (Run), `Win+Tab` (Task View), `Win+X` (Quick Link).
    - `Alt+Tab` & `Alt+Esc`: Intercepted and swallowed. Blocks task switching.
    - `Ctrl+Esc`: Intercepted and swallowed. Blocks Windows Start Menu.
    - `F11` (Fullscreen Toggle) & `F12` (Developer Tools): Intercepted and swallowed.
    - `Alt+F4`: Intercepted. Instead of allowing the application to close, it raises the native `ExitRequested` event, triggering the modal Invigilator PIN prompt.
  - **GameDVR Registry Enforcement:**
    - Sets `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled = 0`.
    - Sets `HKCU\System\GameConfigStore\GameDVR_Enabled = 0`.
    - Eliminates the Windows Game Bar broadcast/recording HUD.

#### 3. [`ProtocolPolicy.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Security/ProtocolPolicy.cs) & [`NavigationDecision.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Security/NavigationDecision.cs)
- Enforces strict web scheme verification (`https`, `http`) and produces immutable result tokens for logging and UI error display.

---

### 3.3. Network Subsystem (`Klyro.Core/Network`)

#### 1. [`NetworkMonitor.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Network/NetworkMonitor.cs)
- **Role:** Autonomous background telemetry service executing multi-probe health checks.
- **Operation:**
  - Runs on a dedicated background thread with configurable interval (default: `2000ms`).
  - Dispatches 4 concurrent network probes:
    1. **Gateway Probe:** ICMP Ping to default gateway IP (resolved dynamically via `NetworkInterface.GetAllNetworkInterfaces()`).
    2. **Captive Portal Probe:** HTTP GET to `http://172.16.103.254:8090/`.
    3. **Global Internet Probe:** HTTP GET with expected HTTP 204 response (`http://clients3.google.com/generate_204`).
    4. **Assessment Provider Probe:** HTTP GET/HEAD to `https://www.codechef.com/`.
  - Calculates roundtrip latency and categorizes connection speed:
    - `< 80 ms`: `Fast` (Green)
    - `< 250 ms`: `Good` (Amber)
    - `>= 250 ms`: `Slow` (Orange)
    - Failed: `Offline` (Red)

#### 2. [`CaptivePortalDetector.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Network/CaptivePortalDetector.cs) & [`NetworkStateMachine.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Network/NetworkStateMachine.cs)
- **Role:** Deterministic state machine governing network transitions.
- **States:**
  - `STARTING`: Initializing adapters.
  - `ONLINE`: Gateway reachable, Internet reachable (HTTP 204 verified), CodeChef reachable.
  - `DEGRADED`: Gateway reachable, but intermittent internet packet loss before reaching consecutive failure threshold.
  - `PORTAL_REQUIRED`: Gateway reachable, Portal controller reachable, Internet failing for $\ge 2$ consecutive cycles.
  - `OFFLINE`: Physical adapter down or gateway unreachable.
  - `ASSESSMENT_SERVICE_UNAVAILABLE`: Internet fully functional, but CodeChef servers failing.

#### 3. [`WifiNetworkManager.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Network/WifiNetworkManager.cs)
- **Role:** Native campus Wi-Fi discovery, automatic profile synthesis, and connection engine.
- **Key Capabilities:**
  - **Campus Network Recognition:** Employs regex `^\d{4}[A-Za-z]?\s*-\s*\w+` (e.g. `8601A -  2`, `8301B - 1`, `8401 - A2`) and explicit matching for `KLU_WIFI` / `Kalasalingam`.
  - **Autonomous Connection (`AutoConnectCampusWifiAsync`):** On startup or Wi-Fi disconnection, automatically scans all available BSSIDs, prioritizes `KLU_WIFI` or the strongest campus lab AP, and establishes connection.
  - **Dynamic WLAN XML Profile Provisioning:** If an unconfigured campus network is selected, generates a temporary open or WPA2-PSK XML profile:
    ```xml
    <?xml version="1.0"?>
    <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
        <name>{ssid}</name>
        <SSIDConfig><SSID><name>{ssid}</name></SSID></SSIDConfig>
        <connectionType>ESS</connectionType>
        <connectionMode>manual</connectionMode>
        <MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security></MSM>
    </WLANProfile>
    ```
    Registers profile via `netsh wlan add profile` and initiates immediate connection.

#### 4. [`WifiSignalService.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Network/WifiSignalService.cs)
- **Role:** High-performance, cached Wi-Fi signal percentage telemetry.
- Caches queries for 2000ms to eliminate netsh process thrashing while supplying smooth real-time signal updates (e.g., `92%`) to the header bar.

#### 5. [`NetworkSimulator.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Network/NetworkSimulator.cs)
- **Role:** Mocking harness allowing developers and QA engineers to simulate portal drop, degraded link, offline state, and CodeChef outages without touching physical hardware.

---

### 3.4. Recovery Subsystem (`Klyro.Core/Recovery`)

#### 1. [`RecoveryController.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Recovery/RecoveryController.cs)
- **Role:** Coordinates the captive portal recovery lifecycle.
- **Lifecycle States:**
  - `Hidden`: Modal overlay hidden; exam is primary.
  - `LoginRequired`: Captive portal detected; modal overlay displayed.
  - `Authenticating`: Credentials submitted; background verification active.
  - `ConnectionRestored`: Internet restored; overlay transitions to background.
  - `ActiveInBackground`: **Crucial state!** Portal WebView remains alive in the visual tree (Opacity 0 / HitTestVisible False) to prevent campus network session timeouts.
  - `RecoveryFailed`: Authentication failed or timed out.

#### 2. [`PortalAuthenticationMonitor.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Recovery/PortalAuthenticationMonitor.cs)
- **Role:** Independent out-of-band network verification after candidate submits login credentials.
- Bounded by `NetworkOptions.RecoveryTimeoutMs` (default 60 seconds). Verifies external HTTP 204 before signaling recovery success.

---

### 3.5. Exam Subsystem (`Klyro.Core/Exam`)

#### 1. [`ExamStateMachine.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Exam/ExamStateMachine.cs)
- Coordinates overall exam states: `INITIALIZING`, `PREFLIGHT_CHECKS`, `READY`, `ACTIVE`, `NETWORK_RECOVERY`, `PAUSED`, `RESUMING`, `COMPLETED`, `FAILED`.
- Automatically pauses the exam timer during verified network recovery if configured under `TimerPolicy = "PauseDuringVerifiedNetworkFailure"`.
- Triggers periodic persistence every 15 seconds.

#### 2. [`ExamTimer.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Exam/ExamTimer.cs)
- High-precision exam countdown clock.
- Dispatches `Tick` and `TimeExpired` events to ensure automated contest conclusion upon reaching 0 seconds.

---

### 3.6. Health & Pre-flight Subsystem (`Klyro.Core/Health`)

#### 1. [`PreflightChecker.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Health/PreflightChecker.cs) & [`StandardPreflightChecks.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Health/Checks/StandardPreflightChecks.cs)
- Executes environmental diagnostics before launching the exam session:
  1. **Windows Version Check (Critical):** Verifies Windows 10/11 OS build.
  2. **Configuration Integrity Check (Critical):** Validates URLs, origins, and options.
  3. **Network Adapter Check (Critical):** Confirms an operational Wi-Fi or Ethernet adapter exists.
  4. **Disk Storage State Check (Warning):** Verifies at least 200 MB free space for browser caching.
  5. **System Clock Synchronization Check (Warning):** Confirms valid year ($\ge 2024$) to prevent SSL/TLS certificate invalidation errors.
  6. **Assessment Provider Reachability Check (Critical):** Confirms CodeChef is reachable.

---

### 3.7. Persistence Subsystem (`Klyro.Core/Persistence`)

#### 1. [`SessionStore.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Persistence/SessionStore.cs)
- Atomic file persistence mechanism avoiding crash corruption.
- Writes new session state to `session_recovery.json.tmp` and executes atomic replacement via `File.Move(..., overwrite: true)` protected by `SemaphoreSlim(1, 1)`.

#### 2. [`SessionRecord.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Persistence/SessionRecord.cs)
- Persists `ExamId`, `SessionId`, `MachineId`, `StartTimestampUtc`, `CurrentExamState`, `RemainingSeconds`, and `LastKnownAssessmentUrl`.
- **Zero-Credential Guarantee:** Explicitly excludes candidate passwords, Wi-Fi credentials, session tokens, keystrokes, and clipboard data.

---

### 3.8. Logging Subsystem (`Klyro.Core/Logging`)

#### 1. [`AuditLogger.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Logging/AuditLogger.cs)
- Non-blocking, lock-free audit logging utilizing `System.Threading.Channels.Channel<AuditEvent>`.
- Appends serialized JSONL entries asynchronously to `%LOCALAPPDATA%\Klyro\Logs\klyro_audit_YYYYMMDD.jsonl`.
- Sanitizes all logged text to redact accidental query string passwords (`password=[REDACTED]`, `token=[REDACTED]`).

---

### 3.9. Configuration Subsystem (`Klyro.Core/Configuration`)

#### 1. [`KlyroConfiguration.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Configuration/KlyroConfiguration.cs), [`ConfigurationLoader.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Configuration/ConfigurationLoader.cs), & [`ConfigurationValidator.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.Core/Configuration/ConfigurationValidator.cs)
- Provides strongly typed schemas for Application, Exam, Network, Security, and Browser configuration sections.
- Validates URL formats, port ranges, and required allowlist entries.

---

### 3.10. Application Shell (`Klyro.App`)

#### 1. [`Program.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.App/Program.cs)
- Application entry point.
- **Single-Instance Mutex:**
  - Instantiates named mutex `Global\Klyro_Secure_Assessment_Browser_SingleInstance`.
  - When a secondary instance launches, it detects the active mutex, uses Win32 `ShowWindow(hWnd, SW_RESTORE)` and `SetForegroundWindow(hWnd)` to bring the running Klyro window to the foreground, and exits immediately. This eliminates profile lock contention crashes.
- Initializes WinRT COM wrappers and starts the WinUI 3 XAML dispatcher.

#### 2. [`MainWindow.xaml.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.App/MainWindow.xaml.cs)
- Hosts `MainPage`.
- Configures `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)` to cover the entire screen and completely hide the Windows taskbar.
- Intercepts `AppWindow.Closing` to cancel standard window closing and prompt for the Invigilator PIN.

#### 3. [`MainPage.xaml`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.App/MainPage.xaml) & [`MainPage.xaml.cs`](file:///c:/Users/pvenk/Downloads/Klyro/src/Klyro.App/MainPage.xaml.cs)
- **Top Header Bar (56px):**
  - Brand identity with institutional badge (`KLYRO SECURE BROWSER`).
  - Segmented Dual Tabs: **CodeChef Assessment** tab and **Campus Wi-Fi** tab with live status pill (`Active in BG (18ms)`).
  - Navigation Capsule: Back, Forward, Refresh, and Home buttons with slate iconography (`#475569`).
  - Safe Domain Pill: displays current secure origin.
  - Live Wi-Fi Latency & Signal Badge (`77% • 18 ms • Fast ▾`): Clickable dropdown opening the Wi-Fi picker modal.
  - Exit Exam Button: Prompts for Invigilator PIN.
- **Wi-Fi Picker Modal Dialog:**
  - Lists available campus and lab APs with signal badges.
  - Dedicated WPA2 password prompt card.
  - **4 Dismissal Options:**
    1. Primary footer action button: `[ ✕ Close Menu ]`
    2. Header action button: `[ ✕ Close ]`
    3. Tap outside dialog backdrop (`WifiPickerOverlay_Tapped`)
    4. Hardware `Escape` key handler (`MainPage_KeyDown`)
- **Captive Portal Recovery Modal Overlay:**
  - Renders `RecoveryWebView` in isolated environment.
  - Displays recovery status progress ring.
  - Allows candidate to authenticate or click `[ Return to Exam (Active in BG) ]`.
- **WebView2 Profile Lock Isolation Fallback:**
  - If the default profile directory (`Profiles/Exam` or `Profiles/Recovery`) is locked, catches the exception and initializes an isolated session directory (`Exam_{pid}` / `Recovery_{pid}`).
- **In-DOM Anti-Cheating Injections:**
  - Adds script on document created:
    ```javascript
    document.addEventListener('contextmenu', e => e.preventDefault(), true);
    document.addEventListener('copy', e => e.preventDefault(), true);
    document.addEventListener('cut', e => e.preventDefault(), true);
    document.addEventListener('paste', e => e.preventDefault(), true);
    ```
  - Intercepts keyboard shortcuts: `Ctrl+C`, `Ctrl+V`, `Ctrl+X`, `Ctrl+P`, `Ctrl+S`, `Ctrl+U`, `F12`.

---

## 4. Resilience & Fallback Architecture (The 7 Core Fallbacks)

Klyro's core strength lies in its ability to recover autonomously from failures that terminate standard examination browsers:

| # | Scenario / Failure Mode | Fallback Mechanism | Technical Implementation |
|---|---|---|---|
| **F1** | **Multi-Instance Startup Contention** | Mutex Interception & Focus Activation | Secondary instance detects `Global\Klyro_Secure_Assessment_Browser_SingleInstance`, restores existing window handle via Win32 `ShowWindow` / `SetForegroundWindow`, and terminates cleanly without corrupting the profile. |
| **F2** | **WebView2 Profile Lock Contention** | Process-Isolated Session Fallback | If `CreateWithOptionsAsync` fails due to lock on `%LOCALAPPDATA%\Klyro\Profiles\Exam`, catches exception and dynamically falls back to `%LOCALAPPDATA%\Klyro\Profiles\Exam_{ProcessId}`. |
| **F3** | **Campus Wi-Fi Disconnection / Roaming** | Autonomous AP Discovery & XML Synthesis | Scans BSSIDs using `netsh wlan`, identifies university network via regex `^\d{4}[A-Za-z]?\s*-\s*\w+`, generates open or WPA2 WLAN XML profile, adds via `netsh wlan add profile`, and connects automatically. |
| **F4** | **Captive Portal Background Throttling** | Background Heartbeat Preservation Flags | `RecoveryWebView` remains in visual tree (`ActiveInBackground`) with Chromium flags: `--disable-background-timer-throttling`, `--disable-backgrounding-occluded-windows`, `--disable-renderer-backgrounding`, and `--disable-features=CalculateNativeWinOcclusion`. Portal session stays alive without timing out. |
| **F5** | **Mid-Exam Internet Session Drop** | Zero-Sifting Automatic Recovery Popup | When `NetworkMonitor` detects `PORTAL_REQUIRED` or `OFFLINE`, modal overlay appears automatically over the screen. Student enters credentials in portal; on HTTP 204 restoration, modal automatically minimizes to background and focuses code editor. |
| **F6** | **CodeChef Server Outage vs. Wi-Fi Loss** | Decoupled Multi-Probe Diagnosis | Multi-probe concurrent matrix checks Gateway, Portal, HTTP 204, and CodeChef. If Internet works (HTTP 204) but CodeChef fails, transitions to `ASSESSMENT_SERVICE_UNAVAILABLE`. Displays institutional notice without opening captive portal or blaming Wi-Fi. |
| **F7** | **Workstation Reboot or Shell Crash** | Atomic Session State Restoration | `SessionStore` persists state via atomic file move. Upon shell restart, `MainPage` loads active session record, restores `RemainingSeconds` and `LastKnownAssessmentUrl`, resuming contest seamlessly. |

---

## 5. Anti-Cheating & Security Lockdown Matrix

| Threat Code | Attack Vector | Primary Enforcement Layer | Secondary Fallback Layer | Failure Policy |
|---|---|---|---|---|
| **T1: External Browsing** | Student navigates to search engine, ChatGPT, or external forum. | `NavigationGuard` strict origin allowlist. | `FrameNavigationGuard` on child frames. | **Fail Closed (BLOCK)** |
| **T2: Other Browsers** | Student attempts to open Edge, Chrome, or Firefox. | Windows App Control (WDAC) binary allowlist. | Shell Launcher v2 replaces `explorer.exe`. | **Fail Closed (BLOCK)** |
| **T3: DevTools Inspection** | Candidate presses `F12` or opens inspect element. | WebView2 `AreDevToolsEnabled = false`. | Low-level keyboard hook blocks `VK_F12`. | **Fail Closed (BLOCK)** |
| **T4: Secondary Windows** | Script executes `window.open()` or `target="_blank"`. | `PopupGuard` cancels `NewWindowRequested`. | WinUI 3 Windowing architecture. | **Fail Closed (BLOCK)** |
| **T5: File Downloads** | Candidate tries to download scripts or executables. | `DownloadGuard` cancels `DownloadStarting`. | Kiosk local user directory permissions. | **Fail Closed (BLOCK)** |
| **T6: OS Binary Launch** | Link triggers `calc.exe`, `powershell.exe`, or `cmd.exe`. | `ProtocolGuard` cancels custom protocol handler. | Windows App Control (WDAC) denies startup. | **Fail Closed (BLOCK)** |
| **T7: Keyboard Escape** | Candidate presses `Win+G`, `Win+D`, `Alt+Tab`, `Alt+Esc`. | `KeyboardHookManager` intercepts in low-level hook. | GameDVR registry keys set to 0. | **Fail Closed (BLOCK)** |
| **T8: Clipboard Theft** | Candidate attempts to copy problem statement or paste code. | Injected DOM script prevents `copy`, `cut`, `paste`. | `ExamWebView.PreviewKeyDown` blocks `Ctrl+C/V/X`. | **Fail Closed (BLOCK)** |
| **T9: Right-Click Menu** | Candidate right-clicks to view page source or inspect. | Injected DOM script prevents `contextmenu`. | `AreDefaultContextMenusEnabled = false`. | **Fail Closed (BLOCK)** |
| **T10: Forced App Exit** | Candidate presses `Alt+F4` or close button. | `MainWindow.AppWindow.Closing` canceled; PIN required. | `KeyboardHookManager` intercepts `Alt+F4`. | **Fail Closed (PIN)** |

---

## 6. Network Probing Engine & State Truth Table

### 6.1. Concurrent Probe Specifications
Every cycle (default `2000ms`), the network engine executes 4 concurrent asynchronous tasks:
1. `ProbeGatewayAsync`: ICMP Ping to dynamic default gateway IP.
2. `ProbeCaptivePortalAsync`: HTTP GET to `http://172.16.103.254:8090/`.
3. `ProbeInternetAsync`: HTTP GET to `http://clients3.google.com/generate_204`.
4. `ProbeAssessmentAsync`: HTTP GET/HEAD to `https://www.codechef.com/`.

### 6.2. Deterministic State Evaluation Matrix

| Gateway Reachable | Internet Reachable (204) | Assessment Reachable | Portal Reachable | Consecutive Failures | Resulting `NetworkState` | Action Taken |
|:---:|:---:|:---:|:---:|:---:|:---|:---|
| **No** | *Any* | *Any* | *Any* | *Any* | **`OFFLINE`** | Search for campus Wi-Fi; show Wi-Fi modal if disconnected. |
| **Yes** | **Yes** | **Yes** | *Any* | 0 | **`ONLINE`** | Full access. If recovery modal was visible, minimize to background. |
| **Yes** | **Yes** | **No** | *Any* | 0 | **`ASSESSMENT_SERVICE_UNAVAILABLE`** | Show notice: CodeChef servers down. Do NOT pop captive portal. |
| **Yes** | **No** | *Any* | **Yes** | $< \text{threshold}$ | **`DEGRADED`** | Background retry; prevent UI flapping. |
| **Yes** | **No** | *Any* | **Yes** | $\ge \text{threshold}$ | **`PORTAL_REQUIRED`** | Display Captive Portal Recovery Modal immediately. |
| **Yes** | **No** | *Any* | **No** | *Any* | **`DEGRADED`** | Uplink down; quiet background retry. |

---

## 7. Verification & Automated Test Matrix (140/140 Tests Passing)

The Klyro test suite verifies all critical components through 140 automated tests:

```
Total Test Suites: 4
Total Automated Tests: 140
Status: 140 Passed (100%), 0 Failed, 0 Skipped
Execution Time: ~4.1 seconds
```

### 7.1. Test Suite Breakdown
1. **`Klyro.SecurityTests` (52 Tests):**
   - Origin parsing, lookalike subdomains, case normalization.
   - Google OAuth domain allowlist boundary verification.
   - External and dangerous protocol scheme blocking.
   - Download interception and popup cancellation.
   - Configuration schema validation.
2. **`Klyro.NetworkTests` (44 Tests):**
   - 4-probe matrix evaluation across all permutation states.
   - Wi-Fi SSID campus recognition and regex matching (`8601A -  2`, `KLU_WIFI`).
   - Netsh output parsing and BSSID extraction.
   - WLAN XML profile formatting (open and WPA2-PSK).
   - Network simulation harness.
3. **`Klyro.UnitTests` (26 Tests):**
   - Exam state machine transitions and persistence triggers.
   - Exam timer policies (pausing vs. continuing during outages).
   - Pre-flight checks (Windows version, config, network adapter, storage).
   - Asynchronous audit logger channel throughput and credential sanitization.
   - Session store atomic file swapping.
4. **`Klyro.RecoveryTests` (18 Tests):**
   - Captive portal detection and recovery controller state flow.
   - Transition to `ActiveInBackground` upon internet verification.
   - Strict recovery origin boundary (`172.16.103.254:8090/*`).
   - Re-authentication retry timeout logic.

---

## 8. Deployment & Administrative Runbook

### 8.1. Workstation Installation
To deploy Klyro on university laboratory computers running Windows 10/11:
1. Open PowerShell as Administrator.
2. Run the deployment script:
   ```powershell
   cd C:\Users\pvenk\Downloads\Klyro
   .\Install-Klyro.ps1
   ```
   This script builds the self-contained WinUI 3 binaries, copies assets and configurations to `C:\Klyro`, installs desktop shortcuts, and registers kiosk permissions.

### 8.2. Launching Klyro
- **Standard User Execution:** Double-click the desktop shortcut or run `C:\Klyro\Klyro.App.exe`.
- **Kiosk Assigned Access Execution:** Automatically launches when the student logs into the `KLYRO-EXAM` workstation account.

### 8.3. Invigilator Exit Procedure
To close Klyro at the conclusion of an examination:
1. Click the red **[ Exit Exam ]** button in the top-right corner, or press **`Alt+F4`**.
2. When the **Invigilator Authorization** modal appears, enter the PIN:
   ```
   270905
   ```
3. Click **[ Authorize & Exit ]** or press Enter. The low-level keyboard hook uninstalls and the application exits cleanly.
