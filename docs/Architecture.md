# Klyro: Secure Assessment Browser — Master Architecture Document

## 1. System Overview & Vision

**Klyro** is a purpose-built Windows desktop examination browser designed for university coding assessments (initially configured for CodeChef, but architecturally provider-agnostic). It transforms managed laboratory Windows workstations into secure, distraction-free assessment appliances.

Rather than functioning as a conventional browser or a fragile JavaScript-based page wrapper, Klyro implements a defense-in-depth architecture across four distinct security and operational tiers:
1. **Operating System Layer**: Windows Assigned Access (Shell Launcher v2) & App Control for Business (WDAC) executing under a least-privileged standard local user account (`KLYRO-EXAM`).
2. **Native Shell Layer**: WinUI 3 desktop application running .NET 8 LTS, enforcing fullscreen display, lifecycle coordination, native non-browser chrome, and crash resilience.
3. **Restricted Web Runtime Layer**: Hardened Microsoft WebView2 engine with strict origin allowlists, frame navigation guards, popup suppression, download interdiction, and disabled developer/context menus.
4. **Network Engine & Captive Portal Recovery**: Autonomous multi-probe background connectivity service with deterministic state machine and isolated modal recovery WebView for university captive portal re-authentication without session disruption.

```
┌────────────────────────────────────────────────────────────────────────┐
│                        MANAGED WINDOWS HARDWARE                        │
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │ Windows Shell Launcher v2 / App Control (WDAC)                    │ │
│ │ Least-Privileged Local Account: KLYRO-EXAM                         │ │
│ └──────────────────────────────────┬─────────────────────────────────┘ │
│                                    ▼                                   │
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │                         KLYRO NATIVE SHELL                         │ │
│ │        (WinUI 3 / .NET 8 / MVVM / Background Services)             │ │
│ │                                                                    │ │
│ │  ┌───────────────────────────┐    ┌─────────────────────────────┐  │ │
│ │  │        EXAM ENGINE        │    │       NETWORK ENGINE        │  │ │
│ │  │                           │    │                             │  │ │
│ │  │ ┌───────────────────────┐ │    │ ┌─────────────────────────┐ │  │ │
│ │  │ │  ExamWebViewHost      │ │    │ │ Multi-Probe Service     │ │  │ │
│ │  │ │  (CodeChef / Exam)    │ │    │ │ Gateway / Portal / 204  │ │  │ │
│ │  │ └───────────┬───────────┘ │    │ └────────────┬────────────┘ │  │ │
│ │  │             ▼             │    │              ▼              │  │ │
│ │  │ ┌───────────────────────┐ │    │ ┌─────────────────────────┐ │  │ │
│ │  │ │ NavigationGuard       │ │    │ │ NetworkStateMachine     │ │  │ │
│ │  │ │ FrameNavigationGuard  │ │    │ └────────────┬────────────┘ │  │ │
│ │  │ │ PopupGuard            │ │    │              ▼              │  │ │
│ │  │ │ ProtocolGuard         │ │    │ ┌─────────────────────────┐ │  │ │
│ │  │ │ DownloadGuard         │ │    │ │ RecoveryController      │ │  │ │
│ │  │ └───────────────────────┘ │    │ └────────────┬────────────┘ │  │ │
│ │  └───────────────────────────┘    └──────────────┼──────────────┘  │ │
│ │                                                  ▼                 │ │
│ │                                   ┌─────────────────────────────┐  │ │
│ │                                   │    RecoveryWebViewHost      │  │ │
│ │                                   │    (Captive Portal Modal)   │  │ │
│ │                                   │    172.16.103.254:8090/*    │  │ │
│ │                                   └─────────────────────────────┘  │ │
│ └────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────┬───────────────────────────────────┘
                                     ▼
                      UNIVERSITY NETWORK INFRASTRUCTURE
         Default Gateway ◄──► Captive Portal ◄──► Internet / CodeChef
```

---

## 2. Layered Security Model

### Layer 1: Windows OS & Device Lockdown
- **Least-Privileged Account**: The student logs into `KLYRO-EXAM`, a standard local account stripped of administrative rights, access to command prompts, and personal profile storage.
- **Shell Launcher v2 (Assigned Access)**: In place of `explorer.exe` (the Windows taskbar, start menu, and desktop), Windows executes `Klyro.App.exe`. If the process terminates unexpectedly, the OS Shell Launcher automatically restarts the application.
- **Application Control (WDAC)**: Windows App Control for Business enforces binary allowlisting. Even if a binary or script is placed on disk, kernel-level enforcement blocks execution of unauthorized browsers (Chrome, Edge, Firefox), terminals (cmd, powershell), or utilities (Taskmgr, Regedit).
- **Keyboard Filter (WEKF)**: Suppresses OS-level system escape keys (`Win+X`, `Win+R`, `Win+D`, `Ctrl+Shift+Esc`, `Alt+Tab`, `Win+G`).

