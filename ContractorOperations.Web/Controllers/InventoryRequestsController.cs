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

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Inventory.Requests")]
public class InventoryRequestsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IBusinessIdService _ids;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;

    public InventoryRequestsController(ApplicationDbContext db, IPermissionService permissions, IBusinessIdService ids, IAuditService audit, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _permissions = permissions;
        _ids = ids;
        _audit = audit;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(InventoryRequestStatus? status, int? projectId)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var query = _db.InventoryRequests.AsNoTracking()
            .Include(x => x.Department)
            .Include(x => x.Project)
            .Include(x => x.RequestedByUser)
            .Include(x => x.ApprovedByUser)
            .AsQueryable();
        if (allowed != null) query = query.Where(x => allowed.Contains(x.DepartmentId));
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId.Value);

        var allForStats = _db.InventoryRequests.AsNoTracking().AsQueryable();
        if (allowed != null) allForStats = allForStats.Where(x => allowed.Contains(x.DepartmentId));

        return View(new InventoryRequestListVm
        {
            Requests = await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(),
            Total = await allForStats.CountAsync(),
            Pending = await allForStats.CountAsync(x => x.Status == InventoryRequestStatus.Pending),
            Accepted = await allForStats.CountAsync(x => x.Status == InventoryRequestStatus.Accepted),
            Rejected = await allForStats.CountAsync(x => x.Status == InventoryRequestStatus.Rejected),
            CanApprove = await _permissions.HasPermissionAsync(User, "Inventory.Approve")
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? projectId)
    {
        var vm = new InventoryRequestFormVm { ProjectId = projectId, RequestDate = DateTime.Today, Quantity = 1 };
        await FillLists(vm);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(InventoryRequestFormVm vm)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        if (allowed != null && !allowed.Contains(vm.DepartmentId)) ModelState.AddModelError(nameof(vm.DepartmentId), "You do not have access to this department.");
        if (vm.ProjectId.HasValue && !await _db.Projects.AnyAsync(x => x.Id == vm.ProjectId.Value && x.IsActive)) ModelState.AddModelError(nameof(vm.ProjectId), "Project not found.");

        if (!ModelState.IsValid)
        {
            await FillLists(vm);
            return View(vm);
        }

        var request = new InventoryRequest
        {
            RequestNumber = await _ids.NextAsync("INV"),
            ItemName = vm.ItemName.Trim(),
            Category = vm.Category?.Trim(),
            Quantity = vm.Quantity,
            Unit = vm.Unit.Trim().ToUpperInvariant(),
            DepartmentId = vm.DepartmentId,
            ProjectId = vm.ProjectId,
            RequestedByUserId = _userManager.GetUserId(User)!,
            RequestDate = vm.RequestDate,
            RequiredDate = vm.RequiredDate,
            Purpose = vm.Purpose.Trim(),
            Notes = vm.Notes?.Trim(),
            Status = InventoryRequestStatus.Pending
        };
        _db.InventoryRequests.Add(request);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Create", "InventoryRequest", request.Id, $"{request.RequestNumber} - {request.ItemName} - Qty {request.Quantity}");
        TempData["Success"] = $"Inventory request {request.RequestNumber} submitted.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Inventory.Approve")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(long id, decimal approvedQuantity)
    {
        var request = await _db.InventoryRequests.FindAsync(id);
        if (request == null) return NotFound();
        if (request.Status != InventoryRequestStatus.Pending)
        {
            TempData["Error"] = "Only pending requests can be accepted.";
            return RedirectToAction(nameof(Index));
        }
        if (approvedQuantity <= 0) approvedQuantity = request.Quantity;
        request.Status = InventoryRequestStatus.Accepted;
        request.ApprovedQuantity = approvedQuantity;
        request.ApprovedByUserId = _userManager.GetUserId(User);
        request.ApprovalDate = DateTime.UtcNow;
        request.RejectionReason = null;
        request.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Accept", "InventoryRequest", request.Id, $"{request.RequestNumber}; Approved qty {approvedQuantity}");
        TempData["Success"] = $"{request.RequestNumber} accepted.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Inventory.Approve")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long id, string rejectionReason)
    {
        var request = await _db.InventoryRequests.FindAsync(id);
        if (request == null) return NotFound();
        if (request.Status != InventoryRequestStatus.Pending)
        {
            TempData["Error"] = "Only pending requests can be rejected.";
            return RedirectToAction(nameof(Index));
        }
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            TempData["Error"] = "A rejection reason is required.";
            return RedirectToAction(nameof(Index));
        }

        request.Status = InventoryRequestStatus.Rejected;
        request.RejectionReason = rejectionReason.Trim();
        request.ApprovedByUserId = _userManager.GetUserId(User);
        request.ApprovalDate = DateTime.UtcNow;
        request.ApprovedQuantity = null;
        request.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Reject", "InventoryRequest", request.Id, $"{request.RequestNumber}; {request.RejectionReason}");
        TempData["Success"] = $"{request.RequestNumber} rejected.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Inventory.Approve")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(long id, InventoryRequestStatus status)
    {
        var request = await _db.InventoryRequests.FindAsync(id);
        if (request == null) return NotFound();
        if (status is not (InventoryRequestStatus.Issued or InventoryRequestStatus.Returned)) return BadRequest();
        if (status == InventoryRequestStatus.Issued && request.Status != InventoryRequestStatus.Accepted)
        {
            TempData["Error"] = "Only accepted requests can be marked Issued.";
            return RedirectToAction(nameof(Index));
        }
        if (status == InventoryRequestStatus.Returned && request.Status != InventoryRequestStatus.Issued)
        {
            TempData["Error"] = "Only issued requests can be marked Returned.";
            return RedirectToAction(nameof(Index));
        }
        request.Status = status;
        request.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, status.ToString(), "InventoryRequest", request.Id, request.RequestNumber);
        TempData["Success"] = $"{request.RequestNumber} marked {status}.";
        return RedirectToAction(nameof(Index));
    }

    private async Task FillLists(InventoryRequestFormVm vm)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var departments = _db.Departments.AsNoTracking().Where(x => x.IsActive).AsQueryable();
        if (allowed != null) departments = departments.Where(x => allowed.Contains(x.Id));
        vm.Departments = await departments.OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
        vm.Projects = await _db.Projects.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync();
    }
}
