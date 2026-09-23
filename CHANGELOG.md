# Changelog

All notable changes to the **Klyro Secure Assessment Browser** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.0.0] - 2026-09-23

### Added
- **Complete 4-Screen Enterprise Onboarding Sequence**:
  - **Screen 01 — Startup & Diagnostic Pipeline**:
    - Uncompressed branding artwork adhering to native 1.5 aspect ratio.
    - Centered Klyro brand mark and 380 DIP interactive diagnostic container.
    - High-precision startup pipeline validating system environment, network connectivity, and security watchdog readiness (10% $\rightarrow$ 100% Ready).
  - **Screen 02 — Welcome Experience**:
    - Feature cards highlighting Secure Environment, Distraction-Free Kiosk, and Trusted Instructions.
    - Unified Klyro brand gradient (`#5DCBFA` $\rightarrow$ `#116FF9`).
    - Primary action button (`Get Started →`) with standardized 8px corner radius.
  - **Screen 03 — Google Account Setup & Authentication**:
    - 50/50 split design with custom 3D hardware and security credential artwork.
    - Official 4-color Google 'G' vector sign-in button.
    - Native embedded WebView2 authentication running under a shared persistent Chromium user profile.
    - Seamless session cookie synchronization between authentication and assessment modes.
  - **Screen 04 — Google Account Connected**:
    - Appears exclusively upon verified Google authentication completion.
    - 100% dynamic student identity presentation (`DisplayName`, `Email`, `PhotoUrl`).
    - Circular avatar (96x96 DIPs) with 2px cyan border (`#38BDF8`) and overlapping Google "G" vector badge.
    - Culture-aware deterministic initials fallback (e.g. `PV`, `JD`, `AS`) for accounts without profile photos.
    - Primary `Continue →` action navigating to the assessment view and recording onboarding completion.
    - `Use a different account` interactive flow leveraging Google's account chooser with safe session restoration if cancelled.

- **Institutional Network Fallback & Proctor Bypass**:
  - Zero raw or cryptic browser error codes (`ERR_NAME_NOT_RESOLVED`, `-105`, etc.).
  - Plain-English diagnostic guidance for disconnected links, captive Wi-Fi portals, and server timeouts.
  - Built-in **Campus Wi-Fi Portal** browser modal enabling students to clear university captive logins without leaving the application.
  - **Invigilator Offline Authorization PIN Modal** allowing test administrators to authorize workstations when WAN/Google is inaccessible.
  - Automatic default-browser authentication return detection with live progress notification.

- **Dual-WebView Isolation & State Preservation Engine**:
  - Primary `ExamWebView` hosting assessment platforms in a strictly locked canonical-allowlist sandbox.
  - Secondary `RecoveryWebView` instantiated only during captive portal expirations.
  - In-place browser freezing to prevent student code loss or editor reloads during wireless drops.

- **Hardened Kiosk & Anti-Cheating Architecture**:
  - Windows Shell Launcher v2 configuration replacing `explorer.exe` on designated exam user accounts.
  - Windows Defender Application Control (WDAC) profile whitelisting trusted binaries and WebView2 dependencies.
  - Low-level keyboard hook intercepting task switching (`Alt+Tab`, `Win`, `Ctrl+Esc`, `Ctrl+Shift+Esc`).
  - Dedicated background thread `SecurityWatchdog` scanning for unauthorized processes, screen recorders, and virtual machine hypervisors.
  - Fail-closed protocol guards blocking `file:`, `mailto:`, `ms-settings:`, `cmd:`, `powershell:`, and local execution.

- **High-Throughput Audit Logging & Telemetry**:
  - Asynchronous thread-safe audit logging channel (`klyro_audit_*.jsonl`).
  - Automatic redaction of sensitive credentials, authentication tokens, and personal parameters.
  - Pre-flight diagnostic CLI (`Klyro.Admin`) for institutional IT verification.

- **Automated CI/CD & GitHub Release Pipeline**:
  - GitHub Actions workflow (`.github/workflows/release.yml`) for automated testing, compilation, and asset publishing.
  - Local packaging script (`build-release.ps1`) for generating standalone win-x64 zip archives with SHA256 verification.

### Changed
- Refactored `OnboardingStateStore` to track authenticated student metadata with atomic file swaps.
- Standardized UI corner radii across all screens to 8px for visual consistency.
- Expanded automated test suite from 108 to 155 comprehensive tests across unit, security, network, and recovery domains.

---

## [0.9.0] - 2026-09-20

### Added
- Core dual-WebView prototype and basic URL allowlist enforcement.
- Initial captive portal detection state machine.
- Local exam configuration loader (`config/default.json`).
- Shell Launcher deployment scripts for Windows 10/11 Enterprise and Education editions.

---

[1.0.0]: https://github.com/Venkatasai6789/Klyro/releases/tag/v1.0.0
[0.9.0]: https://github.com/Venkatasai6789/Klyro/releases/tag/v0.9.0