### Layer 2: Klyro Native Desktop Shell
- **Zero Browser Chrome**: Strips address bar, tab strip, back/forward buttons, menu bars, and status bars.
- **Minimalist Institutional UI**: Only a quiet assessment view with an unobtrusive status dot in the upper corner indicating network health (Green: Online, Amber: Degraded, Blue: Portal Required, Red: Offline).
- **Process Lifecycle Management**: Coordinates exam state, session persistence, health monitoring, and audit logging.

### Layer 3: WebView2 Web Security Runtime
- **Strict Navigation Guard**: Intercepts `NavigationStarting` and `FrameNavigationStarting` events. Every request is verified against canonicalized origins in the allowlist. Unauthorized requests trigger `e.Cancel = true`.
- **Lookalike & Subdomain Shield**: Strict URI parsing isolates Scheme, Host, and Port. Subdomain lookalikes (`evil-codechef.com`, `codechef.com.attacker.org`) fail closed.
- **Popup & Tab Interdiction**: Intercepts `NewWindowRequested` and sets `e.Handled = true`. Content requesting `window.open` or `target="_blank"` cannot launch secondary windows.
- **External Protocol Guard**: Cancels invocations of OS protocol handlers (`mailto:`, `file:`, `ms-settings:`, `cmd:`, `powershell:`, `javascript:`, `data:`).
- **Runtime Surface Hardening**:
  - `AreDevToolsEnabled = false`
  - `AreDefaultContextMenusEnabled = false`
  - `AreDefaultScriptDialogsEnabled = false`
  - `IsStatusBarEnabled = false`
  - Host objects cleared on `ContentLoading`.

### Layer 4: Network Engine & Dual-WebView Recovery
- **Continuous Multi-Probe Monitoring**: Replaces unreliable `navigator.onLine` checks with active socket pings and HTTP 204 endpoint checks.
- **Dual Isolated WebViews**:
  - `ExamWebViewHost`: Dedicated to CodeChef. User data folder: `%LOCALAPPDATA%\Klyro\Profiles\Exam`. Never navigated away or reloaded during network disruptions.
  - `RecoveryWebViewHost`: Ephemeral instance created in a modal dialog solely for captive portal authentication (`http://172.16.103.254:8090/*`). User data folder: `%LOCALAPPDATA%\Klyro\Profiles\Recovery`.
- **Zero-Credential Retention**: Wi-Fi portal and CodeChef credentials remain strictly within transient browser memory; never captured, intercepted, logged, or persisted by Klyro.

---

## 3. Subsystem Breakdown

### 3.1. Browser Subsystem
```
Browser/
├── ExamWebViewHost.cs       // Primary assessment hosting & lifecycle
├── RecoveryWebViewHost.cs   // Ephemeral captive portal modal host
├── WebViewFactory.cs        // Configures CoreWebView2Settings & environments
├── NavigationGuard.cs       // Validates top-level navigation against allowlist
├── FrameNavigationGuard.cs  // Validates child iframe navigation
├── PopupGuard.cs            // Blocks new windows, new tabs, window.open
├── DownloadGuard.cs         // Intercepts DownloadStarting (block by default)
└── ProtocolGuard.cs         // Intercepts external scheme execution
```

### 3.2. Network Subsystem
```
Network/
├── NetworkMonitor.cs        // Orchestrates probe polling & network adapter queries
├── ConnectivityProbe.cs     // Socket and HTTP probe worker
├── CaptivePortalDetector.cs // Evaluates probe results against threshold rules
├── NetworkState.cs          // State enumeration
├── NetworkStateMachine.cs   // Thread-safe state transition coordinator
└── INetworkSimulator.cs     // Debug-only network condition injector
```

### 3.3. Recovery Subsystem
```
Recovery/
├── RecoveryController.cs           // Shows/hides overlay based on NetworkState
├── RecoveryState.cs                // Interruption, LoginRequired, Authenticating, Restored, Failed
└── PortalAuthenticationMonitor.cs  // Detects portal login post and initiates verification probes
```

### 3.4. Exam Subsystem
```
Exam/
├── ExamSession.cs          // Immutable session identifier & run state
├── ExamState.cs            // State enum (Initializing, Preflight, Active, Recovery, Completed)
├── ExamStateMachine.cs     // Exam lifecycle state transitions
├── ExamTimer.cs            // Native C# timer (respects Continue vs Pause policy)
└── ExamConfiguration.cs    // Parsed strongly-typed exam options
```

### 3.5. Security & Persistence Subsystems
```
Security/
├── SecurityPolicy.cs       // High-level policy coordinator
├── AllowlistManager.cs     // Canonicalized URI & domain matcher
├── ProtocolPolicy.cs       // Approved/blocked scheme dictionary
└── KioskIntegration.cs     // Windows API bridge for shell/kiosk detection

Persistence/
├── SessionStore.cs         // Encrypted minimal state store for crash recovery
└── LocalState.cs           // Transient runtime state
```

