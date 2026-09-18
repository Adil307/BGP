using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Settings.Manage")]
public class SettingsController : Controller
{
    private readonly ApplicationDbContext _db;private readonly IAuditService _audit;public SettingsController(ApplicationDbContext db,IAuditService audit){_db=db;_audit=audit;}
    public async Task<IActionResult> Index()=>View(await _db.SystemSettings.FirstAsync());
    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SystemSetting vm){if(!ModelState.IsValid)return View(vm);var x=await _db.SystemSettings.FirstAsync();x.CompanyName=vm.CompanyName.Trim();x.CompanyTagline=vm.CompanyTagline.Trim();x.SupportEmail=vm.SupportEmail?.Trim();await _db.SaveChangesAsync();await _audit.WriteAsync(HttpContext,"Update","Settings",x.Id);TempData["Success"]="Settings saved.";return RedirectToAction(nameof(Index));}
}
