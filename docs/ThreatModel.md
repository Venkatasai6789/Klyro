# Klyro: Threat Model & Security Specification

## 1. Security Philosophy & Scope

Klyro enforces an uncompromising security posture designed specifically for university coding examinations:
- **Security Violations Fail Closed**: If an unknown URL, unauthorized scheme, unvetted popup, or suspicious redirect is encountered, access is immediately blocked.
- **Network Failures Fail Gracefully**: A network interruption, high latency, or captive-portal session expiration must never terminate the examination or discard student work. The environment pauses and initiates controlled recovery.
- **Physical Device Boundary**: Klyro defends the managed Windows workstation and its software runtime. It assumes the device is university-owned and enrolled in management policies. (Unauthorized physical hardware or external mobile devices remain the domain of human invigilators).

---

## 2. Threat Classification Matrix (STRIDE & T1–T15)

| Threat ID | Threat Name | STRIDE | Attack Vector / Scenario | Primary Control | Secondary Fallback | Failure Policy |
|---|---|---|---|---|---|---|
| **T1** | External Website Access | Information Disclosure / Spoofing | Student inputs URL or clicks hyperlink to Google, ChatGPT, Chegg, or external forum. | `NavigationGuard` strict allowlist verification. | `FrameNavigationGuard` on child frames. | **Fail Closed (BLOCK)** |
| **T2** | Other Browser Execution | Elevation of Privilege | Student attempts to launch Edge, Chrome, Firefox from disk or shortcut. | Windows App Control (WDAC) binary allowlist. | Shell Launcher v2 replaces Windows desktop shell. | **Fail Closed (BLOCK)** |
| **T3** | Developer Tools Inspection | Tampering / Information Disclosure | Student opens F12 / DevTools to inspect DOM, tamper with JS, or access network tab. | WebView2 `AreDevToolsEnabled = false`. | Keyboard filter intercepts F12 shortcut. | **Fail Closed (BLOCK)** |
| **T4** | Popups & Secondary Windows | Information Disclosure | CodeChef or malicious link triggers `window.open()` or `target="_blank"`. | `PopupGuard` intercepts `NewWindowRequested` (`e.Handled = true`). | Window creation API hooks in native shell. | **Fail Closed (BLOCK)** |
| **T5** | Unauthorized Downloads | Tampering / Elevation of Privilege | Student attempts to download executable, script, or browse local filesystem via Save As. | `DownloadGuard` cancels `DownloadStarting` (`e.Handled = true`). | Read-only / restricted local file access in kiosk account. | **Fail Closed (BLOCK)** |
| **T6** | OS Application Launch | Elevation of Privilege | Page triggers `calc.exe`, `powershell.exe`, or `cmd.exe` via custom protocol or shell link. | `ProtocolGuard` cancels execution of unregistered schemes. | App Control (WDAC) denies executable startup. | **Fail Closed (BLOCK)** |
| **T7** | System Shortcut Escape | Elevation of Privilege | Student presses `Win+D`, `Alt+Tab`, `Win+R`, `Ctrl+Shift+Esc`, or `Win+G`. | Windows Assigned Access / Shell Launcher v2 & Keyboard Filter. | WinUI 3 Window hook suppresses remaining key chords. | **Fail Closed (BLOCK)** |
| **T8** | Temporary Network Loss | Denial of Service | Wi-Fi packet drops, antenna flutter, or router degradation. | `NetworkStateMachine` transitions to `DEGRADED`. | CodeChef WebView remains alive; retry timer begins. | **Fail Gracefully (RETRY)** |
| **T9** | Captive Portal Expiration | Denial of Service | University Wi-Fi session drops; HTTP redirects to `172.16.103.254:8090`. | `CaptivePortalDetector` triggers isolated `RecoveryWebViewHost` modal overlay. | `ExamWebViewHost` remains paused, untouched, preserving code. | **Fail Gracefully (RECOVER)** |
| **T10** | Klyro Application Crash | Denial of Service | Unhandled exception terminates `Klyro.App.exe`. | Windows Shell Launcher automatically restarts Klyro. | `SessionStore` reads cached session state and resumes contest. | **Fail Gracefully (RESTORE)** |
| **T11** | WebView2 Process Crash | Denial of Service | Out-of-memory or GPU crash terminates WebView2 renderer process. | `ProcessFailed` event catches fault, disposes host, re-initializes runtime. | Restores active contest URL from session store. | **Fail Gracefully (RESTORE)** |
| **T12** | Workstation Reboot / Power Loss | Denial of Service | Workstation reboots or power cycles during active contest. | Windows auto-logon to `KLYRO-EXAM` + Shell Launcher auto-start. | Invigilator confirmation / auto-resume per university policy. | **Fail Gracefully (RESTORE)** |
| **T13** | Assessment Provider Outage | Denial of Service | CodeChef servers fail while campus Wi-Fi and captive portal remain healthy. | Multi-probe isolates outage to CodeChef; displays institutional notice. | Prohibits auto-refreshing; preserves unsubmitted work. | **Fail Gracefully (PAUSE)** |
| **T14** | Malformed Portal Redirect | Spoofing / Tampering | Captive portal attempts redirect to external search engine or malicious URL. | `RecoveryWebViewHost` enforces independent allowlist (`172.16.103.254:8090/*`). | Cancel navigation if origin is not verified auth target. | **Fail Closed (BLOCK)** |
| **T15** | Unauthorized Protocol Launch | Elevation of Privilege | Exploit payload triggers `file:///`, `ms-settings:`, or `ms-windows-store:`. | `ProtocolGuard` intercepts and cancels non-http/https protocol calls. | Windows URI Association lockdown in standard user profile. | **Fail Closed (BLOCK)** |