### 3.6. Logging, Health & Configuration Subsystems
```
Logging/
├── AuditLogger.cs          // High-integrity structured event recorder
├── EventType.cs            // Enum of all audit events
└── LogStore.cs             // File rotation & tamper-evident JSON writer

Health/
├── PreflightChecker.cs     // 10-point environmental validation runner
├── HealthStatus.cs         // PASS, WARNING, FAIL status model
└── RecoveryHealthMonitor.cs// Runtime heartbeat monitor

Configuration/
├── KlyroConfiguration.cs   // Root configuration schema
├── ConfigurationLoader.cs  // JSON deserializer with environment overrides
└── ConfigurationValidator.cs// Fail-closed schema & security pattern validator
```

---

## 4. Dual-WebView Hosting Architecture

The central architectural innovation of Klyro is the strict physical decoupling of the Assessment environment from the Network Recovery environment:

```
┌────────────────────────────────────────────────────────────────────────┐
│                              KLYRO SHELL                               │
│                                                                        │
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │                         ExamWebViewHost                            │ │
│ │  • Profile: %LOCALAPPDATA%\Klyro\Profiles\Exam                     │ │
│ │  • Target: https://www.codechef.com/                               │ │
│ │  • Allowlist: CodeChef origins only                                │ │
│ │  • State: Never reloaded, frozen/paused during network loss       │ │
│ │  • Preserves: Monaco/Ace editor state, unsubmitted code, DOM       │ │
│ └────────────────────────────────────────────────────────────────────┘ │
│                                   │                                    │
│                     Captive Portal Detected                            │
│                                   │                                    │
│                                   ▼                                    │
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │                    RecoveryWebViewHost (Modal)                     │ │
│ │  • Profile: %LOCALAPPDATA%\Klyro\Profiles\Recovery                 │ │
│ │  • Target: http://172.16.103.254:8090/                             │ │
│ │  • Allowlist: Portal IP:Port only                                  │ │
│ │  • Controls: Borderless, fixed-size, modal overlay                 │ │
│ │  • Dismissal: Closed automatically upon verified connectivity      │ │
│ └────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────┘
```

When network authentication drops:
1. `NetworkMonitor` detects probe failures and triggers `PORTAL_REQUIRED`.
2. `ExamWebViewHost` is visually dimmed/overlayed; no navigation occurs.
3. `RecoveryWebViewHost` is instantiated in a secure WinUI 3 modal dialog.
4. The student submits their credentials to the university portal.
5. `PortalAuthenticationMonitor` detects submission and triggers `ConnectivityProbe`.
6. Upon independent verification of external connectivity and CodeChef reachability, the modal dialog is dismissed.
7. `ExamWebViewHost` is restored to active focus. The student continues without loss of typed code.

---

## 5. Threading & Concurrency Model

Klyro employs strict thread boundaries to guarantee UI fluidity (60 FPS) and non-blocking security checks:

- **UI Thread (WinUI Dispatcher)**:
  - Renders the shell and hosts the CoreWebView2 controls.
  - Handles user interactions and modal display transitions.
  - Never performs synchronous disk I/O, DNS resolution, or network probing.
- **Network Probing Service (Background Worker)**:
  - Runs on a dedicated `Task` loop with a configurable interval (default: 3000ms).
  - Uses asynchronous `HttpClient` with short timeouts (default: 1500ms) and `CancellationToken`.
  - Dispatches state change events to the UI thread via `DispatcherQueue`.
- **Native Exam Timer**:
  - Implemented via high-resolution `System.Threading.PeriodicTimer` on a background thread.
  - Dispatches tick updates to the UI thread once per second.
- **Audit Logging Thread**:
  - Implemented via a thread-safe `Channel<AuditEvent>` (producer-consumer pattern).
  - Background writer flushes events asynchronously to disk to prevent I/O contention.

---

## 6. Process Resilience & Crash Recovery

1. **WebView2 Child Process Crash**:
   - If a WebView2 renderer process terminates (`ProcessFailed` event):
     1. The faulted host instance is safely disposed.
     2. A new `CoreWebView2Environment` is attached.
     3. The last valid assessment URL from `SessionStore` is restored.
     4. An audit event `WEBVIEW_CRASH` is recorded.
2. **Klyro Application Crash**:
   - Shell Launcher v2 detects process exit and immediately relaunches `Klyro.App.exe`.
   - On startup, `SessionStore` detects an uncompleted session.
   - Pre-flight checks re-validate the environment.
   - The assessment is restored directly to the student's active contest URL.
3. **Hardware Reboot / Power Outage**:
   - Windows starts, automatically logs in `KLYRO-EXAM`, and launches Klyro via Shell Launcher.
   - The interrupted session is restored according to the configured invigilator policy.
