using ContractorOperations.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ContractorOperations.Web.Services;

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(ClaimsPrincipal principal, string permissionKey);
    Task<List<int>?> GetAllowedDepartmentIdsAsync(ClaimsPrincipal principal);
}

public class PermissionService : IPermissionService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<Models.ApplicationUser> _userManager;

    public PermissionService(ApplicationDbContext db, UserManager<Models.ApplicationUser> userManager)
    {
        _db = db; _userManager = userManager;
    }

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal principal, string permissionKey)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;
        var userId = _userManager.GetUserId(principal);
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (principal.IsInRole("Administrator")) return true;

        var permissionId = await _db.Permissions.Where(x => x.Key == permissionKey).Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (permissionId == null) return false;

        var direct = await _db.UserPermissions.FirstOrDefaultAsync(x => x.UserId == userId && x.PermissionId == permissionId.Value);
        if (direct != null) return direct.IsGranted;

        var roleNames = await _userManager.GetRolesAsync((await _userManager.FindByIdAsync(userId))!);
        if (roleNames.Count == 0) return false;
        var roleIds = await _db.Roles.Where(x => roleNames.Contains(x.Name!)).Select(x => x.Id).ToListAsync();
        return await _db.RolePermissions.AnyAsync(x => roleIds.Contains(x.RoleId) && x.PermissionId == permissionId.Value);
    }

    public async Task<List<int>?> GetAllowedDepartmentIdsAsync(ClaimsPrincipal principal)
    {
        if (await HasPermissionAsync(principal, "Data.AllDepartments")) return null;
        var userId = _userManager.GetUserId(principal);
        if (string.IsNullOrWhiteSpace(userId)) return new List<int>();
        return await _db.UserDepartments.Where(x => x.UserId == userId).Select(x => x.DepartmentId).ToListAsync();
    }
}