---

## 3. Deep-Dive Threat Vector Analysis

### 3.1. T1 & T14: Origin Allowlisting & Lookalike Domains
Attackers may attempt domain homoglyph attacks or crafty subdomains:
- `https://www.codechef.com` (ALLOWED)
- `https://codechef.com.attacker.org` (BLOCKED)
- `https://evil-codechef.com` (BLOCKED)
- `https://codechef.evil.com` (BLOCKED)
- `http://www.codechef.com` (BLOCKED - insecure scheme, HTTPS required)

**Mitigation in `AllowlistManager`**:
```csharp
// Strict Canonical Origin Comparison:
// Scheme, Host, and Port must match the configured trusted origin set.
var uri = new Uri(targetUrl);
if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
{
    // Exception only for captive portal configured HTTP endpoint
    if (!IsConfiguredCaptivePortal(uri)) return false;
}

bool matches = _allowedOrigins.Any(allowed =>
    string.Equals(allowed.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
    string.Equals(allowed.Host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
    allowed.Port == uri.Port);
```

### 3.2. T7: OS Shortcut Escape & Shell Replacement
Traditional exam software relies on Win32 keyboard hooks (`SetWindowsHookEx(WH_KEYBOARD_LL)`). This approach is inherently flawed on Windows because:
1. `Ctrl+Alt+Delete` and `Win+L` are captured directly by `winlogon.exe` and cannot be trapped in user mode.
2. `Win+G` (Game Bar) and `Win+U` (Accessibility) can bypass user-space low-level hooks under thread contention.
3. If the application thread hangs, the Windows keyboard hook times out (LowLevelHooksTimeout) and Windows unhooks it, exposing the OS.

**Klyro Defense Architecture**:
1. **Shell Replacement**: The Windows Explorer shell (`explorer.exe`) is not running. There is no taskbar, start menu, desktop icons, or notification center.
2. **Standard User Profile**: The account cannot open Task Manager (`DisableTaskMgr` registry policy) or switch users.
3. **Application Control (WDAC)**: Even if a student somehow invoked Run dialog, no unauthorized binary can execute.

### 3.3. T9 & T14: Captive Portal Recovery Security
A common vulnerability in exam browsers is that allowing captive portal login opens an unrestricted browser window, enabling students to search Google, view notes, or access AI tools.

**Klyro Defense Architecture**:
1. **Modal Separation**: The recovery window is a strictly contained modal overlay inside Klyro, not a new OS window or browser tab.
2. **Dedicated Recovery Profile**: The recovery WebView2 runs in a completely separate User Data Directory (`Profiles/Recovery`) from the exam WebView. Cookies and cached tokens from the contest cannot leak into the portal session.
3. **Recovery Origin Restriction**: The recovery WebView allows ONLY `http://172.16.103.254:8090/*`. If the portal contains links to external terms of service, social logins, or search engines, those navigations are intercepted and blocked by `RecoveryNavigationGuard`.
4. **Independent Re-Verification**: Clicking "Login" does not dismiss the recovery screen. Dismissal happens ONLY when the background `ConnectivityProbe` independently receives HTTP 204 from external endpoints and reaches the CodeChef contest endpoint.

---

## 4. Privacy & Credential Boundary

Klyro adheres strictly to university data minimization and ethical assessment standards:
1. **Zero Password Storage**: Neither university Wi-Fi credentials nor CodeChef passwords ever touch Klyro’s logging pipeline, session files, or IPC channels. They are held solely in browser process memory while authenticating.
2. **No Blanket Keystroke Logging**: Student typing is handled natively by the CodeChef code editor in the WebView. Klyro logs audit milestones (e.g. `PORTAL_AUTH_STARTED`, `EXAM_RESUMED`), never individual key events.
3. **No Local File Harvesting**: Klyro does not scan, index, or upload local student documents.
4. **Encrypted Session Recovery Store**: Only essential exam state is saved locally:
   - `ExamId` (GUID)
   - `SessionId` (GUID)
   - `MachineId` (Hash of local machine SID)
   - `State` (Current ExamState enum)
   - `StartTimeUtc`
   - `LastActiveContestUrl`
   - `LastSuccessfulNetworkUtc`
