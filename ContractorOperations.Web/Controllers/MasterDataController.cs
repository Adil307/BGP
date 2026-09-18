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
public class MasterDataController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditService _audit;
    private readonly IBusinessIdService _ids;
    private readonly IProjectSetupService _projectSetup;
    public MasterDataController(ApplicationDbContext db, IPermissionService permissions, IAuditService audit, IBusinessIdService ids, IProjectSetupService projectSetup)
    { _db = db; _permissions = permissions; _audit = audit; _ids = ids; _projectSetup = projectSetup; }

    public async Task<IActionResult> Index(string type = "projects")
    {
        var key = type.ToLowerInvariant() switch { "projects" => "Projects.Manage", "departments" => "Departments.Manage", "contractors" => "Contractors.Manage", _ => "MasterData.Manage" };
        if (!await _permissions.HasPermissionAsync(User, key)) return Forbid();
        return View(new MasterDataVm
        {
            Type = type.ToLowerInvariant(), Projects = await _db.Projects.OrderBy(x=>x.Name).ToListAsync(), Departments = await _db.Departments.OrderBy(x=>x.Name).ToListAsync(),
            Contractors = await _db.Contractors.OrderBy(x=>x.Name).ToListAsync(), Currencies = await _db.Currencies.OrderBy(x=>x.Code).ToListAsync(),
            Units = await _db.Units.OrderBy(x=>x.Name).ToListAsync(), StockCategories = await _db.StockCategories.OrderBy(x=>x.Name).ToListAsync(), Warehouses = await _db.Warehouses.OrderBy(x=>x.Name).ToListAsync()
        });
    }

    [RequirePermission("Projects.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProject(int id, string code, string name, bool isActive = false)
    {
        code = code.Trim().ToUpperInvariant(); name = name.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return BadRequest("Code and name are required.");
        if (await _db.Projects.AnyAsync(x => x.Id != id && x.Code == code)) { TempData["Error"]="Project code already exists."; return RedirectToAction(nameof(Index), new { type="projects" }); }
        var x = id == 0 ? new Project() : await _db.Projects.FindAsync(id); if (x == null) return NotFound();
        x.Code=code; x.Name=name; x.IsActive=isActive; if(id==0) _db.Projects.Add(x); await _db.SaveChangesAsync();
        if (!await _db.ProjectWorkspaces.AnyAsync(w => w.ProjectId == x.Id))
        {
            var country = await _db.Countries.FirstOrDefaultAsync(c => c.Code == "QA") ?? await _db.Countries.FirstOrDefaultAsync(c => c.IsActive);
            if (country != null)
            {
                _db.ProjectWorkspaces.Add(new ProjectWorkspace
                {
                    ProjectId = x.Id,
                    CountryId = country.Id,
                    UniqueId = await _ids.NextAsync("PRJ"),
                    Status = "Active",
                    Priority = "Medium"
                });
                await _db.SaveChangesAsync();
                await _projectSetup.EnsureInitialSheetsAsync(x.Id);
            }
        }
        await _audit.WriteAsync(HttpContext,id==0?"Create":"Edit","Project",x.Id,x.Name);
        TempData["Success"] = id == 0 ? $"Project \"{x.Name}\" added successfully." : $"Project \"{x.Name}\" updated successfully.";
        return RedirectToAction(nameof(Index), new { type="projects" });
    }

    [RequirePermission("Departments.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDepartment(int id, string code, string name, bool isActive = false)
    {
        code=code.Trim().ToUpperInvariant(); name=name.Trim();
        if(await _db.Departments.AnyAsync(x=>x.Id!=id && x.Code==code)){TempData["Error"]="Department code already exists.";return RedirectToAction(nameof(Index),new{type="departments"});}
        var x=id==0?new Department():await _db.Departments.FindAsync(id); if(x==null)return NotFound(); x.Code=code;x.Name=name;x.IsActive=isActive;if(id==0)_db.Departments.Add(x);await _db.SaveChangesAsync();await _audit.WriteAsync(HttpContext,id==0?"Create":"Edit","Department",x.Id,x.Name);
        TempData["Success"] = id == 0 ? $"Department \"{x.Name}\" added successfully." : $"Department \"{x.Name}\" updated successfully.";
        return RedirectToAction(nameof(Index),new{type="departments"});
    }

    [RequirePermission("Contractors.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContractor(int id, string name, string? contactPerson, string? phone, string? email, bool isActive = false)
    {
        name=name.Trim(); if(string.IsNullOrWhiteSpace(name))return BadRequest("Name is required.");
        if(await _db.Contractors.AnyAsync(x=>x.Id!=id && x.Name==name)){TempData["Error"]="Contractor already exists.";return RedirectToAction(nameof(Index),new{type="contractors"});}
        var x=id==0?new Contractor():await _db.Contractors.FindAsync(id);if(x==null)return NotFound();x.Name=name;x.ContactPerson=contactPerson?.Trim();x.Phone=phone?.Trim();x.Email=email?.Trim();x.IsActive=isActive;if(id==0)_db.Contractors.Add(x);await _db.SaveChangesAsync();await _audit.WriteAsync(HttpContext,id==0?"Create":"Edit","Contractor",x.Id,x.Name);
        TempData["Success"] = id == 0 ? $"Contractor \"{x.Name}\" added successfully." : $"Contractor \"{x.Name}\" updated successfully.";
        return RedirectToAction(nameof(Index),new{type="contractors"});
    }

    [RequirePermission("MasterData.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCurrency(int id,string code,string name,string? symbol,bool isActive=false)
    {
        code=code.Trim().ToUpperInvariant();name=name.Trim();if(await _db.Currencies.AnyAsync(x=>x.Id!=id&&x.Code==code)){TempData["Error"]="Currency already exists.";return RedirectToAction(nameof(Index),new{type="currencies"});}
        var x=id==0?new Currency():await _db.Currencies.FindAsync(id);if(x==null)return NotFound();x.Code=code;x.Name=name;x.Symbol=symbol?.Trim();x.IsActive=isActive;if(id==0)_db.Currencies.Add(x);await _db.SaveChangesAsync();TempData["Success"] = id == 0 ? $"Currency {x.Code} added successfully." : $"Currency {x.Code} updated successfully.";return RedirectToAction(nameof(Index),new{type="currencies"});
    }

    [RequirePermission("MasterData.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUnit(int id,string code,string name,bool isActive=false)
    {
        code=code.Trim().ToUpperInvariant();name=name.Trim();if(await _db.Units.AnyAsync(x=>x.Id!=id&&x.Code==code)){TempData["Error"]="Unit already exists.";return RedirectToAction(nameof(Index),new{type="units"});}
        var x=id==0?new Unit():await _db.Units.FindAsync(id);if(x==null)return NotFound();x.Code=code;x.Name=name;x.IsActive=isActive;if(id==0)_db.Units.Add(x);await _db.SaveChangesAsync();TempData["Success"] = id == 0 ? $"Unit {x.Code} added successfully." : $"Unit {x.Code} updated successfully.";return RedirectToAction(nameof(Index),new{type="units"});
    }

    [RequirePermission("MasterData.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategory(int id,string name,bool isActive=false)
    {
        name=name.Trim();var x=id==0?new StockCategory():await _db.StockCategories.FindAsync(id);if(x==null)return NotFound();x.Name=name;x.IsActive=isActive;if(id==0)_db.StockCategories.Add(x);await _db.SaveChangesAsync();TempData["Success"] = id == 0 ? $"Stock category \"{x.Name}\" added successfully." : $"Stock category \"{x.Name}\" updated successfully.";return RedirectToAction(nameof(Index),new{type="categories"});
    }

    [RequirePermission("MasterData.Manage"), HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveWarehouse(int id,string name,string? location,bool isActive=false)
    {
        name=name.Trim();var x=id==0?new Warehouse():await _db.Warehouses.FindAsync(id);if(x==null)return NotFound();x.Name=name;x.Location=location?.Trim();x.IsActive=isActive;if(id==0)_db.Warehouses.Add(x);await _db.SaveChangesAsync();TempData["Success"] = id == 0 ? $"Warehouse \"{x.Name}\" added successfully." : $"Warehouse \"{x.Name}\" updated successfully.";return RedirectToAction(nameof(Index),new{type="warehouses"});
    }
}
