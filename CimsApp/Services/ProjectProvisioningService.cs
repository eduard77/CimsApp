using CimsApp.Data;
using CimsApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Services;

public interface IProjectProvisioningService
{
    Task ProvisionAsync(Guid projectId, CancellationToken ct = default);
    Task<string> GetFileContentAsync(Guid templateId, CancellationToken ct = default);
    Task SaveFileContentAsync(Guid templateId, string content, CancellationToken ct = default);
}

/// <summary>
/// Provisions a newly created project with the PMBOK template folder structure.
/// Copies /wwwroot/templates/project_template/ to /storage/projects/{code}/
/// then registers each file as a ProjectTemplate in the database.
/// </summary>
public class ProjectProvisioningService : IProjectProvisioningService
{
    private readonly CimsDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ProjectProvisioningService> _log;

    // Source: the master templates (read-only, ships with the app)
    private string TemplateSource => Path.Combine(_env.WebRootPath, "templates", "project_template");

    // Destination: per-project storage on disk
    private string StorageRoot => Path.Combine(_env.ContentRootPath, "storage", "projects");

    public ProjectProvisioningService(
        CimsDbContext db,
        IWebHostEnvironment env,
        ILogger<ProjectProvisioningService> log)
    {
        _db = db;
        _env = env;
        _log = log;
    }

    public async Task ProvisionAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException($"Project {projectId} not found");

        if (!Directory.Exists(TemplateSource))
        {
            _log.LogWarning("Template source folder not found: {Path}. Skipping provisioning.", TemplateSource);
            return;
        }

        var projectFolder = Path.Combine(StorageRoot, project.Code);

        if (Directory.Exists(projectFolder))
        {
            _log.LogInformation("Project folder {Path} already exists. Skipping provisioning.", projectFolder);
            return;
        }

        _log.LogInformation("Provisioning project {Code} at {Path}", project.Code, projectFolder);

        // 1. Copy entire template folder
        CopyDirectory(TemplateSource, projectFolder);

        // 2. Substitute placeholders in all text files
        var placeholders = new Dictionary<string, string>
        {
            { "[PRJ-CODE]", project.Code },
            { "[PROJECT-NAME]", project.Name },
            { "[CLIENT-NAME]", "" },
            { "[START-DATE]", project.StartDate?.ToString("dd/MM/yyyy") ?? "" },
            { "[END-DATE]",   project.EndDate?.ToString("dd/MM/yyyy") ?? "" },
            { "[CURRENCY]",   project.Currency },
            { "[BUDGET]",     project.BudgetValue?.ToString("N0") ?? "" },
        };

        SubstitutePlaceholders(projectFolder, placeholders);

        // 3. Register each template file in the database
        var templates = new List<ProjectTemplate>();
        var sourceRoot = Path.GetFullPath(projectFolder);

        foreach (var filePath in Directory.EnumerateFiles(projectFolder, "*.*", SearchOption.AllDirectories))
        {
            var relative    = Path.GetRelativePath(sourceRoot, filePath).Replace('\\', '/');
            var folder      = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
            var fileName    = Path.GetFileName(filePath);
            var extension   = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var title       = Path.GetFileNameWithoutExtension(fileName)
                                .Replace('_', ' ')
                                .Replace("-", " ");

            templates.Add(new ProjectTemplate
            {
                ProjectId     = project.Id,
                PmbokFolder   = folder,
                FileName      = fileName,
                Title         = title,
                StorageKey    = relative,
                FileExtension = extension,
                CdeState      = CdeState.WorkInProgress,
                Suitability   = SuitabilityCode.S0,
                RevisionCode  = "P01",
            });
        }

        await _db.ProjectTemplates.AddRangeAsync(templates, ct);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Provisioned {Count} templates for project {Code}", templates.Count, project.Code);
    }

    public async Task<string> GetFileContentAsync(Guid templateId, CancellationToken ct = default)
    {
        var tpl = await _db.ProjectTemplates
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new InvalidOperationException($"Template {templateId} not found");

        var filePath = Path.Combine(StorageRoot, tpl.Project.Code, tpl.StorageKey);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        return await File.ReadAllTextAsync(filePath, ct);
    }

    public async Task SaveFileContentAsync(Guid templateId, string content, CancellationToken ct = default)
    {
        var tpl = await _db.ProjectTemplates
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new InvalidOperationException($"Template {templateId} not found");

        var filePath = Path.Combine(StorageRoot, tpl.Project.Code, tpl.StorageKey);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(filePath, content, ct);

        tpl.IsEdited = true;
        tpl.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, destination));

        foreach (var file in Directory.GetFiles(source, "*.*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination), overwrite: false);
    }

    private static void SubstitutePlaceholders(string folder, Dictionary<string, string> placeholders)
    {
        // Only process text-based files
        var textExtensions = new HashSet<string> { ".md", ".csv", ".txt", ".html", ".json", ".xml" };

        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!textExtensions.Contains(ext)) continue;

            var content = File.ReadAllText(file);
            var original = content;

            foreach (var kvp in placeholders)
                content = content.Replace(kvp.Key, kvp.Value);

            if (content != original)
                File.WriteAllText(file, content);
        }
    }
}
