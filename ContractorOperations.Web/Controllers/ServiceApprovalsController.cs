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
using System.Data;

namespace ContractorOperations.Web.Controllers;

[Authorize]
public class ServiceApprovalsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;

    public ServiceApprovalsController(ApplicationDbContext db, IPermissionService permissions, IAuditService audit, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _permissions = permissions;
        _audit = audit;
        _userManager = userManager;
    }

    [RequirePermission("ServiceApprovals.View")]
    public async Task<IActionResult> Index(int? projectId, ServiceApprovalStatus? status, string? q)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var query = _db.ServiceApprovals.AsNoTracking()
            .Include(x => x.Project).Include(x => x.Department).Include(x => x.Currency)
            .Include(x => x.Stages).AsQueryable();
        if (allowed != null) query = query.Where(x => x.DepartmentId == null || allowed.Contains(x.DepartmentId.Value));
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId.Value);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(x => x.RequisitionNumber.Contains(search) || (x.ApprovalNumber != null && x.ApprovalNumber.Contains(search)) || x.Description.Contains(search));
        }
        ViewBag.ProjectId = projectId;
        ViewBag.Status = status;
        ViewBag.Query = q;
        ViewBag.Projects = new SelectList(await _db.Projects.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(), "Id", "Name", projectId);
        return View(await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync());
    }

    [RequirePermission("ServiceApprovals.Create")]
    [HttpGet]
    public async Task<IActionResult> Create(int? projectId, string? requisitionNo)
    {
        var resolvedProjectId = projectId ?? await _db.Projects.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).Select(x => x.Id).FirstOrDefaultAsync();
        if (resolvedProjectId <= 0) { TempData["Error"] = "Create a project before creating Service Approval."; return RedirectToAction(nameof(Index)); }
        var vm = new ServiceApprovalFormVm { ProjectId = resolvedProjectId, RequisitionNumber = requisitionNo ?? string.Empty };
        await FillFormListsAsync(vm);
        return View(vm);
    }

    [RequirePermission("ServiceApprovals.Create")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServiceApprovalFormVm vm)
    {
        if (!await RequisitionHasStatusAsync(vm.ProjectId, vm.RequisitionNumber, "Approved"))
            ModelState.AddModelError(nameof(vm.RequisitionNumber), "Service Approval can only be created after the requisition has been approved.");

        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var departmentCode = await GetRequisitionValueAsync(vm.ProjectId, vm.RequisitionNumber, "Request Department");
        var departmentId = string.IsNullOrWhiteSpace(departmentCode) ? (int?)null : await _db.Departments.AsNoTracking().Where(x => x.Code == departmentCode).Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (allowed != null && departmentId.HasValue && !allowed.Contains(departmentId.Value)) return Forbid();

        var duplicate = await _db.ServiceApprovals.AnyAsync(x => x.ProjectId == vm.ProjectId && x.RequisitionNumber == vm.RequisitionNumber && x.Status != ServiceApprovalStatus.Rejected && x.Status != ServiceApprovalStatus.Cancelled);
        if (duplicate) ModelState.AddModelError(nameof(vm.RequisitionNumber), "An active Service Approval already exists for this requisition.");

        var stageTitles = vm.StageTitles ?? new List<string>();
        if (stageTitles.Count < vm.StageCount || vm.StageCount < 3 || vm.StageCount > 4) ModelState.AddModelError(string.Empty, "Configure three or four approval/signature stages.");
        if (!ModelState.IsValid) { await FillFormListsAsync(vm); return View(vm); }

        var userId = _userManager.GetUserId(User) ?? string.Empty;
        var approval = new ServiceApproval
        {
            RequisitionNumber = vm.RequisitionNumber.Trim(), ProjectId = vm.ProjectId, DepartmentId = departmentId,
            ApprovalDate = vm.ApprovalDate.Date, ServiceAmount = vm.ServiceAmount, CurrencyId = vm.CurrencyId,
            Description = vm.Description.Trim(), ServiceType = vm.ServiceType.Trim(), VendorSelectionMethod = vm.VendorSelectionMethod?.Trim(),
            Status = ServiceApprovalStatus.PendingApproval, CurrentStage = 1, CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var stageCount = Math.Min(vm.StageCount, Math.Min(4, stageTitles.Count));
        for (var i = 0; i < stageCount; i++)
        {
            var title = string.IsNullOrWhiteSpace(stageTitles[i]) ? $"Approver {i + 1}" : stageTitles[i].Trim();
            var approverId = vm.ApproverUserIds != null && vm.ApproverUserIds.Count > i ? vm.ApproverUserIds[i] : null;
            approval.Stages.Add(new ServiceApprovalStage { StageNumber = i + 1, Title = title, ApproverUserId = string.IsNullOrWhiteSpace(approverId) ? null : approverId, Status = ApprovalStageStatus.Pending });
        }
        _db.ServiceApprovals.Add(approval);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Create", "ServiceApproval", approval.Id, $"Requisition {approval.RequisitionNumber}; amount {approval.ServiceAmount}");
        await NotifyCurrentStageAsync(approval.Id);
        TempData["Success"] = "Service Approval created. Status: Waiting Approval. The first assigned approver has been notified.";
        return RedirectToAction(nameof(Details), new { id = approval.Id });
    }

    [RequirePermission("ServiceApprovals.View")]
    public async Task<IActionResult> Details(long id)
    {
        var vm = await BuildDetailsVmAsync(id);
        if (vm == null) return NotFound();
        if (!await CanAccessAsync(vm.Approval.DepartmentId)) return Forbid();
        return View(vm);
    }

    [RequirePermission("ServiceApprovals.Approve")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(long id, long stageId, bool approve, string? comment)
    {
        var approval = await _db.ServiceApprovals.Include(x => x.Stages).FirstOrDefaultAsync(x => x.Id == id);
        if (approval == null) return NotFound();
        if (!await CanAccessAsync(approval.DepartmentId)) return Forbid();
        if (approval.Status != ServiceApprovalStatus.PendingApproval) { TempData["Error"] = "This Service Approval is no longer waiting for a decision."; return RedirectToAction(nameof(Details), new { id }); }

        var stage = approval.Stages.FirstOrDefault(x => x.Id == stageId);
        if (stage == null || stage.StageNumber != approval.CurrentStage || stage.Status != ApprovalStageStatus.Pending)
        { TempData["Error"] = "Only the current pending approval stage can be decided."; return RedirectToAction(nameof(Details), new { id }); }

        var currentUserId = _userManager.GetUserId(User) ?? string.Empty;
        var elevated = User.IsInRole("Administrator") || User.IsInRole("Super Admin");
        if (!string.IsNullOrWhiteSpace(stage.ApproverUserId) && stage.ApproverUserId != currentUserId && !elevated) return Forbid();
        if (!approve && string.IsNullOrWhiteSpace(comment))
        { TempData["Error"] = "A rejection reason is required."; return RedirectToAction(nameof(Details), new { id }); }

        UserSignature? personalSignature = null;
        if (approve)
        {
            personalSignature = await _db.UserSignatures.Where(x => x.UserId == currentUserId && x.IsActive).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync();
            if (personalSignature == null)
            {
                TempData["Error"] = "Upload your personal approval signature in My Profile before using Approve & Sign.";
                return RedirectToAction(nameof(Details), new { id });
            }
        }

        stage.Status = approve ? ApprovalStageStatus.Approved : ApprovalStageStatus.Rejected;
        stage.Comment = comment?.Trim(); stage.DecidedByUserId = currentUserId; stage.DecidedAt = DateTime.UtcNow;
        stage.UserSignatureId = approve ? personalSignature!.Id : null;
        stage.SignatureAppliedAt = approve ? DateTime.UtcNow : null;
        approval.UpdatedAt = DateTime.UtcNow;

        if (!approve)
        {
            approval.Status = ServiceApprovalStatus.Rejected;
            approval.RejectionReason = comment?.Trim();
        }
        else
        {
            var next = approval.Stages.Where(x => x.StageNumber > stage.StageNumber && x.Status == ApprovalStageStatus.Pending).OrderBy(x => x.StageNumber).FirstOrDefault();
            if (next == null)
            {
                approval.Status = ServiceApprovalStatus.Approved;
                approval.CurrentStage = stage.StageNumber;
                approval.FinalApprovedAt = DateTime.UtcNow;
                approval.ApprovalNumber ??= await NextApprovalNumberAsync();
            }
            else approval.CurrentStage = next.StageNumber;
        }

        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, approve ? "ApproveAndSign" : "Reject", "ServiceApproval", approval.Id, approve ? $"Stage {stage.StageNumber}: {stage.Title}; personal signature {stage.UserSignatureId}; {comment}" : $"Stage {stage.StageNumber}: {stage.Title}; rejection reason: {comment}");
        if (approval.Status == ServiceApprovalStatus.PendingApproval) await NotifyCurrentStageAsync(approval.Id);
        else await NotifyCreatorAsync(approval, approve ? $"Service Approval {approval.ApprovalNumber} is fully approved." : $"Service Approval for {approval.RequisitionNumber} was rejected: {comment}");
        TempData["Success"] = approve ? (approval.Status == ServiceApprovalStatus.Approved ? $"Final approval completed. Service Approval No. {approval.ApprovalNumber} generated." : "Approval recorded. The next approval stage is now active.") : "Service Approval rejected. The record remains available for audit history.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.Sign")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyOfficialAsset(long id, string assetType)
    {
        var approval = await _db.ServiceApprovals.FirstOrDefaultAsync(x => x.Id == id);
        if (approval == null) return NotFound();
        if (!await CanAccessAsync(approval.DepartmentId)) return Forbid();
        if (approval.Status != ServiceApprovalStatus.Approved)
        { TempData["Error"] = "The company stamp can be applied only after final Service Approval."; return RedirectToAction(nameof(Details), new { id }); }
        if (!string.Equals(assetType, "CompanyStamp", StringComparison.OrdinalIgnoreCase)) return BadRequest();

        var asset = await _db.OfficialDocumentAssets.AsNoTracking().FirstOrDefaultAsync(x => x.AssetType == "CompanyStamp" && x.IsActive);
        if (asset == null) { TempData["Error"] = "No active Company Stamp is configured in Settings."; return RedirectToAction(nameof(Details), new { id }); }
        var exists = await _db.DocumentAssetUsages.AnyAsync(x => !x.IsRemoved && x.OfficialDocumentAssetId == asset.Id && x.ReferenceType == "ServiceApproval" && x.ReferenceId == id.ToString());
        if (!exists)
        {
            _db.DocumentAssetUsages.Add(new DocumentAssetUsage { OfficialDocumentAssetId = asset.Id, ReferenceType = "ServiceApproval", ReferenceId = id.ToString(), ReferenceNumber = approval.ApprovalNumber, AppliedByUserId = _userManager.GetUserId(User) ?? string.Empty, AppliedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
            await _audit.WriteAsync(HttpContext, "ApplyCompanyStamp", "ServiceApproval", id, $"Company stamp asset {asset.Id}; applied by {User.Identity?.Name}");
        }
        TempData["Success"] = "Official company stamp applied and audit-recorded.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("ServiceApprovals.View")]
    public async Task<IActionResult> Print(long id)
    {
        var vm = await BuildDetailsVmAsync(id);
        if (vm == null) return NotFound();
        if (!await CanAccessAsync(vm.Approval.DepartmentId)) return Forbid();
        return View(vm);
    }

    private async Task<ServiceApprovalDetailsVm?> BuildDetailsVmAsync(long id)
    {
        var approval = await _db.ServiceApprovals.AsNoTracking().Include(x => x.Project).Include(x => x.Department).Include(x => x.Currency).Include(x => x.Stages).FirstOrDefaultAsync(x => x.Id == id);
        if (approval == null) return null;
        var docs = await _db.DocumentRecords.AsNoTracking().Where(x => !x.IsDeleted && x.ReferenceType == "ServiceApproval" && x.ReferenceId == id.ToString()).OrderByDescending(x => x.UploadedAt).ToListAsync();
        var usages = await _db.DocumentAssetUsages.AsNoTracking().Include(x => x.OfficialDocumentAsset).Where(x => !x.IsRemoved && x.ReferenceType == "ServiceApproval" && x.ReferenceId == id.ToString()).OrderBy(x => x.AppliedAt).ToListAsync();
        var ids = approval.Stages.SelectMany(x => new[] { x.ApproverUserId, x.DecidedByUserId }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        ids.Add(approval.CreatedByUserId);
        ids.AddRange(usages.Select(x => x.AppliedByUserId));
        var users = await _db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.FullName) ? (x.Email ?? x.UserName ?? x.Id) : x.FullName);
        var history = await _db.AuditLogs.AsNoTracking().Where(x => x.EntityName == "ServiceApproval" && x.EntityId == id.ToString()).OrderByDescending(x => x.TimestampUtc).Take(100).ToListAsync();
        return new ServiceApprovalDetailsVm
        {
            Approval = approval,
            RequisitionItems = await GetRequisitionItemsAsync(approval.ProjectId, approval.RequisitionNumber),
            Documents = docs, AssetUsages = usages, UserNames = users, AuditHistory = history,
            HasCompanyStamp = usages.Any(x => x.OfficialDocumentAsset != null && x.OfficialDocumentAsset.AssetType == "CompanyStamp"),
            CurrentUserHasSignature = await _db.UserSignatures.AsNoTracking().AnyAsync(x => x.UserId == (_userManager.GetUserId(User) ?? string.Empty) && x.IsActive)
        };
    }

    private async Task FillFormListsAsync(ServiceApprovalFormVm vm)
    {
        var reqs = await GetApprovedRequisitionsAsync(vm.ProjectId);
        vm.Requisitions = reqs.Select(x => new SelectListItem(x, x, string.Equals(x, vm.RequisitionNumber, StringComparison.OrdinalIgnoreCase))).ToList();
        vm.Currencies = await _db.Currencies.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Code).Select(x => new SelectListItem(x.Code, x.Id.ToString())).ToListAsync();
        var users = new Dictionary<string, ApplicationUser>();
        foreach (var role in new[] { "Project Manager", "Department Head", "Administrator", "Super Admin" })
            foreach (var user in await _userManager.GetUsersInRoleAsync(role)) if (user.IsActive) users[user.Id] = user;
        vm.Approvers = users.Values.OrderBy(x => x.FullName).Select(x => new SelectListItem(string.IsNullOrWhiteSpace(x.FullName) ? x.Email : $"{x.FullName} · {x.Email}", x.Id)).ToList();
    }

    private async Task<List<string>> GetApprovedRequisitionsAsync(int projectId)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return new();
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var reqCol = columns.FirstOrDefault(x => x.Name == "Requisition No"); var statusCol = columns.FirstOrDefault(x => x.Name == "Status");
        if (reqCol == null || statusCol == null) return new();
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        return rows.Where(x => string.Equals(Cell(x, statusCol.Id), "Approved", StringComparison.OrdinalIgnoreCase)).Select(x => Cell(x, reqCol.Id)).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x).ToList();
    }

    private async Task<bool> RequisitionHasStatusAsync(int projectId, string requisitionNo, string status)
    {
        if (string.IsNullOrWhiteSpace(requisitionNo)) return false;
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return false;
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var reqCol = columns.FirstOrDefault(x => x.Name == "Requisition No"); var statusCol = columns.FirstOrDefault(x => x.Name == "Status");
        if (reqCol == null || statusCol == null) return false;
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var matching = rows.Where(x => string.Equals(Cell(x, reqCol.Id), requisitionNo, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count > 0 && matching.All(x => string.Equals(Cell(x, statusCol.Id), status, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> GetRequisitionValueAsync(int projectId, string requisitionNo, string columnName)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return null;
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var reqCol = columns.FirstOrDefault(x => x.Name == "Requisition No"); var target = columns.FirstOrDefault(x => x.Name == columnName);
        if (reqCol == null || target == null) return null;
        var row = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.Cells.Any(c => c.SheetColumnId == reqCol.Id && c.Value == requisitionNo));
        return row == null ? null : Cell(row, target.Id);
    }

    private async Task<List<RequisitionItemVm>> GetRequisitionItemsAsync(int projectId, string requisitionNo)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return new();
        var cols = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        int? Id(string n) => cols.FirstOrDefault(x => x.Name == n)?.Id;
        var req = Id("Requisition No"); if (!req.HasValue) return new();
        var item = Id("Item No"); var desc = Id("Description"); var unit = Id("Unit"); var qty = Id("Qty");
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id && x.Cells.Any(c => c.SheetColumnId == req.Value && c.Value == requisitionNo)).ToListAsync();
        return rows.OrderBy(x => int.TryParse(item.HasValue ? Cell(x, item.Value) : null, out var n) ? n : int.MaxValue)
            .Select(x => new RequisitionItemVm { ItemNumber = item.HasValue ? Cell(x, item.Value) ?? "" : "", Description = desc.HasValue ? Cell(x, desc.Value) ?? "" : "", Unit = unit.HasValue ? Cell(x, unit.Value) ?? "" : "", Quantity = qty.HasValue ? Cell(x, qty.Value) ?? "" : "" }).ToList();
    }

    private async Task<string> NextApprovalNumberAsync()
    {
        var year = DateTime.UtcNow.Year;
        var configured = await _db.SystemSettings.AsNoTracking().Select(x => x.ServiceApprovalPrefix).FirstOrDefaultAsync();
        var prefixCode = string.IsNullOrWhiteSpace(configured) ? "SA" : configured.Trim().ToUpperInvariant();
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var seq = await _db.BusinessSequences.SingleOrDefaultAsync(x => x.Prefix == prefixCode && x.Year == year);
        if (seq == null)
        {
            var numberPrefix = $"{prefixCode}-{year}-";
            var existing = await _db.ServiceApprovals.AsNoTracking().Where(x => x.ApprovalNumber != null && x.ApprovalNumber.StartsWith(numberPrefix)).Select(x => x.ApprovalNumber!).ToListAsync();
            var max = existing.Select(x => int.TryParse(x[numberPrefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
            seq = new BusinessSequence { Prefix = prefixCode, Year = year, LastNumber = max + 1 }; _db.BusinessSequences.Add(seq);
        }
        else seq.LastNumber++;
        await _db.SaveChangesAsync(); await tx.CommitAsync();
        return $"{prefixCode}-{year}-{seq.LastNumber:0000}";
    }

    private async Task NotifyCurrentStageAsync(long id)
    {
        var approval = await _db.ServiceApprovals.AsNoTracking().Include(x => x.Stages).FirstAsync(x => x.Id == id);
        var stage = approval.Stages.FirstOrDefault(x => x.StageNumber == approval.CurrentStage);
        if (stage == null || string.IsNullOrWhiteSpace(stage.ApproverUserId)) return;
        _db.WorkflowNotifications.Add(new WorkflowNotification { UserId = stage.ApproverUserId, Title = "Service Approval waiting for your decision", Message = $"Stage {stage.StageNumber} ({stage.Title}) is waiting for your Accept or Reject decision for requisition {approval.RequisitionNumber}.", Url = Url.Action(nameof(Details), "ServiceApprovals", new { id = approval.Id }), CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    private async Task NotifyCreatorAsync(ServiceApproval approval, string message)
    {
        if (string.IsNullOrWhiteSpace(approval.CreatedByUserId)) return;
        _db.WorkflowNotifications.Add(new WorkflowNotification { UserId = approval.CreatedByUserId, Title = "Service Approval update", Message = message, Url = Url.Action(nameof(Details), "ServiceApprovals", new { id = approval.Id }), CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    private async Task<bool> CanAccessAsync(int? departmentId)
    {
        if (!departmentId.HasValue) return true;
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        return allowed == null || allowed.Contains(departmentId.Value);
    }

    private static string? Cell(SheetRow row, int columnId) => row.Cells.FirstOrDefault(x => x.SheetColumnId == columnId)?.Value?.Trim();
}
