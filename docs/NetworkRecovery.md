# Klyro: Network Engine & Captive Portal Recovery Specification

## 1. Network Architecture Overview

University laboratory networks frequently subject client workstations to unexpected Wi-Fi session expirations, DHCP lease renegotiations, and high-density radio contention. Standard browsers respond to captive portal session expiration by redirecting the active browser tab to the captive portal login page (`http://172.16.103.254:8090/`), instantly wiping out uncommitted student code, unsaved editor state, and contest DOM elements.

Klyro solves this with a **decoupled, multi-probe network engine** and an **isolated dual-WebView recovery architecture**.

```
                           NETWORK DIAGNOSIS
                                   │
                  ┌────────────────┴────────────────┐
                  ▼                                 ▼
           Probe 1: Gateway                  Probe 2: Portal
           (172.16.x.x socket)              (172.16.103.254:8090)
                  │                                 │
                  ├────────────────┬────────────────┤
                  ▼                ▼                ▼
           Probe 3: 204      Probe 4: Chef      Matrix Check
           (External Ping)   (codechef.com)         │
                  │                │                │
                  └────────────────┴────────────────┘
                                   │
                                   ▼
                       State Machine Evaluation
                                   │
    ┌──────────────┬───────────────┼───────────────┬──────────────┐
    ▼              ▼               ▼               ▼              ▼
 [ONLINE]     [DEGRADED]      [PORTAL_REQ]     [OFFLINE]    [PROVIDER_DOWN]
    │              │               │               │              │
 Normal       Quiet retry     Modal overlay   Quiet banner   Notice modal
```

---

## 2. Multi-Probe Specification

Klyro relies on four deterministic probe signals polled concurrently at configurable intervals (default: `probeIntervalMs = 3000`):

| Probe ID | Target Endpoint | Method | Success Criteria | Purpose |
|---|---|---|---|---|
| **Probe 1: Gateway** | Default Gateway IP (e.g. `172.16.100.1`) | TCP socket connect (port 53/80) / ICMP Ping | Connected within `500ms` | Verifies physical link and local Wi-Fi router association. |
| **Probe 2: Captive Portal** | `http://172.16.103.254:8090/` | HTTP GET | HTTP 200/302/Login form received within `1000ms` | Verifies the campus captive portal controller is accessible. |
| **Probe 3: External Internet** | `http://clients3.google.com/generate_204` (fallback: `http://www.msftconnecttest.com/connecttest.txt`) | HTTP GET | HTTP 204 No Content (or expected token) within `1500ms` | Verifies uninhibited routing to the global Internet without HTTP interception. |
| **Probe 4: Assessment Provider** | `https://www.codechef.com/api/status` or contest origin | HTTP HEAD / GET | HTTP 200/302/304 within `2000ms` | Verifies the contest platform servers are active and reachable. |

---

## 3. Network State Machine Specification

### 3.1. States
- **`STARTING`**: Initializing network adapters, resolving gateway, establishing baseline probes.
- **`CHECKING`**: Probing all four signals concurrently.
- **`ONLINE`**: Gateway reachable, external internet verified (HTTP 204), CodeChef reachable. Student experiences seamless exam.
- **`DEGRADED`**: Intermittent packet loss or high latency on external probe, but gateway remains reachable and portal is not intercepting. No UI popup shown to avoid student panic; background probe rate temporarily increases.
- **`PORTAL_REQUIRED`**: Gateway reachable, Captive Portal reachable, External Internet blocked/redirected. Triggers modal recovery overlay.
- **`OFFLINE`**: Gateway unreachable or local Wi-Fi adapter disconnected. Portal is NOT shown because it cannot be reached.
- **`ASSESSMENT_SERVICE_UNAVAILABLE`**: Gateway reachable, External Internet reachable (HTTP 204), but CodeChef is failing. Informs student without blaming Wi-Fi.

### 3.2. State Transition Matrix & Decision Algorithm

