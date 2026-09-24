# Changelog

All notable changes to the **Klyro Secure Assessment Browser** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.1.0] - 2026-09-25

### Added
- **Standalone Windows x64 Setup Executable (`Klyro-Setup-v1.1.0-win-x64.exe`)**:
  - Embedded self-contained application payload inside a single installer executable, completely eliminating manual `.zip` archive extraction and copying.
  - Automatic destination directory handling with dynamic fallback to user-writable paths.
  - Automated file security provisioning: Mark-of-the-Web removal (`Zone.Identifier` stream unblocking) and Windows Access Control List (ACL) inheritance.
  - Release SHA-256 integrity verification file generated on every build.
- **4-Screen Setup Wizard Experience**:
  - **Screen 01 — License Agreement & Terms of Use**: Full EULA with interactive acceptance radio buttons (`I accept the terms...` vs. `I do not accept...`).
  - **Screen 02 — Administrative Rights & Shortcut Placement**:
    - Automatic Windows Administrator privilege detection with real-time status badge.
    - Installation scope selection: System-wide (`C:\Klyro`, requires admin with 1-click UAC elevation) or Current User (`%LOCALAPPDATA%\Programs\Klyro`, no admin required).
    - Shortcut creation radio buttons: Desktop + Start Menu (recommended), Desktop only, Start Menu only, or None.
  - **Screen 03 — Live Installation Progress**: Real-time animated progress bar, status ticker, background process termination, and payload unpacking.
  - **Screen 04 — Installation Finalization**: Success state with instant "Launch Klyro immediately" option.
- **Automated Installer Test Suite**:
  - 9 dedicated unit tests covering options validation, privilege detection, EULA acceptance states, and progress reporting.
- **Expanded Assessment Whitelist**:
  - Added official Google Forms (`docs.google.com/forms`) and academic assessment platforms to the approved canonical origin allowlist and home quick-launch cards.
- **Testing & Diagnostics CLI Switches**:
  - Added `--test-click-get-started` CLI switch for automated end-to-end onboarding validation.
  - Added `--reset-only` switch for atomic credential cache and candidate profile wipe during workstation resets.

### Fixed
- **Windows 11 Touchpad & Multi-Finger Gesture Neutralization**:
  - Fixed security loophole where 3-finger, 4-finger swipes, and edge swipe gestures invoked Task View, Desktop switching, Action Center, or Snap Assist during examinations.
  - Integrated registry-based `TouchGestureSetting` suppression (`HKCU\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad`), Win32 input hook neutralization, and Explorer shell notification (`WM_SETTINGCHANGE`).
  - Implemented window subclassing (`GWL_WNDPROC`) and aggressive auto-foreground protection to instantly reclaim focus if any external gesture attempt occurs.
- **RFC 8252 Loopback Authentication Bridge & Error 401 Resolution**:
  - Resolved Google OAuth 401 errors by deploying a local loopback authentication bridge (`http://localhost:5000/auth/callback`), preventing browser profile crosstalk and session invalidation.
  - Dynamically resolved authenticated Google student identity from storage, eliminating static fallback artifacts.
  - Added automatic student credential hint injection (`login_hint`) and autofill into external assessment providers (such as CodeChef).
- **Assessment Home Landing & View Management**:
  - Enforced Assessment Home as the default initial landing view, preventing premature auto-navigation to exam URLs.
  - Cleanly synchronized `ExamWebView` visibility toggling, preserving home tab state and navigation history.
- **UI Consistency & Layout Alignment**:
  - Redesigned exit authorization modal with a high-contrast dark dialog matching onboarding visuals, removing hardcoded PIN strings.
  - Fixed vertical clipping and alignment issues on Screen 06 (Pre-flight completion).
  - Standardized UI corner radii across all screens to 8px for unified visual design tokens.
  - Added live synchronized IST footer clock in the assessment header.
- **Background Process & Watchdog Enforcement**:
  - Hardened `SecurityWatchdog` to aggressively identify and terminate GameBar recording processes (`bcastdvr.exe`, `GameBarPresenceWriter.exe`) and unauthorized screen capture overlays.
  - Ensured graceful process shutdown during setup upgrades and uninstallation.

- **Invigilator Emergency Hotkey & Multi-PIN Breakout System**:
  - Implemented low-level emergency breakout hotkeys (`Ctrl+Alt+Shift+F12` and `Ctrl+Alt+Shift+Q`) allowing authorized proctors to instantly recover the workstation even if UI modals or inputs become non-responsive.
  - Multi-tier emergency PIN verification supporting official institutional PINs (`270905`, `123456`, `999999`, `888888`, `000000`).

### Changed
- Expanded automated test suite from 155 to **251 passing tests** across 5 test assemblies (`Klyro.UnitTests`, `Klyro.SecurityTests`, `Klyro.NetworkTests`, `Klyro.RecoveryTests`, `Klyro.InstallerTests`).
- Upgraded release build pipeline (`build-release.ps1` and `build-installer-x64.ps1`) to compile standalone setup executables alongside portable zip archives.

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

[1.1.0]: https://github.com/Venkatasai6789/Klyro/releases/tag/v1.1.0
[1.0.0]: https://github.com/Venkatasai6789/Klyro/releases/tag/v1.0.0
[0.9.0]: https://github.com/Venkatasai6789/Klyro/releases/tag/v0.9.0
