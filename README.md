# Klyro — Secure Assessment Browser

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)]()
[![Tests](https://img.shields.io/badge/tests-108%20passed-success.svg)]()
[![Target Framework](https://img.shields.io/badge/.NET-8.0-blue.svg)]()
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey.svg)]()

> **Klyro** is a production-grade, hardened Windows desktop examination browser engineered for university coding assessments. It provides a secure, locked examination environment while solving the critical campus network challenge: **seamless captive-portal recovery without losing student exam state**.

---

## Key Features

- **Dual-WebView Isolation Architecture**:
  - Primary `ExamWebView`: Hosts the coding platform (e.g. CodeChef) in a strictly locked, canonical-allowlist sandbox.
  - Secondary `RecoveryWebView`: Rendered in an isolated overlay modal **only** when captive portal authentication expires.
  - **Zero State Loss**: The primary browser context is frozen/blurred in-place during network drops; student code in the web editor is never cleared or reloaded.

- **Deterministic Multi-Probe Network Engine**:
  - Probes Default Gateway, Captive Portal (`http://172.16.103.254:8090/`), Internet 204 (`https://www.gstatic.com/generate_204`), and Assessment endpoint (`codechef.com`).
  - Damped state machine requires consecutive failures before triggering recovery, preventing UI disruption during brief wireless jitter.
  - Independent double-verification confirms real Internet access before dismissing the captive portal modal.

- **Layered Defense-in-Depth Security**:
  - **Windows Shell Launcher v2**: Replaces `explorer.exe` on standard exam accounts (`KLYRO-EXAM`), disabling task switching and desktop shells.
  - **WDAC / App Control**: Whitelists trusted Klyro binaries and Microsoft WebView2 dependencies.
  - **Fail-Closed Navigation Guards**: Hard block on unapproved protocols (`file:`, `mailto:`, `ms-settings:`, `cmd:`, `powershell:`), popups, downloads, and external navigations.
  - **InPrivate Ephemeral Profiles**: Zero persistence of student credentials, browsing history, or keystrokes.

- **Exam Lifecycle & Resilience**:
  - Native countdown timer running independently of JavaScript timers.
  - Atomic session persistence (`session.json`) enabling seamless resume in case of machine power glitches or reboots.
  - Configurable timer pause policy during verified network drops.

- **Administrative Diagnostics & Audit Logging**:
  - Built-in `Klyro.Admin` CLI utility for pre-flight health checks, configuration validation, and diagnostic telemetry export.
  - High-throughput asynchronous audit logger (`audit.log`) with automatic redaction of sensitive credentials and tokens.

---

## Solution Architecture

```
                                +--------------------------------------+
                                |      Windows OS (Layer 1 & 2)        |
                                |  Shell Launcher v2 + WDAC Lockdown   |
                                +-------------------+------------------+
                                                    |
+---------------------------------------------------+---------------------------------------------------+
| Klyro.App (WinUI 3 Host / Layer 3)                                                                    |
|                                                                                                       |
|  +-------------------------------+   +-----------------------------+   +---------------------------+  |
|  |       Exam Timer & State      |   |   Pre-flight Health Checker |   |   Audit Logger & Redactor |  |
|  +---------------+---------------+   +--------------+--------------+   +-------------+-------------+  |
|                  |                                  |                                |                |
|  +---------------+----------------------------------+--------------------------------+-------------+  |
|  |                                  Recovery Controller (Layer 4)                                  |  |
|  +--------------------+---------------------------------------------------+------------------------+  |
|                       |                                                   |                           |
|       +---------------+---------------+                   +---------------+---------------+           |
|       |     ExamWebView Control       |                   |    RecoveryWebView Modal      |           |
|       |  (Target: codechef.com)       |                   |  (Target: 172.16.103.254:8090)|           |
|       |  - Canonical Allowlist Guard  |                   |  - Single-Origin Portal Guard |           |
|       |  - Downloads/Popups Blocked   |                   |  - Ephemeral Memory Profile   |           |
|       |  - InPrivate Exam Profile     |                   |  - Verification Monitor       |           |
|       +-------------------------------+                   +-------------------------------+           |
|                                                                                                       |
+-------------------------------------------------------------------------------------------------------+
| Network State Machine (Layer 4)                                                                       |
| [Gateway Probe] ---> [Captive Portal Probe] ---> [Internet 204 Probe] ---> [Assessment Endpoint Probe]|
+-------------------------------------------------------------------------------------------------------+
```

---

## Repository Structure

```
Klyro/
|-- config/
|   |-- default.json               # Default production exam configuration
|   |-- development.json           # Development profile with debug logging
|   `-- exam.example.json          # Template for university custom exams
|-- docs/
|   |-- Architecture.md            # Comprehensive architecture & layered security specification
|   |-- ThreatModel.md             # STRIDE threat model & mitigations (T1-T15)
|   |-- NetworkRecovery.md         # Deterministic multi-probe state machine specification
|   |-- TestingMatrix.md           # Failure scenarios, chaos testing & keyboard policy
|   |-- Deployment.md              # IT Admin guide: Shell Launcher, Intune, WDAC, Code Signing
|   `-- Troubleshooting.md         # Invigilator & proctor quick triage guide
|-- src/
|   |-- Klyro.Core/                # Portable domain logic, network probes, guards, logging (.NET 8)
|   |-- Klyro.App/                 # WinUI 3 desktop application shell & dual-WebView host
|   |-- Klyro.Admin/               # Command-line administrative diagnostics & telemetry tool
|   `-- Klyro.Installer/           # Kiosk provisioning, Shell Launcher v2, WDAC & teardown scripts
`-- tests/
    |-- Klyro.SecurityTests/       # 51 tests: URL allowlist, protocol blocks, download guards
    |-- Klyro.NetworkTests/        # 15 tests: Probe evaluation, state transitions, damping
    |-- Klyro.RecoveryTests/       # 16 tests: Dual-webview modal lifecycle, verification probes
    `-- Klyro.UnitTests/           # 26 tests: Preflight checks, timer, session recovery, audit logger
```

---

## Building and Running

### Prerequisites
- Windows 10 (Build 19041+) or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Microsoft Edge WebView2 Evergreen Runtime

### 1. Build the Entire Solution
```powershell
dotnet build Klyro.sln -c Release
```

### 2. Run the Test Suite (108 Tests)
```powershell
dotnet test Klyro.sln -v minimal
```

All 108 tests validate URL canonicalization, lookalike domain rejection, protocol guarding, multi-probe damping, dual-profile isolation, and crash recovery.

### 3. Run Administrative Diagnostics
```powershell
& "$HOME\.dotnet\dotnet.exe" "src\Klyro.Admin\bin\Release\net8.0\Klyro.Admin.dll" diagnose
```

---

## Configuration Reference

Klyro is configured via JSON. Key settings in `config/default.json`:

```json
{
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

## University Workstation Kiosk Deployment

1. **Deploy Workstation Kiosk**:
   Execute from an elevated PowerShell administrator terminal:
   ```powershell
   cd src\Klyro.Installer\Provisioning
   .\Deploy-KlyroKiosk.ps1 -ExamUser "KLYRO-EXAM"
   ```
2. **Revert Workstation to Normal**:
   ```powershell
   .\Remove-KlyroKiosk.ps1 -ExamUser "KLYRO-EXAM" -DeleteUser $true -RemoveBinaries $true
   ```

Refer to [`docs/Deployment.md`](docs/Deployment.md) for full instructions on Microsoft Intune, MECM, and Smart App Control (SAC) code signing.

---

## Documentation Links

- [System Architecture & Layered Security](docs/Architecture.md)
- [STRIDE Threat Model & Mitigations](docs/ThreatModel.md)
- [Deterministic Network Recovery Engine](docs/NetworkRecovery.md)
- [Testing Matrix & Failure Scenarios](docs/TestingMatrix.md)
- [Enterprise Deployment & Provisioning Guide](docs/Deployment.md)
- [Invigilator Triage & Troubleshooting Guide](docs/Troubleshooting.md)

---

## License
Confidential & Proprietary. Developed for university secure assessment environments.