```csharp
public NetworkState EvaluateProbes(ProbeResults results)
{
    // Step 1: Check Physical / Gateway link
    if (!results.GatewayReachable)
    {
        return NetworkState.OFFLINE;
    }

    // Step 2: Check External Internet
    if (results.InternetReachable)
    {
        // Internet is healthy. Check Assessment Service.
        if (results.AssessmentProviderReachable)
        {
            return NetworkState.ONLINE;
        }
        else
        {
            return NetworkState.ASSESSMENT_SERVICE_UNAVAILABLE;
        }
    }

    // Step 3: Internet is failing. Determine if Captive Portal is active.
    if (results.CaptivePortalReachable)
    {
        // Prevent flapping: Require consecutive failures before popping UI
        if (ConsecutiveFailures >= Config.FailureThreshold) // default: 3
        {
            return NetworkState.PORTAL_REQUIRED;
        }
        else
        {
            return NetworkState.DEGRADED;
        }
    }

    // Gateway is up, but neither Internet nor Portal are reachable
    return NetworkState.DEGRADED;
}
```

---

## 4. Dual-WebView Recovery Lifecycle

### Step-by-Step Recovery Sequence

```mermaid
sequenceDiagram
    participant Student
    participant Shell as Klyro Shell UI
    participant ExamWV as Exam WebView (CodeChef)
    participant NetEng as Network Engine
    participant RecWV as Recovery WebView (Modal)
    participant Portal as Campus Portal (172.16.103.254)

    Note over ExamWV: Student coding actively in CodeChef
    NetEng->>NetEng: Wi-Fi session drops; External probe fails
    NetEng->>NetEng: Consecutive failures >= 3; Portal verified reachable
    NetEng->>Shell: OnNetworkStateChanged(PORTAL_REQUIRED)
    
    Shell->>ExamWV: Dim opacity, pause user input (DO NOT RELOAD)
    Shell->>RecWV: Instantiate Modal Overlay (http://172.16.103.254:8090/)
    Shell->>Student: Display "University Network Login Required"
    
    Student->>RecWV: Enter student credentials & click Connect
    RecWV->>Portal: POST credentials
    Portal-->>RecWV: HTTP 200 / Success redirect
    
    RecWV->>Shell: OnAuthenticationSubmitted()
    Shell->>Student: Display "Authenticating... Verifying Connection"
    
    Shell->>NetEng: TriggerImmediateProbeAsync()
    NetEng->>Portal: Check probe (Pass)
    NetEng->>NetEng: Probe 204 Internet (Pass)
    NetEng->>ExamWV: Probe CodeChef (Pass)
    
    NetEng->>Shell: OnNetworkStateChanged(ONLINE)
    Shell->>RecWV: Dispose & Dismiss Modal Overlay
    Shell->>ExamWV: Restore full opacity, focus editor
    Shell->>Student: Subtle notification "Connection Restored — Resuming Assessment"
```

---

## 5. Failure Scenarios & Edge Case Handling

### 5.1. Authentication Fails (Wrong Password)
- **Behavior**: The captive portal page displays an error message (e.g. "Invalid credentials").
- **Klyro Handling**: The recovery modal remains open. The student can re-enter their password and retry. No external navigation is permitted.

### 5.2. Captive Portal is Down / Unresponsive
- **Behavior**: Gateway is reachable, but `http://172.16.103.254:8090/` times out or returns HTTP 500.
- **Klyro Handling**: Klyro displays:
  > **Network Infrastructure Unavailable**
  > The university authentication portal cannot be reached.
  > Your exam session is safely preserved. Please notify your invigilator.
  > `[ Check Again ]`

### 5.3. Portal Login Succeeds but Internet Routing is Delayed
- **Behavior**: Portal responds with success, but upstream radius/firewall rules take 5–10 seconds to update route tables.
- **Klyro Handling**: Klyro does NOT dismiss the modal immediately upon button click. It displays "Authenticating... restoring connection" and polls the external probe every 1000ms up to a bounded `recoveryTimeoutMs` (default: 30 seconds). Only when `Probe 3 (204)` succeeds is the modal closed.

### 5.4. Contest Submission During Degradation
- **Behavior**: Student clicks "Submit" in CodeChef right as a packet drop occurs.
- **Klyro Handling**: Klyro **NEVER** reloads the WebView page. Automatic reloads during coding assessments risk losing student scratchpad buffers. The user is prompted with an unobtrusive native toast:
  > **Submission Delayed**
  > Connection is currently restoring. Your current code has been retained. Please retry submission once the indicator turns green.
