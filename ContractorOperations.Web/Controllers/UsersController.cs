using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ContractorOperations.Web.Controllers;

[Authorize]
public class UsersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IAuditService _audit;
    public UsersController(ApplicationDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, IAuditService audit)
    { _db = db; _userManager = userManager; _roleManager = roleManager; _audit = audit; }

    [RequirePermission("Users.View")]
    public async Task<IActionResult> Index()
    {
        var users = await _db.Users.Include(x => x.UserDepartments).ThenInclude(x => x.Department).OrderBy(x => x.FullName).ToListAsync();
        var list = new List<UserListVm>();
        foreach (var u in users)
            list.Add(new UserListVm { User=u, Roles=string.Join(", ", await _userManager.GetRolesAsync(u)), Departments=string.Join(", ", u.UserDepartments.Select(x=>x.Department!.Name)) });
        return View(list);
    }

    [RequirePermission("Users.Manage")]
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var vm = new UserFormVm(); await FillLists(vm); return View("UserForm", vm);
    }

    [RequirePermission("Users.Manage")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserFormVm vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Password) || vm.Password.Length < 8) ModelState.AddModelError(nameof(vm.Password), "A temporary password of at least 8 characters is required.");
        if (await _userManager.FindByEmailAsync(vm.Email.Trim()) != null) ModelState.AddModelError(nameof(vm.Email), "Email already exists.");
        if (!ModelState.IsValid) { await FillLists(vm); return View("UserForm", vm); }
        var user = new ApplicationUser { UserName=vm.Email.Trim(), Email=vm.Email.Trim(), EmailConfirmed=true, FullName=vm.FullName.Trim(), IsActive=vm.IsActive };
        var result = await _userManager.CreateAsync(user, vm.Password!);
        if (!result.Succeeded) { foreach(var e in result.Errors) ModelState.AddModelError(string.Empty,e.Description); await FillLists(vm); return View("UserForm", vm); }
        await _userManager.AddToRoleAsync(user, vm.Role);
        foreach(var d in vm.DepartmentIds.Distinct()) _db.UserDepartments.Add(new UserDepartment { UserId=user.Id, DepartmentId=d });
        await _db.SaveChangesAsync(); await _audit.WriteAsync(HttpContext,"Create","User",user.Id,user.Email);
        TempData["Success"]="User created successfully."; return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Users.Manage")]
    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var user=await _db.Users.Include(x=>x.UserDepartments).FirstOrDefaultAsync(x=>x.Id==id); if(user==null)return NotFound();
        var vm=new UserFormVm { Id=user.Id, FullName=user.FullName, Email=user.Email??"", Role=(await _userManager.GetRolesAsync(user)).FirstOrDefault()??"Department User", DepartmentIds=user.UserDepartments.Select(x=>x.DepartmentId).ToList(), IsActive=user.IsActive };
        await FillLists(vm);return View("UserForm",vm);
    }

    [RequirePermission("Users.Manage")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, UserFormVm vm)
    {
        if(id!=vm.Id)return BadRequest();var user=await _db.Users.Include(x=>x.UserDepartments).FirstOrDefaultAsync(x=>x.Id==id);if(user==null)return NotFound();
        if(await _db.Users.AnyAsync(x=>x.Id!=id&&x.Email==vm.Email.Trim()))ModelState.AddModelError(nameof(vm.Email),"Email already exists.");
        if(!ModelState.IsValid){await FillLists(vm);return View("UserForm",vm);} user.FullName=vm.FullName.Trim();user.Email=vm.Email.Trim();user.UserName=vm.Email.Trim();user.IsActive=vm.IsActive;
        var roles=await _userManager.GetRolesAsync(user); if(roles.Any()) await _userManager.RemoveFromRolesAsync(user,roles); await _userManager.AddToRoleAsync(user,vm.Role);
        _db.UserDepartments.RemoveRange(user.UserDepartments); foreach(var d in vm.DepartmentIds.Distinct())_db.UserDepartments.Add(new UserDepartment{UserId=id,DepartmentId=d});
        await _userManager.UpdateAsync(user);await _db.SaveChangesAsync();await _audit.WriteAsync(HttpContext,"Edit","User",user.Id,user.Email);TempData["Success"]="User updated.";return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Users.Manage")]
    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string id)
    {
        var user=await _userManager.FindByIdAsync(id);if(user==null)return NotFound();var token=await _userManager.GeneratePasswordResetTokenAsync(user);
        var temp=$"Tmp!{RandomNumberGenerator.GetInt32(100000,999999)}aA";var result=await _userManager.ResetPasswordAsync(user,token,temp);
        TempData[result.Succeeded?"Success":"Error"]=result.Succeeded?$"Temporary password: {temp}":string.Join("; ",result.Errors.Select(x=>x.Description));
        if(result.Succeeded)await _audit.WriteAsync(HttpContext,"ResetPassword","User",user.Id,user.Email);return RedirectToAction(nameof(Index));
    }

    private async Task FillLists(UserFormVm vm)
    {
        vm.Roles=await _roleManager.Roles.OrderBy(x=>x.Name).Select(x=>new SelectListItem(x.Name!,x.Name!)).ToListAsync();
        vm.Departments=await _db.Departments.Where(x=>x.IsActive).OrderBy(x=>x.Name).Select(x=>new SelectListItem(x.Name,x.Id.ToString())).ToListAsync();
    }
}
