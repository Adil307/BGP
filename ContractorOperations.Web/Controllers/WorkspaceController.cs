using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Dashboard.View")]
public class WorkspaceController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IBusinessIdService _ids;
    private readonly IProjectSetupService _projectSetup;
    private readonly IAuditService _audit;

    public WorkspaceController(ApplicationDbContext db, IBusinessIdService ids, IProjectSetupService projectSetup, IAuditService audit)
    {
        _db = db;
        _ids = ids;
        _projectSetup = projectSetup;
        _audit = audit;
    }

    public async Task<IActionResult> Index()
    {
        var countries = await _db.Countries.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        var counts = await _db.ProjectWorkspaces.AsNoTracking()
            .Where(x => x.Project!.IsActive)
            .GroupBy(x => x.CountryId)
            .Select(g => new { CountryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CountryId, x => x.Count);
        return View(new CountrySelectionVm { Countries = countries, ProjectCounts = counts });
    }

    public async Task<IActionResult> Projects(int id)
    {
        var country = await _db.Countries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (country == null) return NotFound();

        var workspaces = await _db.ProjectWorkspaces.AsNoTracking()
            .Include(x => x.Project)
            .Where(x => x.CountryId == id && x.Project!.IsActive)
            .OrderBy(x => x.Project!.Name)
            .ToListAsync();

        return View(new CountryProjectsVm { Country = country, Workspaces = workspaces });
    }

    public async Task<IActionResult> Project(int id)
    {
        var workspace = await _db.ProjectWorkspaces.AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Country)
            .FirstOrDefaultAsync(x => x.ProjectId == id);
        if (workspace?.Project == null || workspace.Country == null) return NotFound();

        await _projectSetup.EnsureInitialSheetsAsync(id);
        var sheets = await _db.ProjectSheets.AsNoTracking()
            .Where(x => x.ProjectId == id && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync();

        return View(new ProjectWorkspaceDashboardVm
        {
            Project = workspace.Project,
            Workspace = workspace,
            Country = workspace.Country,
            Sheets = sheets,
            JobCount = await _db.Jobs.CountAsync(x => x.ProjectId == id),
            CompletedJobs = await _db.Jobs.CountAsync(x => x.ProjectId == id && x.Status == JobStatus.Completed),
            InventoryRequestCount = await _db.InventoryRequests.CountAsync(x => x.ProjectId == id),
            PendingInventoryRequests = await _db.InventoryRequests.CountAsync(x => x.ProjectId == id && x.Status == InventoryRequestStatus.Pending)
        });
    }

    [RequirePermission("Projects.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCountry(string name, string code)
    {
        name = (name ?? string.Empty).Trim();
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
        {
            TempData["Error"] = "Country name and code are required.";
            return RedirectToAction(nameof(Index));
        }
        if (await _db.Countries.AnyAsync(x => x.Code == code))
        {
            TempData["Error"] = "That country code already exists.";
            return RedirectToAction(nameof(Index));
        }

        var country = new Country { Name = name, Code = code, IsActive = true };
        _db.Countries.Add(country);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Create", "Country", country.Id, $"{country.Code} - {country.Name}");
        TempData["Success"] = $"Country \"{country.Name}\" added.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Projects.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddProject(int countryId, string name, string? description, string? projectManager, DateTime? startDate, DateTime? deadline, decimal? budget, string priority = "Medium")
    {
        var country = await _db.Countries.FirstOrDefaultAsync(x => x.Id == countryId && x.IsActive);
        if (country == null) return NotFound();
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Project name is required.";
            return RedirectToAction(nameof(Projects), new { id = countryId });
        }

        var uniqueId = await _ids.NextAsync("PRJ");
        var project = new Project { Code = uniqueId, Name = name, IsActive = true };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var workspace = new ProjectWorkspace
        {
            ProjectId = project.Id,
            CountryId = countryId,
            UniqueId = uniqueId,
            Description = description?.Trim(),
            ProjectManager = projectManager?.Trim(),
            StartDate = startDate,
            Deadline = deadline,
            Budget = budget,
            Priority = string.IsNullOrWhiteSpace(priority) ? "Medium" : priority.Trim(),
            Status = "Active"
        };
        _db.ProjectWorkspaces.Add(workspace);
        await _db.SaveChangesAsync();
        await _projectSetup.EnsureInitialSheetsAsync(project.Id);
        await _audit.WriteAsync(HttpContext, "Create", "Project", project.Id, $"{uniqueId} - {name} - {country.Name}");

        TempData["Success"] = $"Project \"{name}\" created with ID {uniqueId}.";
        return RedirectToAction(nameof(Project), new { id = project.Id });
    }

    [RequirePermission("Projects.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProject(int projectId, string name, string? description, string? projectManager, DateTime? startDate, DateTime? deadline, decimal? budget, string priority, string status, int progressPercent, string? notes)
    {
        var project = await _db.Projects.FindAsync(projectId);
        var workspace = await _db.ProjectWorkspaces.FirstOrDefaultAsync(x => x.ProjectId == projectId);
        if (project == null || workspace == null) return NotFound();

        project.Name = (name ?? project.Name).Trim();
        workspace.Description = description?.Trim();
        workspace.ProjectManager = projectManager?.Trim();
        workspace.StartDate = startDate;
        workspace.Deadline = deadline;
        workspace.Budget = budget;
        workspace.Priority = string.IsNullOrWhiteSpace(priority) ? "Medium" : priority.Trim();
        workspace.Status = string.IsNullOrWhiteSpace(status) ? "Active" : status.Trim();
        workspace.ProgressPercent = Math.Clamp(progressPercent, 0, 100);
        workspace.Notes = notes?.Trim();
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Edit", "Project", project.Id, $"{workspace.UniqueId} - {project.Name}");
        TempData["Success"] = "Project details updated.";
        return RedirectToAction(nameof(Project), new { id = projectId });
    }
}
