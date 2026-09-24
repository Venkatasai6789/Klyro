namespace Klyro.Installer.Services;

public static class TermsAndConditionsText
{
    public const string FullText = """
KLYRO SECURE ASSESSMENT BROWSER — SOFTWARE LICENSE & TERMS OF USE
Version 1.0.0 | Effective September 2026
Copyright (c) 2026 P. Venkata Sai / Klyro Project Contributors

IMPORTANT — READ CAREFULLY BEFORE INSTALLING OR USING THIS SOFTWARE:
This End User License Agreement ("Agreement") is a legal agreement between you (either an individual student, test candidate, or institution) and the Klyro Project Contributors regarding the Klyro Secure Assessment Browser ("Software"). By selecting "I accept the terms in the License Agreement" and proceeding with the installation, you agree to be bound by the terms of this Agreement.

================================================================================
1. GRANT OF LICENSE (MIT LICENSE COMPLIANCE)
================================================================================
Permission is hereby granted, free of charge, to any person obtaining a copy of this Software and associated documentation files, to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, subject to the conditions that the copyright notice and this permission notice appear in all copies or substantial portions of the Software.

================================================================================
2. PURPOSE & ASSESSMENT INTEGRITY REQUIREMENTS
================================================================================
Klyro is an enterprise-grade lockdown browser engineered specifically for academic examinations, technical evaluations, and competitive coding tests. When active during an examination session:
a. The application operates in an exclusive full-screen presentation mode designed to enforce academic fairness.
b. The system temporarily suppresses non-permitted keyboard combinations (including Windows keys, Alt-Tab, task-switching sequences, and game overlays) to prevent unauthorized collaboration or browser hopping.
c. Exiting an active examination requires authorized Invigilator PIN verification to prevent premature termination.

================================================================================
3. DUAL-WEBVIEW RESILIENCE & ZERO CODE-LOSS GUARANTEE
================================================================================
Klyro utilizes a Dual-WebView State Isolation Architecture:
a. Primary Exam WebView: Renders your official examination portal within a strict domain allowlist.
b. Secondary Recovery WebView: Deployed on-demand in an isolated modal solely when volatile campus Wi-Fi drops or captive portal authentication leases expire.
c. Your in-progress examination source code, responses, and session state are preserved in-place in the frozen primary WebView and will never be cleared, reloaded, or wiped during network disruptions.

================================================================================
4. PRIVACY, TELEMETRY & ZERO EXFILTRATION COMMITMENT
================================================================================
Klyro is engineered with a strict privacy-first architecture:
a. Zero Background Profiling: Klyro does not track, monetize, or harvest personal background browsing activity.
b. Ephemeral Local Caching: Diagnostic logs, onboarding state, and profile session data are stored locally in %LOCALAPPDATA%\Klyro and can be reviewed or reset by the user at any time.
c. No Hidden Keystroke Logging: The low-level keyboard hook acts purely as an access-control gate during active exams; typed examination input is dispatched directly to the browser editor and is never recorded to disk or external servers.

================================================================================
5. SYSTEM PERMISSIONS & LOCAL DEPLOYMENT
================================================================================
By proceeding with the installation:
a. Setup will deploy self-contained binaries to your designated destination folder (default: C:\Klyro).
b. Setup will remove Windows Mark-of-the-Web (MOTW) zone identifiers on the downloaded binaries to ensure smooth, unblocked execution.
c. Setup will create Windows Desktop and/or Start Menu shortcuts according to your chosen shortcut preferences.

================================================================================
6. DISCLAIMER OF WARRANTIES & LIMITATION OF LIABILITY
================================================================================
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES, OR OTHER LIABILITY ARISING FROM, OUT OF, OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
""";
}
