using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _environment;
    public SettingsController(ApplicationDbContext db, IAuditService audit, UserManager<ApplicationUser> userManager, IWebHostEnvironment environment)
    { _db = db; _audit = audit; _userManager = userManager; _environment = environment; }

    [RequirePermission("Settings.Manage")]
    public async Task<IActionResult> Index()
    {
        ViewBag.CompanyStamp = await _db.OfficialDocumentAssets.AsNoTracking().FirstOrDefaultAsync(x => x.AssetType == "CompanyStamp" && x.IsActive);
        return View(await _db.SystemSettings.FirstAsync());
    }

    [RequirePermission("Settings.Manage")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SystemSetting vm)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.CompanyStamp = await _db.OfficialDocumentAssets.AsNoTracking().FirstOrDefaultAsync(x => x.AssetType == "CompanyStamp" && x.IsActive);
            return View(vm);
        }
        var x = await _db.SystemSettings.FirstAsync(); x.CompanyName = vm.CompanyName.Trim(); x.CompanyTagline = vm.CompanyTagline.Trim(); x.SupportEmail = vm.SupportEmail?.Trim(); x.ServiceApprovalPrefix = string.IsNullOrWhiteSpace(vm.ServiceApprovalPrefix) ? "SA" : vm.ServiceApprovalPrefix.Trim().ToUpperInvariant();
        await _db.SaveChangesAsync(); await _audit.WriteAsync(HttpContext, "Update", "Settings", x.Id); TempData["Success"] = "Settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Settings.Manage")]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadOfficialAsset(string assetType, IFormFile? file)
    {
        if (!string.Equals(assetType, "CompanyStamp", StringComparison.OrdinalIgnoreCase)) return BadRequest();
        assetType = "CompanyStamp";
        if (file == null || file.Length == 0) { TempData["Error"] = "Select an image first."; return RedirectToAction(nameof(Index)); }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (file.Length > 5 * 1024 * 1024 || (ext != ".png" && ext != ".jpg" && ext != ".jpeg"))
        { TempData["Error"] = "Company stamp must be PNG, JPG or JPEG and no larger than 5 MB."; return RedirectToAction(nameof(Index)); }

        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "OfficialAssets"); Directory.CreateDirectory(folder);
        var stored = $"{Guid.NewGuid():N}{ext}"; var physical = Path.Combine(folder, stored);
        await using (var stream = System.IO.File.Create(physical)) await file.CopyToAsync(stream);

        var old = await _db.OfficialDocumentAssets.Where(x => x.AssetType == assetType && x.IsActive).ToListAsync();
        foreach (var item in old) item.IsActive = false;
        if (old.Count > 0) await _db.SaveChangesAsync();
        var asset = new OfficialDocumentAsset
        {
            AssetType = assetType, OriginalFileName = Path.GetFileName(file.FileName), StoredFileName = stored,
            ContentType = ext == ".png" ? "image/png" : "image/jpeg", SizeBytes = file.Length,
            StoragePath = Path.Combine("App_Data", "OfficialAssets", stored).Replace('\\','/'), IsActive = true,
            UpdatedByUserId = _userManager.GetUserId(User) ?? string.Empty, UpdatedAt = DateTime.UtcNow
        };
        _db.OfficialDocumentAssets.Add(asset); await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "ReplaceOfficialAsset", "OfficialDocumentAsset", asset.Id, assetType);
        TempData["Success"] = "Official company stamp updated.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Settings.Manage")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOfficialAsset(int id)
    {
        var asset = await _db.OfficialDocumentAssets.FirstOrDefaultAsync(x => x.Id == id && x.IsActive); if (asset == null) return NotFound();
        asset.IsActive = false; asset.UpdatedAt = DateTime.UtcNow; asset.UpdatedByUserId = _userManager.GetUserId(User) ?? string.Empty;
        await _db.SaveChangesAsync(); await _audit.WriteAsync(HttpContext, "DeleteOfficialAsset", "OfficialDocumentAsset", id, asset.AssetType);
        TempData["Success"] = "Official asset deactivated. Existing audit history is preserved."; return RedirectToAction(nameof(Index));
    }

    [HttpGet("/Settings/OfficialAsset/{id:int}")]
    public async Task<IActionResult> OfficialAsset(int id)
    {
        var asset = await _db.OfficialDocumentAssets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id); if (asset == null) return NotFound();
        var relative = asset.StoragePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relative)); if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, asset.ContentType);
    }
}
