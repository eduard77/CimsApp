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
