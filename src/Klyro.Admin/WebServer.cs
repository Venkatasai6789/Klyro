using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Klyro.Core.Incidents;
using Klyro.Core.Logging;
using Klyro.Core.Security.RemoteAccess;

namespace Klyro.Admin;

public sealed record HeartbeatRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("examId")] string? ExamId = null,
    [property: JsonPropertyName("email")] string? Email = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("examState")] string? ExamState = null,
    [property: JsonPropertyName("securityState")] string? SecurityState = null);

public sealed record ReviewSubmissionRequest(
    [property: JsonPropertyName("explanation")] string? Explanation = null);

public sealed record RecheckAuthorizationRequest(
    [property: JsonPropertyName("authorizedBy")] string? AuthorizedBy = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public static class WebServer
{
    private static readonly ConcurrentDictionary<string, (HeartbeatRequest Request, DateTimeOffset LastSeen)> ActiveSessions = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRouting();

        var app = builder.Build();

        // ════════════════════════════════════════════════════════════════
        // API: METRICS
        // ════════════════════════════════════════════════════════════════
        app.MapGet("/api/metrics", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var liveSessions = ActiveSessions.Values
                .Where(s => now - s.LastSeen < TimeSpan.FromSeconds(60))
                .Select(s => s.Request)
                .ToList();

            var incidents = IncidentManager.Instance.GetAllIncidents();

            int activeUsers = liveSessions.Select(s => s.Email).Where(e => !string.IsNullOrEmpty(e)).Distinct().Count();
            int activeBrowserSessions = liveSessions.Count;
            int activeExams = liveSessions.Count(s => s.ExamState.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase));
            int openIncidents = incidents.Count(i => !i.IsResolved);
            int restrictedSessions = liveSessions.Count(s => s.ExamState.Equals("RESTRICTED", StringComparison.OrdinalIgnoreCase));
            int pendingReviews = incidents.Count(i => i.ReviewStatus.Equals("PENDING_REVIEW", StringComparison.OrdinalIgnoreCase));
            int monitorsUnavailable = incidents.Count(i => i.Decision == SecurityPolicyDecision.MonitorUnavailable);

            return Results.Ok(new
            {
                activeUsers,
                activeBrowserSessions,
                activeExams,
                openIncidents,
                restrictedSessions,
                pendingReviews,
                monitorsUnavailable,
                timestamp = now
            });
        });

        // ════════════════════════════════════════════════════════════════
        // API: SESSIONS & HEARTBEAT
        // ════════════════════════════════════════════════════════════════
        app.MapGet("/api/sessions", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var list = ActiveSessions.Values
                .OrderByDescending(s => s.LastSeen)
                .Select(s => new
                {
                    s.Request.SessionId,
                    s.Request.ExamId,
                    s.Request.Email,
                    s.Request.DisplayName,
                    s.Request.ExamState,
                    s.Request.SecurityState,
                    s.LastSeen,
                    isStale = (now - s.LastSeen) > TimeSpan.FromSeconds(30)
                });
            return Results.Ok(list);
        });

        app.MapPost("/api/sessions/heartbeat", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<HeartbeatRequest>(request.Body, JsonOptions);
            if (body == null || string.IsNullOrWhiteSpace(body.SessionId))
            {
                return Results.BadRequest(new { error = "Invalid session payload" });
            }

            ActiveSessions[body.SessionId] = (body, DateTimeOffset.UtcNow);
            return Results.Ok(new { status = "acknowledged", timestamp = DateTimeOffset.UtcNow });
        });

        // ════════════════════════════════════════════════════════════════
        // API: INCIDENTS
        // ════════════════════════════════════════════════════════════════
        app.MapGet("/api/incidents", () =>
        {
            var incidents = IncidentManager.Instance.GetAllIncidents();
            return Results.Ok(incidents);
        });

        app.MapGet("/api/incidents/{id}", (string id) =>
        {
            var incident = IncidentManager.Instance.GetIncident(id);
            return incident != null ? Results.Ok(incident) : Results.NotFound(new { error = "Incident not found" });
        });

        app.MapPost("/api/incidents/{id}/review-request", async (string id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<ReviewSubmissionRequest>(request.Body, JsonOptions);
            if (body == null || string.IsNullOrWhiteSpace(body.Explanation))
            {
                return Results.BadRequest(new { error = "Explanation is required." });
            }

            bool success = IncidentManager.Instance.SubmitReviewRequest(id, body.Explanation);
            return success ? Results.Ok(new { status = "submitted", incidentId = id }) : Results.NotFound(new { error = "Incident not found." });
        });

        app.MapPost("/api/incidents/{id}/authorize-recheck", async (string id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<RecheckAuthorizationRequest>(request.Body, JsonOptions);
            string reviewer = body?.AuthorizedBy ?? "ExamAdministrator";

            bool success = IncidentManager.Instance.AuthorizeRecovery(id, reviewer, body?.Notes);
            return success ? Results.Ok(new { status = "authorized", incidentId = id, authorizedBy = reviewer }) : Results.NotFound(new { error = "Incident not found." });
        });

        app.MapPost("/api/incidents/{id}/resolve", async (string id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<RecheckAuthorizationRequest>(request.Body, JsonOptions);
            string reviewer = body?.AuthorizedBy ?? "ExamAdministrator";

            bool success = IncidentManager.Instance.ResolveIncident(id, reviewer);
            return success ? Results.Ok(new { status = "resolved", incidentId = id }) : Results.NotFound(new { error = "Incident not found." });
        });

        // ════════════════════════════════════════════════════════════════
        // HTML PAGES: ADMIN DASHBOARD & STUDENT RESOLUTION PORTAL
        // ════════════════════════════════════════════════════════════════
        app.MapGet("/admin", () => Results.Content(GetAdminDashboardHtml(), "text/html"));
        app.MapGet("/admin/{*path}", () => Results.Content(GetAdminDashboardHtml(), "text/html"));

        app.MapGet("/security-incident", () => Results.Content(GetStudentResolutionHtml(), "text/html"));
        app.MapGet("/security-incident/{*path}", () => Results.Content(GetStudentResolutionHtml(), "text/html"));

        app.MapGet("/", () => Results.Redirect("/admin"));

        string url = "http://localhost:5050";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n=================================================");
        Console.WriteLine($"  KLYRO WEB SERVICES STARTED SUCCESSFULLY        ");
        Console.WriteLine($"  Admin Dashboard:    {url}/admin               ");
        Console.WriteLine($"  Resolution Portal:  {url}/security-incident   ");
        Console.WriteLine($"=================================================\n");
        Console.ResetColor();

        await app.RunAsync(url);
        return 0;
    }

    private static string GetAdminDashboardHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Klyro — Examination Administration Portal</title>
    <style>
        :root {
            --bg-dark: #0B0F17;
            --card-bg: #131B2E;
            --border: #1E293B;
            --text-main: #FFFFFF;
            --text-muted: #94A3B8;
            --primary: #0066FF;
            --primary-hover: #2563EB;
            --danger: #EF4444;
            --danger-bg: #221217;
            --success: #10B981;
            --warning: #F59E0B;
            --accent: #38BDF8;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
        body { background: var(--bg-dark); color: var(--text-main); min-height: 100vh; display: flex; flex-direction: column; }
        header { background: #0F1626; border-bottom: 1px solid var(--border); padding: 14px 28px; display: flex; justify-content: space-between; align-items: center; }
        .brand { display: flex; align-items: center; gap: 12px; font-weight: 700; font-size: 18px; letter-spacing: 0.5px; }
        .brand-badge { background: var(--primary); color: #fff; padding: 4px 10px; border-radius: 6px; font-size: 11px; font-weight: 600; text-transform: uppercase; }
        .role-selector { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text-muted); }
        .role-badge { background: #1E293B; color: var(--accent); padding: 6px 12px; border-radius: 6px; border: 1px solid #334155; font-weight: 600; }
        main { padding: 28px; flex: 1; max-width: 1400px; margin: 0 auto; width: 100%; }
        .metrics-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 16px; margin-bottom: 28px; }
        .metric-card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 10px; padding: 18px; }
        .metric-title { font-size: 12px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 8px; }
        .metric-value { font-size: 28px; font-weight: 700; color: #fff; }
        .metric-card.danger { border-color: var(--danger); background: var(--danger-bg); }
        .metric-card.danger .metric-value { color: var(--danger); }
        .metric-card.warning .metric-value { color: var(--warning); }
        .section-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 14px; }
        .section-title { font-size: 18px; font-weight: 600; }
        .data-table { width: 100%; border-collapse: collapse; background: var(--card-bg); border-radius: 10px; overflow: hidden; border: 1px solid var(--border); margin-bottom: 28px; }
        .data-table th, .data-table td { padding: 12px 16px; text-align: left; font-size: 13px; border-bottom: 1px solid var(--border); }
        .data-table th { background: #111827; color: var(--text-muted); font-weight: 600; text-transform: uppercase; font-size: 11px; letter-spacing: 0.5px; }
        .data-table tr:hover { background: #19233C; }
        .badge { display: inline-block; padding: 4px 10px; border-radius: 6px; font-size: 11px; font-weight: 600; }
        .badge.active { background: #064E3B; color: #34D399; }
        .badge.restricted { background: #450A0A; color: #F87171; border: 1px solid var(--danger); }
        .badge.pending { background: #451A03; color: #FBBF24; }
        .badge.approved { background: #1E3A8A; color: #60A5FA; }
        .btn { padding: 6px 14px; border-radius: 6px; border: none; font-size: 12px; font-weight: 600; cursor: pointer; transition: 0.2s; }
        .btn-primary { background: var(--primary); color: #fff; }
        .btn-primary:hover { background: var(--primary-hover); }
        .btn-recheck { background: #059669; color: #fff; }
        .btn-recheck:hover { background: #10B981; }
        .modal-overlay { position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.75); display: none; justify-content: center; align-items: center; z-index: 1000; }
        .modal { background: var(--card-bg); border: 1px solid var(--border); border-radius: 12px; width: 560px; max-width: 90%; padding: 24px; box-shadow: 0 20px 40px rgba(0,0,0,0.5); }
        .modal-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
        .modal-close { background: none; border: none; color: var(--text-muted); font-size: 20px; cursor: pointer; }
    </style>
</head>
<body>
    <header>
        <div class="brand">
            <span>KLYRO</span>
            <span class="brand-badge">Administration Portal</span>
        </div>
        <div class="role-selector">
            <span>Role:</span>
            <span class="role-badge" id="currentRole">Examination Administrator</span>
        </div>
    </header>

    <main>
        <div class="metrics-grid">
            <div class="metric-card">
                <div class="metric-title">Active Users</div>
                <div class="metric-value" id="mUsers">0</div>
            </div>
            <div class="metric-card">
                <div class="metric-title">Active Sessions</div>
                <div class="metric-value" id="mSessions">0</div>
            </div>
            <div class="metric-card">
                <div class="metric-title">Active Exams</div>
                <div class="metric-value" id="mExams">0</div>
            </div>
            <div class="metric-card danger">
                <div class="metric-title">Open Incidents</div>
                <div class="metric-value" id="mIncidents">0</div>
            </div>
            <div class="metric-card danger">
                <div class="metric-title">Restricted Sessions</div>
                <div class="metric-value" id="mRestricted">0</div>
            </div>
            <div class="metric-card warning">
                <div class="metric-title">Pending Reviews</div>
                <div class="metric-value" id="mPending">0</div>
            </div>
        </div>

        <div class="section-header">
            <h2 class="section-title">Security Incidents & Violations</h2>
        </div>
        <table class="data-table">
            <thead>
                <tr>
                    <th>Ref ID</th>
                    <th>Candidate</th>
                    <th>Category</th>
                    <th>Details</th>
                    <th>Status</th>
                    <th>Action</th>
                </tr>
            </thead>
            <tbody id="incidentsBody">
                <tr><td colspan="6" style="text-align: center; color: var(--text-muted);">No incidents recorded.</td></tr>
            </tbody>
        </table>

        <div class="section-header">
            <h2 class="section-title">Live Assessment Workstations</h2>
        </div>
        <table class="data-table">
            <thead>
                <tr>
                    <th>Candidate Email</th>
                    <th>Contest / Exam</th>
                    <th>Session ID</th>
                    <th>Exam State</th>
                    <th>Last Heartbeat</th>
                </tr>
            </thead>
            <tbody id="sessionsBody">
                <tr><td colspan="5" style="text-align: center; color: var(--text-muted);">No active sessions reporting.</td></tr>
            </tbody>
        </table>
    </main>

    <div class="modal-overlay" id="incidentModal">
        <div class="modal">
            <div class="modal-header">
                <h3 id="modalTitle">Incident Details</h3>
                <button class="modal-close" onclick="closeModal()">&times;</button>
            </div>
            <div id="modalBody" style="font-size: 13px; color: var(--text-muted); line-height: 1.6; margin-bottom: 20px;"></div>
            <div style="display: flex; justify-content: flex-end; gap: 10px;">
                <button class="btn btn-primary" onclick="closeModal()">Close</button>
                <button class="btn btn-recheck" id="modalAuthorizeBtn" onclick="authorizeRecheck()">Authorize Fresh Verification</button>
            </div>
        </div>
    </div>

    <script>
        let currentIncidentId = null;

        async function fetchMetrics() {
            try {
                const res = await fetch('/api/metrics');
                if (!res.ok) return;
                const data = await res.json();
                document.getElementById('mUsers').innerText = data.activeUsers;
                document.getElementById('mSessions').innerText = data.activeBrowserSessions;
                document.getElementById('mExams').innerText = data.activeExams;
                document.getElementById('mIncidents').innerText = data.openIncidents;
                document.getElementById('mRestricted').innerText = data.restrictedSessions;
                document.getElementById('mPending').innerText = data.pendingReviews;
            } catch (e) {}
        }

        async function fetchIncidents() {
            try {
                const res = await fetch('/api/incidents');
                if (!res.ok) return;
                const items = await res.json();
                const tbody = document.getElementById('incidentsBody');
                if (!items || items.length === 0) {
                    tbody.innerHTML = '<tr><td colspan="6" style="text-align: center; color: var(--text-muted);">No incidents recorded.</td></tr>';
                    return;
                }
                tbody.innerHTML = items.map(i => `
                    <tr>
                        <td style="font-family: monospace; color: var(--accent);">${i.incidentId.substring(0, 10)}...</td>
                        <td>${i.candidateEmail || 'Unverified Candidate'}</td>
                        <td>${i.category}</td>
                        <td style="max-width: 300px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">${i.details}</td>
                        <td><span class="badge ${i.reviewStatus === 'APPROVED_RECHECK' ? 'approved' : i.reviewStatus === 'PENDING_REVIEW' ? 'pending' : 'restricted'}">${i.reviewStatus}</span></td>
                        <td><button class="btn btn-primary" onclick="openIncident('${i.incidentId}')">Inspect</button></td>
                    </tr>
                `).join('');
            } catch (e) {}
        }

        async function fetchSessions() {
            try {
                const res = await fetch('/api/sessions');
                if (!res.ok) return;
                const items = await res.json();
                const tbody = document.getElementById('sessionsBody');
                if (!items || items.length === 0) {
                    tbody.innerHTML = '<tr><td colspan="5" style="text-align: center; color: var(--text-muted);">No active sessions reporting.</td></tr>';
                    return;
                }
                tbody.innerHTML = items.map(s => `
                    <tr>
                        <td><strong>${s.email || 'Candidate'}</strong></td>
                        <td>${s.examId || 'General'}</td>
                        <td style="font-family: monospace; font-size: 12px; color: var(--text-muted);">${s.sessionId.substring(0, 8)}...</td>
                        <td><span class="badge ${s.examState === 'ACTIVE' ? 'active' : 'restricted'}">${s.examState}</span></td>
                        <td>${new Date(s.lastSeen).toLocaleTimeString()}</td>
                    </tr>
                `).join('');
            } catch (e) {}
        }

        async function openIncident(id) {
            currentIncidentId = id;
            try {
                const res = await fetch(`/api/incidents/${id}`);
                if (!res.ok) return;
                const inc = await res.json();
                document.getElementById('modalTitle').innerText = `Incident: ${inc.incidentId}`;
                document.getElementById('modalBody').innerHTML = `
                    <p><strong>Candidate:</strong> ${inc.candidateEmail || 'Unknown'}</p>
                    <p><strong>Workstation:</strong> ${inc.machineId}</p>
                    <p><strong>Category:</strong> ${inc.category} (${inc.classification})</p>
                    <p><strong>Details:</strong> ${inc.details}</p>
                    <p><strong>Decision:</strong> ${inc.decision}</p>
                    <p><strong>Time:</strong> ${new Date(inc.createdAtUtc).toLocaleString()}</p>
                    <p><strong>Status:</strong> <span class="badge ${inc.reviewStatus === 'APPROVED_RECHECK' ? 'approved' : 'restricted'}">${inc.reviewStatus}</span></p>
                    ${inc.reviewNotes ? `<p style="margin-top: 10px; padding: 10px; background: #0B0F17; border-radius: 6px;"><strong>Candidate Explanation:</strong><br>${inc.reviewNotes}</p>` : ''}
                `;
                document.getElementById('incidentModal').style.display = 'flex';
            } catch (e) {}
        }

        function closeModal() {
            document.getElementById('incidentModal').style.display = 'none';
        }

        async function authorizeRecheck() {
            if (!currentIncidentId) return;
            try {
                const res = await fetch(`/api/incidents/${currentIncidentId}/authorize-recheck`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ authorizedBy: 'ExamAdministrator', notes: 'Administrator approved fresh security check' })
                });
                if (res.ok) {
                    alert('Recheck authorized! Candidate may now click Check Again.');
                    closeModal();
                    fetchIncidents();
                    fetchMetrics();
                }
            } catch (e) {}
        }

        setInterval(() => {
            fetchMetrics();
            fetchIncidents();
            fetchSessions();
        }, 3000);

        fetchMetrics();
        fetchIncidents();
        fetchSessions();
    </script>
</body>
</html>
""";

    private static string GetStudentResolutionHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Klyro — Security Review & Resolution</title>
    <style>
        :root {
            --bg-dark: #0F1219;
            --card-bg: #141C2E;
            --border: #1E293B;
            --text-main: #FFFFFF;
            --text-muted: #94A3B8;
            --primary: #0066FF;
            --danger: #EF4444;
            --success: #10B981;
            --accent: #38BDF8;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
        body { background: var(--bg-dark); color: var(--text-main); min-height: 100vh; display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 24px; }
        .card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 14px; width: 100%; max-width: 580px; padding: 36px; box-shadow: 0 20px 40px rgba(0,0,0,0.5); }
        .header-accent { width: 24px; height: 3px; background: var(--primary); border-radius: 1.5px; margin-bottom: 12px; }
        .section-label { font-size: 11px; font-weight: 600; letter-spacing: 2px; color: var(--text-muted); text-transform: uppercase; margin-bottom: 8px; }
        h1 { font-size: 26px; font-weight: 700; margin-bottom: 10px; }
        .subtitle { font-size: 13.5px; color: var(--text-muted); line-height: 1.5; margin-bottom: 24px; }
        .alert-box { background: #201015; border: 1px solid #50181F; border-radius: 10px; padding: 14px 16px; margin-bottom: 24px; }
        .alert-title { font-size: 12.5px; font-weight: 700; color: #FCA5A5; margin-bottom: 4px; }
        .alert-desc { font-size: 12px; color: #E2E8F0; line-height: 1.5; }
        .status-pill { display: inline-block; padding: 4px 12px; border-radius: 12px; font-size: 11px; font-weight: 600; text-transform: uppercase; margin-top: 8px; }
        .status-restricted { background: #450A0A; color: #F87171; border: 1px solid var(--danger); }
        .status-approved { background: #064E3B; color: #34D399; border: 1px solid var(--success); }
        .form-group { margin-bottom: 20px; }
        label { display: block; font-size: 12.5px; font-weight: 600; margin-bottom: 8px; color: #E2E8F0; }
        textarea, input[type="text"] { width: 100%; background: #0B0F17; border: 1px solid var(--border); border-radius: 8px; padding: 12px; color: #fff; font-size: 13px; outline: none; }
        textarea:focus, input[type="text"]:focus { border-color: var(--accent); }
        textarea { height: 100px; resize: vertical; }
        .btn { width: 100%; height: 46px; background: var(--primary); border: 1px solid #2563EB; border-radius: 8px; color: #fff; font-size: 14px; font-weight: 600; cursor: pointer; transition: 0.2s; }
        .btn:hover { background: #2563EB; }
        .btn:disabled { opacity: 0.5; cursor: not-allowed; }
        .approved-banner { background: #064E3B; border: 1px solid #059669; border-radius: 10px; padding: 16px; margin-top: 20px; display: none; }
        .footer-note { font-size: 12px; color: #64748B; margin-top: 24px; text-align: center; line-height: 1.5; }
    </style>
</head>
<body>
    <div class="card">
        <div class="header-accent"></div>
        <div class="section-label">ASSESSMENT INTEGRITY REVIEW</div>
        <h1>Klyro Security Review</h1>
        <p class="subtitle">Your assessment session has been restricted because a security condition requires review. Your exam code and state are securely preserved.</p>

        <div class="alert-box">
            <div class="alert-title">Incident Information</div>
            <div class="alert-desc" id="incidentDetails">Loading security incident details...</div>
            <div id="statusBadge" class="status-pill status-restricted">Status: Restricted</div>
        </div>

        <div id="reviewFormSection">
            <div class="form-group">
                <label for="incidentRef">Incident Reference ID</label>
                <input type="text" id="incidentRef" placeholder="Enter incident reference">
            </div>

            <div class="form-group">
                <label for="explanation">Explanation / Actions Taken</label>
                <textarea id="explanation" placeholder="Describe the steps you took to disconnect remote sessions or close background tools..."></textarea>
            </div>

            <button class="btn" id="submitBtn" onclick="submitReview()">Submit Review Request</button>
        </div>

        <div class="approved-banner" id="approvedBanner">
            <strong>✓ Review Approved!</strong><br>
            Your examination administrator has approved a fresh environment verification.
            Please return to Klyro and click <strong>"Check Again"</strong> or <strong>"Recheck Environment"</strong>.
        </div>

        <div class="footer-note">
            If you need immediate assistance or believe this restriction was made in error, please contact your examination hall invigilator or administrator.
        </div>
    </div>

    <script>
        const urlParams = new URLSearchParams(window.location.search);
        const refFromUrl = urlParams.get('ref');
        const refInput = document.getElementById('incidentRef');

        if (refFromUrl) {
            refInput.value = refFromUrl;
            loadIncident(refFromUrl);
        }

        refInput.addEventListener('change', () => {
            if (refInput.value.trim()) {
                loadIncident(refInput.value.trim());
            }
        });

        async function loadIncident(id) {
            try {
                const res = await fetch(`/api/incidents/${id}`);
                if (!res.ok) {
                    document.getElementById('incidentDetails').innerText = 'No record found for this incident reference.';
                    return;
                }
                const inc = await res.json();
                document.getElementById('incidentDetails').innerHTML = `
                    <strong>Category:</strong> ${inc.category}<br>
                    <strong>Details:</strong> ${inc.details}<br>
                    <strong>Observed:</strong> ${new Date(inc.createdAtUtc).toLocaleTimeString()}
                `;

                if (inc.reviewStatus === 'APPROVED_RECHECK' || inc.reviewStatus === 'RESOLVED') {
                    document.getElementById('statusBadge').className = 'status-pill status-approved';
                    document.getElementById('statusBadge').innerText = 'Status: Approved for Recheck';
                    document.getElementById('approvedBanner').style.display = 'block';
                    document.getElementById('submitBtn').disabled = true;
                } else if (inc.reviewStatus === 'PENDING_REVIEW' && inc.reviewNotes) {
                    document.getElementById('statusBadge').innerText = 'Status: Review Submitted (Pending Administrator)';
                    document.getElementById('submitBtn').disabled = true;
                    document.getElementById('submitBtn').innerText = 'Review Under Evaluation';
                }
            } catch (e) {}
        }

        async function submitReview() {
            const id = refInput.value.trim();
            const explanation = document.getElementById('explanation').value.trim();
            if (!id) { alert('Incident reference is required.'); return; }
            if (!explanation) { alert('Please provide an explanation of the actions taken.'); return; }

            const btn = document.getElementById('submitBtn');
            btn.disabled = true;
            btn.innerText = 'Submitting...';

            try {
                const res = await fetch(`/api/incidents/${id}/review-request`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ explanation })
                });

                if (res.ok) {
                    alert('Review request submitted successfully to the examination administrator.');
                    btn.innerText = 'Review Under Evaluation';
                    loadIncident(id);
                } else {
                    const err = await res.json().catch(() => ({}));
                    alert('Failed to submit review request: ' + (err.error || res.statusText));
                    btn.disabled = false;
                    btn.innerText = 'Submit Review Request';
                }
            } catch (e) {
                alert('Connection error: ' + e.message);
                btn.disabled = false;
                btn.innerText = 'Submit Review Request';
            }
        }

        // Poll status every 3 seconds if reference is loaded
        setInterval(() => {
            const id = refInput.value.trim();
            if (id) {
                loadIncident(id);
            }
        }, 3000);
    </script>
</body>
</html>
""";
}
