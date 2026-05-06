using System.Net.Http.Json;
using System.Text.Json;
using CimsApp.Models;
using CimsApp.DTOs;

namespace CimsApp.UI;

public class BlazorApiClient(IHttpClientFactory factory, UiStateService state)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private HttpClient Http()
    {
        var c = factory.CreateClient("Self");
        if (state.AccessToken != null)
            c.DefaultRequestHeaders.Authorization = new("Bearer", state.AccessToken);
        return c;
    }

    // ── Auth ──────────────────────────────────────────────────────────────────
    public async Task<(bool Ok, string? Error, AuthResponse? Data)> LoginAsync(string email, string password)
    {
        try
        {
            var r = await Http().PostAsJsonAsync("api/v1/auth/login", new LoginRequest(email, password));
            var body = await r.Content.ReadAsStringAsync();
            if (r.IsSuccessStatusCode)
            {
                var data = Extract<AuthResponse>(body, "data");
                return (true, null, data);
            }
            var err = ExtractError(body);
            return (false, err, null);
        }
        catch (Exception ex) { return (false, $"Cannot connect: {ex.Message}", null); }
    }

    // ── Organisations ─────────────────────────────────────────────────────────
    public async Task<List<Organisation>> GetOrgsAsync()
    {
        try { var b = await Http().GetStringAsync("api/v1/organisations"); return Extract<List<Organisation>>(b, "data") ?? []; }
        catch { return []; }
    }

    public async Task<Organisation?> CreateOrgAsync(CreateOrgRequest req)
    {
        try { var r = await Http().PostAsJsonAsync("api/v1/organisations", req); var b = await r.Content.ReadAsStringAsync(); return Extract<Organisation>(b, "data"); }
        catch { return null; }
    }

    // ── Projects ──────────────────────────────────────────────────────────────
    public async Task<List<Project>> GetProjectsAsync(string? search = null)
    {
        try { var b = await Http().GetStringAsync($"api/v1/projects{(search != null ? $"?search={search}" : "")}"); return Extract<List<Project>>(b, "data") ?? []; }
        catch { return []; }
    }

    public async Task<Project?> CreateProjectAsync(CreateProjectRequest req)
    {
        try { var r = await Http().PostAsJsonAsync("api/v1/projects", req); var b = await r.Content.ReadAsStringAsync(); return Extract<Project>(b, "data"); }
        catch { return null; }
    }

    // ── Documents ─────────────────────────────────────────────────────────────
    public async Task<List<Document>> GetDocumentsAsync(Guid projectId, string? state = null, string? search = null)
    {
        try { var url = $"api/v1/projects/{projectId}/documents?" + (state != null ? $"state={state}&" : "") + (search != null ? $"search={search}" : ""); var b = await Http().GetStringAsync(url); return Extract<List<Document>>(b, "data") ?? []; }
        catch { return []; }
    }

    public async Task<Document?> CreateDocumentAsync(Guid projectId, CreateDocumentRequest req)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/documents", req); var b = await r.Content.ReadAsStringAsync(); return Extract<Document>(b, "data"); }
        catch { return null; }
    }

    public async Task<Document?> TransitionDocumentAsync(Guid projectId, Guid documentId, CdeState toState)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/documents/{documentId}/transition", new TransitionRequest(toState, null)); var b = await r.Content.ReadAsStringAsync(); return Extract<Document>(b, "data"); }
        catch { return null; }
    }

    // ── RFIs ──────────────────────────────────────────────────────────────────
    public async Task<List<Rfi>> GetRfisAsync(Guid projectId, string? status = null)
    {
        try { var url = $"api/v1/projects/{projectId}/rfis" + (status != null ? $"?status={status}" : ""); var b = await Http().GetStringAsync(url); return Extract<List<Rfi>>(b, "data") ?? []; }
        catch { return []; }
    }

    public async Task<Rfi?> CreateRfiAsync(Guid projectId, CreateRfiRequest req)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/rfis", req); var b = await r.Content.ReadAsStringAsync(); return Extract<Rfi>(b, "data"); }
        catch { return null; }
    }

    public async Task<Rfi?> RespondRfiAsync(Guid projectId, Guid rfiId, RespondRfiRequest req)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/rfis/{rfiId}/respond", req); var b = await r.Content.ReadAsStringAsync(); return Extract<Rfi>(b, "data"); }
        catch { return null; }
    }

    // ── Actions ───────────────────────────────────────────────────────────────
    public async Task<List<ActionItem>> GetActionsAsync(Guid projectId, string? status = null, bool overdue = false)
    {
        try { var url = $"api/v1/projects/{projectId}/actions?" + (status != null ? $"status={status}&" : "") + (overdue ? "overdue=true" : ""); var b = await Http().GetStringAsync(url); return Extract<List<ActionItem>>(b, "data") ?? []; }
        catch { return []; }
    }

    public async Task<ActionItem?> CreateActionAsync(Guid projectId, CreateActionRequest req)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/actions", req); var b = await r.Content.ReadAsStringAsync(); return Extract<ActionItem>(b, "data"); }
        catch { return null; }
    }

    public async Task<ActionItem?> UpdateActionAsync(Guid projectId, Guid actionId, UpdateActionRequest req)
    {
        try { var r = await Http().PatchAsJsonAsync($"api/v1/projects/{projectId}/actions/{actionId}", req); var b = await r.Content.ReadAsStringAsync(); return Extract<ActionItem>(b, "data"); }
        catch { return null; }
    }

    // ── Audit ─────────────────────────────────────────────────────────────────
    public async Task<List<AuditLog>> GetAuditAsync(Guid projectId)
    {
        try { var b = await Http().GetStringAsync($"api/v1/projects/{projectId}/audit?limit=50"); return Extract<List<AuditLog>>(b, "data") ?? []; }
        catch { return []; }
    }

    // ── Admin Console (T-S16-04) ──────────────────────────────────────────────
    // Each method mirrors the AdminPagedResult<T> response shape and
    // surfaces the (success | http-status + extracted error message)
    // tuple for the mutate calls so the UI can show a snackbar.

    public async Task<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminUserListItem>?> AdminListUsersAsync(string? search = null, int skip = 0, int take = 50)
    {
        try { var url = $"api/v1/admin/users?skip={skip}&take={take}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"); var b = await Http().GetStringAsync(url); return Extract<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminUserListItem>>(b, "data"); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> AdminSetGlobalRoleAsync(Guid userId, CimsApp.Models.UserRole? role)
    {
        try { var r = await Http().PutAsJsonAsync($"api/v1/admin/users/{userId}/global-role", new CimsApp.Services.Admin.SetGlobalRoleRequest(role)); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> AdminDeactivateUserAsync(Guid userId)
    {
        try { var r = await Http().PostAsync($"api/v1/users/{userId}/deactivate", null); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> AdminRevokeUserTokensAsync(Guid userId)
    {
        try { var r = await Http().PostAsync($"api/v1/users/{userId}/revoke-tokens", null); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminOrgListItem>?> AdminListOrgsAsync(string? search = null, int skip = 0, int take = 50, bool includeInactive = false)
    {
        try { var url = $"api/v1/admin/organisations?skip={skip}&take={take}&includeInactive={includeInactive}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"); var b = await Http().GetStringAsync(url); return Extract<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminOrgListItem>>(b, "data"); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> AdminUpdateOrgAsync(Guid orgId, string name, string? country)
    {
        try { var r = await Http().PutAsJsonAsync($"api/v1/admin/organisations/{orgId}", new CimsApp.Services.Admin.UpdateOrganisationRequest(name, country)); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> AdminDeactivateOrgAsync(Guid orgId)
    {
        try { var r = await Http().PostAsync($"api/v1/admin/organisations/{orgId}/deactivate", null); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminInvitationListItem>?> AdminListInvitationsAsync(Guid? organisationId = null, int skip = 0, int take = 50)
    {
        try { var url = $"api/v1/admin/invitations?skip={skip}&take={take}" + (organisationId is { } id ? $"&organisationId={id}" : ""); var b = await Http().GetStringAsync(url); return Extract<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminInvitationListItem>>(b, "data"); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> AdminRevokeInvitationAsync(Guid invitationId)
    {
        try { var r = await Http().DeleteAsync($"api/v1/admin/invitations/{invitationId}"); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminAuditListItem>?> AdminListAuditAsync(DateTime? from = null, DateTime? to = null, string? action = null, string? entity = null, Guid? userId = null, int skip = 0, int take = 50)
    {
        try
        {
            var qs = new List<string> { $"skip={skip}", $"take={take}" };
            if (from is { } f) qs.Add($"from={Uri.EscapeDataString(f.ToString("o"))}");
            if (to is { } t) qs.Add($"to={Uri.EscapeDataString(t.ToString("o"))}");
            if (!string.IsNullOrWhiteSpace(action)) qs.Add($"action={Uri.EscapeDataString(action)}");
            if (!string.IsNullOrWhiteSpace(entity)) qs.Add($"entity={Uri.EscapeDataString(entity)}");
            if (userId is { } u) qs.Add($"userId={u}");
            var b = await Http().GetStringAsync($"api/v1/admin/audit?" + string.Join("&", qs));
            return Extract<CimsApp.Services.Admin.AdminPagedResult<CimsApp.Services.Admin.AdminAuditListItem>>(b, "data");
        }
        catch { return null; }
    }

    // ── Compliance (S18 fast-follower) ────────────────────────────────────────

    public async Task<CimsApp.Services.Compliance.EvidencePagedResult?> ListEvidenceAsync(Guid projectId, ComplianceArea? area = null, int skip = 0, int take = 50)
    {
        try { var url = $"api/v1/projects/{projectId}/evidence?skip={skip}&take={take}" + (area is { } a ? $"&area={a}" : ""); var b = await Http().GetStringAsync(url); return Extract<CimsApp.Services.Compliance.EvidencePagedResult>(b, "data"); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> AddEvidenceAsync(Guid projectId, CimsApp.Services.Compliance.AddEvidenceRequest req)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/evidence", req); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> RemoveEvidenceAsync(Guid projectId, Guid evidenceId)
    {
        try { var r = await Http().DeleteAsync($"api/v1/projects/{projectId}/evidence/{evidenceId}"); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<CimsApp.Services.Compliance.AuditorAssignmentPagedResult?> ListAuditorAssignmentsAsync(Guid? projectId = null, bool includeRevoked = false, int skip = 0, int take = 50)
    {
        try { var url = $"api/v1/admin/auditor-assignments?skip={skip}&take={take}&includeRevoked={includeRevoked}" + (projectId is { } p ? $"&projectId={p}" : ""); var b = await Http().GetStringAsync(url); return Extract<CimsApp.Services.Compliance.AuditorAssignmentPagedResult>(b, "data"); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreateAuditorAssignmentAsync(CimsApp.Services.Compliance.CreateAuditorAssignmentRequest req)
    {
        try { var r = await Http().PostAsJsonAsync("api/v1/admin/auditor-assignments", req); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> RevokeAuditorAssignmentAsync(Guid assignmentId)
    {
        try { var r = await Http().PostAsync($"api/v1/admin/auditor-assignments/{assignmentId}/revoke", null); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Returns the audit-export ZIP bundle URL for the given
    /// project + window + areas. Used by the UI's anchor-href download
    /// pattern; the browser's Authorization header is automatically
    /// included by the existing JWT-bearing fetch infrastructure.</summary>
    public string AuditExportUrl(Guid projectId, DateTime? from, DateTime? to, ComplianceAreaScope areas)
    {
        var qs = new List<string> { $"areas={(int)areas}" };
        if (from is { } f) qs.Add($"from={Uri.EscapeDataString(f.ToString("o"))}");
        if (to is { } t) qs.Add($"to={Uri.EscapeDataString(t.ToString("o"))}");
        return $"/api/v1/projects/{projectId}/audit-export?" + string.Join("&", qs);
    }

    public async Task<(bool Ok, byte[]? Bytes, string? Error)> DownloadAuditExportAsync(Guid projectId, DateTime? from, DateTime? to, ComplianceAreaScope areas)
    {
        try
        {
            var r = await Http().GetAsync(AuditExportUrl(projectId, from, to, areas));
            if (!r.IsSuccessStatusCode)
                return (false, null, ExtractError(await r.Content.ReadAsStringAsync()));
            return (true, await r.Content.ReadAsByteArrayAsync(), null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    // ── Site-user mobile workflows (S20) ──────────────────────────────────────

    public async Task<CimsApp.Services.SiteUserWorkflows.DailyDiaryPagedResult?> ListDiaryAsync(Guid projectId, DateOnly? from = null, DateOnly? to = null, int skip = 0, int take = 50)
    {
        try
        {
            var qs = new List<string> { $"skip={skip}", $"take={take}" };
            if (from is { } f) qs.Add($"from={f:yyyy-MM-dd}");
            if (to   is { } t) qs.Add($"to={t:yyyy-MM-dd}");
            var b = await Http().GetStringAsync($"api/v1/projects/{projectId}/diary?" + string.Join("&", qs));
            return Extract<CimsApp.Services.SiteUserWorkflows.DailyDiaryPagedResult>(b, "data");
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreateDiaryAsync(Guid projectId, CimsApp.Services.SiteUserWorkflows.CreateDailyDiaryRequest req)
    {
        try { var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/diary", req); var b = await r.Content.ReadAsStringAsync(); return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b)); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<CimsApp.Services.SiteUserWorkflows.NcrPagedResult?> ListNcrsAsync(Guid projectId, NcrState? status = null, int skip = 0, int take = 50)
    {
        try { var url = $"api/v1/projects/{projectId}/ncrs?skip={skip}&take={take}" + (status is { } s ? $"&status={s}" : ""); var b = await Http().GetStringAsync(url); return Extract<CimsApp.Services.SiteUserWorkflows.NcrPagedResult>(b, "data"); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error, string? Number)> CreateNcrAsync(Guid projectId, CimsApp.Services.SiteUserWorkflows.CreateNcrRequest req)
    {
        try
        {
            var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/ncrs", req);
            var b = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) return (false, ExtractError(b), null);
            using var doc = System.Text.Json.JsonDocument.Parse(b);
            var num = doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("number", out var nn) ? nn.GetString() : null;
            return (true, null, num);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public async Task<(bool Ok, string? Error)> TransitionNcrAsync(Guid projectId, Guid ncrId, NcrState toState, string? resolutionNotes = null)
    {
        try
        {
            var req = new CimsApp.Services.SiteUserWorkflows.TransitionNcrRequest(toState, resolutionNotes);
            var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/ncrs/{ncrId}/transition", req);
            var b = await r.Content.ReadAsStringAsync();
            return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> AssignNcrAsync(Guid projectId, Guid ncrId, Guid assignedToId)
    {
        try
        {
            var req = new CimsApp.Services.SiteUserWorkflows.AssignNcrRequest(assignedToId);
            var r = await Http().PostAsJsonAsync($"api/v1/projects/{projectId}/ncrs/{ncrId}/assign", req);
            var b = await r.Content.ReadAsStringAsync();
            return r.IsSuccessStatusCode ? (true, null) : (false, ExtractError(b));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static T? Extract<T>(string json, string key)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty(key, out var el) ? JsonSerializer.Deserialize<T>(el.GetRawText(), Json) : default; }
        catch { return default; }
    }

    private static string ExtractError(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error"; }
        catch { return "Unknown error"; }
    }
}
