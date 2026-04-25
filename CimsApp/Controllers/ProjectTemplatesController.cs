using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Controllers;

[Route("api/projects/{projectId:guid}/templates")]
public class ProjectTemplatesController(CimsDbContext db, IProjectProvisioningService provisioning) : CimsControllerBase
{
    /// <summary>List all PMBOK templates for a project, grouped by folder</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var templates = await db.ProjectTemplates
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.PmbokFolder).ThenBy(t => t.FileName)
            .Select(t => new
            {
                t.Id,
                t.PmbokFolder,
                t.FileName,
                t.Title,
                t.FileExtension,
                t.CdeState,
                t.Suitability,
                t.RevisionCode,
                t.IsEdited,
                t.UpdatedAt
            })
            .ToListAsync(ct);

        return Ok(templates);
    }

    /// <summary>Read the content of a single template file</summary>
    [HttpGet("{templateId:guid}/content")]
    public async Task<IActionResult> GetContent(Guid projectId, Guid templateId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var template = await db.ProjectTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.ProjectId == projectId, ct);
        if (template is null) return NotFound();

        var content = await provisioning.GetFileContentAsync(templateId, ct);
        return Ok(new { template.Title, template.FileName, template.FileExtension, Content = content });
    }

    /// <summary>Save edited template content back to disk</summary>
    [HttpPut("{templateId:guid}/content")]
    public async Task<IActionResult> SaveContent(Guid projectId, Guid templateId, [FromBody] SaveContentRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager)) throw new ForbiddenException();
        var template = await db.ProjectTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && t.ProjectId == projectId, ct);
        if (template is null) return NotFound();

        await provisioning.SaveFileContentAsync(templateId, req.Content, ct);
        return NoContent();
    }

    /// <summary>Re-provision (admin only — useful for regenerating missing files)</summary>
    [HttpPost("~/api/projects/{projectId:guid}/provision")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Reprovision(Guid projectId, CancellationToken ct)
    {
        await provisioning.ProvisionAsync(projectId, ct);
        return Ok(new { message = "Project templates provisioned" });
    }
}

public record SaveContentRequest(string Content);
