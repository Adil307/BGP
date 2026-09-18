using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Permissions.Manage")]
public class PermissionsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IAuditService _audit;
    public PermissionsController(ApplicationDbContext db, RoleManager<IdentityRole> roles, UserManager<ApplicationUser> users, IAuditService audit)
    { _db=db;_roles=roles;_users=users;_audit=audit; }

    public async Task<IActionResult> Index(string? roleId)
    {
        var roles=await _roles.Roles.OrderBy(x=>x.Name).ToListAsync();ViewBag.Roles=roles;
        var role=string.IsNullOrWhiteSpace(roleId)?roles.FirstOrDefault():roles.FirstOrDefault(x=>x.Id==roleId);if(role==null)return View(new RolePermissionVm());
        return View(new RolePermissionVm { RoleId=role.Id, RoleName=role.Name!, Permissions=await _db.Permissions.OrderBy(x=>x.Group).ThenBy(x=>x.Name).ToListAsync(), GrantedPermissionIds=(await _db.RolePermissions.Where(x=>x.RoleId==role.Id).Select(x=>x.PermissionId).ToListAsync()).ToHashSet() });
    }

    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRole(string roleId,List<int>? permissionIds)
    {
        var role=await _roles.FindByIdAsync(roleId);if(role==null)return NotFound();var current=await _db.RolePermissions.Where(x=>x.RoleId==roleId).ToListAsync();_db.RolePermissions.RemoveRange(current);
        foreach(var id in (permissionIds??new()).Distinct())_db.RolePermissions.Add(new RolePermission{RoleId=roleId,PermissionId=id});await _db.SaveChangesAsync();await _audit.WriteAsync(HttpContext,"Update","RolePermissions",roleId,role.Name);TempData["Success"]="Role permissions updated.";return RedirectToAction(nameof(Index),new{roleId});
    }

    public async Task<IActionResult> UserAccess(string id)
    {
        var user=await _users.FindByIdAsync(id);if(user==null)return NotFound();var perms=await _db.Permissions.OrderBy(x=>x.Group).ThenBy(x=>x.Name).ToListAsync();var overrides=await _db.UserPermissions.Where(x=>x.UserId==id).ToDictionaryAsync(x=>x.PermissionId,x=>x.IsGranted?"allow":"deny");
        return View("User", new UserPermissionVm{UserId=id,UserName=user.FullName,Permissions=perms,Decisions=overrides});
    }

    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUser(string userId,IFormCollection form)
    {
        var user=await _users.FindByIdAsync(userId);if(user==null)return NotFound();var all=await _db.Permissions.Select(x=>x.Id).ToListAsync();var old=await _db.UserPermissions.Where(x=>x.UserId==userId).ToListAsync();_db.UserPermissions.RemoveRange(old);
        foreach(var id in all){var decision=form[$"permission_{id}"].ToString();if(decision=="allow"||decision=="deny")_db.UserPermissions.Add(new UserPermission{UserId=userId,PermissionId=id,IsGranted=decision=="allow"});}
        await _db.SaveChangesAsync();await _audit.WriteAsync(HttpContext,"Update","UserPermissions",userId,user.Email);TempData["Success"]="User permission overrides updated.";return RedirectToAction(nameof(UserAccess),new{id=userId});
    }
}
