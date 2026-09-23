using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditService _audit;
    private readonly IWebHostEnvironment _environment;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        IPermissionService permissions,
        IAuditService audit,
        IWebHostEnvironment environment)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _db = db;
        _permissions = permissions;
        _audit = audit;
        _environment = environment;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null) => View(new LoginVm { ReturnUrl = returnUrl });

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var user = await _userManager.FindByEmailAsync(vm.Email.Trim());
        if (user == null || !user.IsActive)
        {
            ModelState.AddModelError(string.Empty, "Invalid credentials or inactive account.");
            return View(vm);
        }
        var result = await _signInManager.PasswordSignInAsync(user, vm.Password, vm.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(string.IsNullOrWhiteSpace(vm.ReturnUrl) ? "/" : vm.ReturnUrl);
        ModelState.AddModelError(string.Empty, result.IsLockedOut ? "Account temporarily locked." : "Invalid email or password.");
        return View(vm);
    }

    [Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        var departments = await _db.UserDepartments.Where(x => x.UserId == user.Id).Include(x => x.Department).Select(x => x.Department!.Name).ToListAsync();
        return View(new ProfileVm
        {
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            Roles = string.Join(", ", await _userManager.GetRolesAsync(user)),
            Departments = string.Join(", ", departments),
            ActiveSignature = await _db.UserSignatures.AsNoTracking().Where(x => x.UserId == user.Id && x.IsActive).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(),
            CanManageSignature = await CanManagePersonalSignatureAsync()
        });
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadMySignature(IFormFile? file)
    {
        if (!await CanManagePersonalSignatureAsync()) return Forbid();
        var user = await _userManager.GetUserAsync(User); if (user == null) return Challenge();
        if (file == null || file.Length == 0) { TempData["Error"] = "Select your signature image first."; return RedirectToAction(nameof(Profile)); }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (file.Length > 5 * 1024 * 1024 || (ext != ".png" && ext != ".jpg" && ext != ".jpeg"))
        { TempData["Error"] = "Signature must be PNG, JPG or JPEG and no larger than 5 MB."; return RedirectToAction(nameof(Profile)); }

        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "UserSignatures");
        Directory.CreateDirectory(folder);
        var stored = $"{Guid.NewGuid():N}{ext}";
        await using (var stream = System.IO.File.Create(Path.Combine(folder, stored))) await file.CopyToAsync(stream);

        var existing = await _db.UserSignatures.Where(x => x.UserId == user.Id && x.IsActive).ToListAsync();
        foreach (var old in existing) { old.IsActive = false; old.UpdatedAt = DateTime.UtcNow; }
        var signature = new UserSignature
        {
            UserId = user.Id,
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFileName = stored,
            ContentType = ext == ".png" ? "image/png" : "image/jpeg",
            SizeBytes = file.Length,
            StoragePath = Path.Combine("App_Data", "UserSignatures", stored).Replace('\\', '/'),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.UserSignatures.Add(signature);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "UploadMySignature", "UserSignature", signature.Id, "Personal signature uploaded/replaced by its owner.");
        TempData["Success"] = "Your signature has been saved securely. Future approvals will use this active signature when you approve and sign.";
        return RedirectToAction(nameof(Profile));
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMySignature(long id)
    {
        if (!await CanManagePersonalSignatureAsync()) return Forbid();
        var userId = _userManager.GetUserId(User); if (string.IsNullOrWhiteSpace(userId)) return Challenge();
        var signature = await _db.UserSignatures.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.IsActive);
        if (signature == null) return NotFound();
        signature.IsActive = false; signature.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "DeactivateMySignature", "UserSignature", signature.Id, "Personal signature deactivated by its owner. Historical signed records keep their original signature reference.");
        TempData["Success"] = "Your active signature was removed. Historical signed records are preserved.";
        return RedirectToAction(nameof(Profile));
    }

    [Authorize]
    [HttpGet("/Account/UserSignature/{id:long}")]
    public async Task<IActionResult> UserSignature(long id)
    {
        var signature = await _db.UserSignatures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (signature == null) return NotFound();
        var currentUserId = _userManager.GetUserId(User);
        if (signature.UserId != currentUserId &&
            !await _permissions.HasPermissionAsync(User, "ServiceApprovals.View") &&
            !await _permissions.HasPermissionAsync(User, "Documents.View")) return Forbid();

        var relative = signature.StoragePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relative));
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, signature.ContentType);
    }

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordVm());

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordVm vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        var result = await _userManager.ChangePasswordAsync(user, vm.CurrentPassword, vm.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(vm);
        }
        await _signInManager.RefreshSignInAsync(user);
        TempData["Success"] = "Password changed successfully.";
        return RedirectToAction(nameof(Profile));
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    private async Task<bool> CanManagePersonalSignatureAsync() =>
        User.IsInRole("Administrator") || User.IsInRole("Super Admin") ||
        await _permissions.HasPermissionAsync(User, "Documents.Sign") ||
        await _permissions.HasPermissionAsync(User, "ServiceApprovals.Approve");
}
