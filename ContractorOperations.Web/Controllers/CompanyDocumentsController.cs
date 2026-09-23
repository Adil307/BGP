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
public class CompanyDocumentsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDocumentRenderingService _renderer;

    public CompanyDocumentsController(ApplicationDbContext db, IPermissionService permissions, IAuditService audit,
        UserManager<ApplicationUser> userManager, IDocumentRenderingService renderer)
    {
        _db = db; _permissions = permissions; _audit = audit; _userManager = userManager; _renderer = renderer;
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Index(string? q)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var query = _db.GeneratedCompanyDocuments.AsNoTracking().Include(x => x.DocumentTemplate).AsQueryable();
        if (allowed != null) query = query.Where(x => x.DepartmentId == null || allowed.Contains(x.DepartmentId.Value));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(x => x.Title.Contains(search) || (x.ReferenceNumber != null && x.ReferenceNumber.Contains(search)) || (x.RequisitionNumber != null && x.RequisitionNumber.Contains(search)));
        }
        return View(new CompanyDocumentLibraryVm
        {
            Query = q,
            Documents = await query.OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync(),
            Templates = await _db.DocumentTemplates.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DocumentType).ThenBy(x => x.Name).ToListAsync()
        });
    }

    [RequirePermission("Documents.Generate")]
    [HttpGet]
    public async Task<IActionResult> Create(int? templateId = null, long? serviceApprovalId = null, int? jobId = null, string? requisitionNumber = null, int? projectId = null)
    {
        var context = await ResolveContextAsync(serviceApprovalId, jobId, requisitionNumber, projectId);
        if (context != null && !await CanAccessAsync(context.DepartmentId)) return Forbid();
        var vm = new CompanyDocumentCreateVm
        {
            DocumentTemplateId = templateId ?? 0,
            ServiceApprovalId = serviceApprovalId,
            JobId = jobId,
            RequisitionNumber = requisitionNumber,
            ProjectId = projectId ?? context?.ProjectId,
            LinkedReferenceLabel = context?.Label ?? "Standalone company document",
            Title = context == null ? "Company Document" : $"{context.Label} Document"
        };
        await FillTemplateListAsync(vm);
        return View(vm);
    }

    [RequirePermission("Documents.Generate")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CompanyDocumentCreateVm vm)
    {
        var template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vm.DocumentTemplateId && x.IsActive);
        if (template == null) ModelState.AddModelError(nameof(vm.DocumentTemplateId), "Select an active company document template.");
        var context = await ResolveContextAsync(vm.ServiceApprovalId, vm.JobId, vm.RequisitionNumber, vm.ProjectId);
        if (context != null && !await CanAccessAsync(context.DepartmentId)) return Forbid();
        if (!ModelState.IsValid)
        {
            vm.LinkedReferenceLabel = context?.Label ?? "Standalone company document";
            await FillTemplateListAsync(vm);
            return View(vm);
        }

        var userId = _userManager.GetUserId(User) ?? string.Empty;
        var refId = context?.ReferenceId ?? Guid.NewGuid().ToString("N");
        var document = new GeneratedCompanyDocument
        {
            DocumentTemplateId = template!.Id,
            Title = vm.Title.Trim(),
            ReferenceType = context?.ReferenceType ?? "General",
            ReferenceId = refId,
            ReferenceNumber = context?.ReferenceNumber,
            RequisitionNumber = context?.RequisitionNumber,
            ProjectId = context?.ProjectId ?? vm.ProjectId,
            DepartmentId = context?.DepartmentId,
            ServiceApprovalId = vm.ServiceApprovalId,
            JobId = vm.JobId,
            TemplateHtmlSnapshot = template.HtmlContent,
            EditableFieldLabelsSnapshot = template.EditableFieldLabels,
            SignatureSlotLabelsSnapshot = template.SignatureSlotLabels,
            Status = GeneratedDocumentStatus.Draft,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.GeneratedCompanyDocuments.Add(document);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "CreateDraft", "CompanyDocument", document.Id, $"Template {template.Name}; reference {document.ReferenceType} {document.ReferenceNumber ?? document.ReferenceId}");
        TempData["Success"] = "Company document draft created from the master template. The master template remains unchanged.";
        return RedirectToAction(nameof(Details), new { id = document.Id });
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Details(long id)
    {
        var vm = await BuildDetailsVmAsync(id); if (vm == null) return NotFound();
        if (!await CanAccessAsync(vm.Document.DepartmentId)) return Forbid();
        return View(vm);
    }

    [RequirePermission("Documents.Generate")]
    [HttpGet]
    public async Task<IActionResult> Edit(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (!await CanEditAsync(document)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) { TempData["Error"] = "Finalized or voided documents cannot be edited."; return RedirectToAction(nameof(Details), new { id }); }
        return View(new CompanyDocumentEditVm
        {
            Id = document.Id, Title = document.Title, CustomText1 = document.CustomText1, CustomText2 = document.CustomText2,
            CustomText3 = document.CustomText3, CustomText4 = document.CustomText4,
            EditableFieldLabels = ParseLabels(document.EditableFieldLabelsSnapshot)
        });
    }

    [RequirePermission("Documents.Generate")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(long id, CompanyDocumentEditVm vm)
    {
        if (id != vm.Id) return BadRequest();
        var document = await _db.GeneratedCompanyDocuments.FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (!await CanEditAsync(document)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) { TempData["Error"] = "Finalized or voided documents cannot be edited."; return RedirectToAction(nameof(Details), new { id }); }
        if (!ModelState.IsValid) { vm.EditableFieldLabels = ParseLabels(document.EditableFieldLabelsSnapshot); return View(vm); }
        document.Title = vm.Title.Trim();
        document.CustomText1 = vm.CustomText1?.Trim(); document.CustomText2 = vm.CustomText2?.Trim();
        document.CustomText3 = vm.CustomText3?.Trim(); document.CustomText4 = vm.CustomText4?.Trim();
        document.UpdatedByUserId = _userManager.GetUserId(User) ?? string.Empty; document.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "EditDraft", "CompanyDocument", document.Id, "Editable document fields updated; master template was not changed.");
        TempData["Success"] = "Draft updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Preview(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        ViewBag.Document = document;
        ViewBag.RenderedHtml = document.Status == GeneratedDocumentStatus.Finalized && !string.IsNullOrWhiteSpace(document.FinalHtmlSnapshot)
            ? document.FinalHtmlSnapshot
            : await _renderer.RenderGeneratedAsync(document, _userManager.GetUserId(User));
        return View();
    }

    [RequirePermission("Documents.Print")]
    public async Task<IActionResult> Print(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        ViewBag.Document = document;
        ViewBag.RenderedHtml = document.Status == GeneratedDocumentStatus.Finalized && !string.IsNullOrWhiteSpace(document.FinalHtmlSnapshot)
            ? document.FinalHtmlSnapshot
            : await _renderer.RenderGeneratedAsync(document, _userManager.GetUserId(User));
        await _audit.WriteAsync(HttpContext, "Print", "CompanyDocument", id, document.Title);
        return View();
    }

    [RequirePermission("Documents.Sign")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplySignature(long id, int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > 4) return BadRequest();
        var document = await _db.GeneratedCompanyDocuments.Include(x => x.Signatures).FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) { TempData["Error"] = "Signatures can only be changed while the document is a draft."; return RedirectToAction(nameof(Details), new { id }); }
        var labels = ParseLabels(document.SignatureSlotLabelsSnapshot);
        if (slotNumber > labels.Length) { TempData["Error"] = "This template does not define that signature slot."; return RedirectToAction(nameof(Details), new { id }); }
        var userId = _userManager.GetUserId(User) ?? string.Empty;
        var personalSignature = await _db.UserSignatures.Where(x => x.UserId == userId && x.IsActive).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync();
        if (personalSignature == null) { TempData["Error"] = "Upload your personal signature in My Profile before signing this document."; return RedirectToAction(nameof(Details), new { id }); }
        var existing = document.Signatures.FirstOrDefault(x => x.SlotNumber == slotNumber);
        if (existing != null && existing.AppliedByUserId != userId && !IsElevated())
        { TempData["Error"] = "This signature slot is already signed by another user."; return RedirectToAction(nameof(Details), new { id }); }
        if (existing == null)
        {
            existing = new GeneratedDocumentSignature { GeneratedCompanyDocumentId = id, SlotNumber = slotNumber };
            _db.GeneratedDocumentSignatures.Add(existing);
        }
        existing.UserSignatureId = personalSignature.Id; existing.AppliedByUserId = userId; existing.AppliedAt = DateTime.UtcNow;
        document.UpdatedByUserId = userId; document.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Sign", "CompanyDocument", id, $"Signature slot {slotNumber} ({labels[slotNumber - 1]}) signed by {User.Identity?.Name} using personal signature {personalSignature.Id}.");
        TempData["Success"] = $"Signed: {labels[slotNumber - 1]}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.Sign")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSignature(long id, int slotNumber)
    {
        var document = await _db.GeneratedCompanyDocuments.Include(x => x.Signatures).FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) return BadRequest();
        var signature = document.Signatures.FirstOrDefault(x => x.SlotNumber == slotNumber); if (signature == null) return RedirectToAction(nameof(Details), new { id });
        var userId = _userManager.GetUserId(User) ?? string.Empty;
        if (signature.AppliedByUserId != userId && !IsElevated()) return Forbid();
        _db.GeneratedDocumentSignatures.Remove(signature);
        document.UpdatedByUserId = userId; document.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "RemoveSignature", "CompanyDocument", id, $"Signature slot {slotNumber} removed from draft by {User.Identity?.Name}.");
        TempData["Success"] = "Draft signature removed. Audit history is preserved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.Sign")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyCompanyStamp(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.FirstOrDefaultAsync(x => x.Id == id); if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) { TempData["Error"] = "The company stamp can only be changed while the document is a draft."; return RedirectToAction(nameof(Details), new { id }); }
        var asset = await _db.OfficialDocumentAssets.AsNoTracking().FirstOrDefaultAsync(x => x.AssetType == "CompanyStamp" && x.IsActive);
        if (asset == null) { TempData["Error"] = "No active Company Stamp is configured in Settings."; return RedirectToAction(nameof(Details), new { id }); }
        var active = await _db.DocumentAssetUsages.FirstOrDefaultAsync(x => !x.IsRemoved && x.ReferenceType == "CompanyDocument" && x.ReferenceId == id.ToString() && x.OfficialDocumentAssetId == asset.Id);
        if (active == null)
        {
            _db.DocumentAssetUsages.Add(new DocumentAssetUsage { OfficialDocumentAssetId = asset.Id, ReferenceType = "CompanyDocument", ReferenceId = id.ToString(), ReferenceNumber = document.ReferenceNumber ?? document.Title, AppliedByUserId = _userManager.GetUserId(User) ?? string.Empty, AppliedAt = DateTime.UtcNow });
            document.UpdatedByUserId = _userManager.GetUserId(User) ?? string.Empty; document.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.WriteAsync(HttpContext, "ApplyCompanyStamp", "CompanyDocument", id, $"Company stamp {asset.Id} applied by {User.Identity?.Name}.");
        }
        TempData["Success"] = "Company stamp applied to the template-defined stamp position.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.Sign")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCompanyStamp(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.FirstOrDefaultAsync(x => x.Id == id); if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) return BadRequest();
        var usage = await _db.DocumentAssetUsages.Include(x => x.OfficialDocumentAsset).Where(x => !x.IsRemoved && x.ReferenceType == "CompanyDocument" && x.ReferenceId == id.ToString() && x.OfficialDocumentAsset != null && x.OfficialDocumentAsset.AssetType == "CompanyStamp").OrderByDescending(x => x.AppliedAt).FirstOrDefaultAsync();
        if (usage != null)
        {
            usage.IsRemoved = true; usage.RemovedByUserId = _userManager.GetUserId(User); usage.RemovedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.WriteAsync(HttpContext, "RemoveCompanyStamp", "CompanyDocument", id, $"Company stamp removed from draft by {User.Identity?.Name}.");
        }
        TempData["Success"] = "Company stamp removed from this draft. Audit history remains available.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.Finalize")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Finalize(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.Include(x => x.Signatures).FirstOrDefaultAsync(x => x.Id == id); if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Draft) { TempData["Error"] = "Only draft documents can be finalized."; return RedirectToAction(nameof(Details), new { id }); }

        var labels = ParseLabels(document.SignatureSlotLabelsSnapshot);
        var missingSlots = Enumerable.Range(1, labels.Length).Where(slot => document.Signatures.All(x => x.SlotNumber != slot)).ToList();
        if (missingSlots.Count > 0)
        { TempData["Error"] = $"Complete required signature slot(s) before finalizing: {string.Join(", ", missingSlots.Select(x => labels[x - 1]))}."; return RedirectToAction(nameof(Details), new { id }); }
        if (document.ServiceApprovalId.HasValue)
        {
            var status = await _db.ServiceApprovals.AsNoTracking().Where(x => x.Id == document.ServiceApprovalId.Value).Select(x => (ServiceApprovalStatus?)x.Status).FirstOrDefaultAsync();
            if (status != ServiceApprovalStatus.Approved)
            { TempData["Error"] = "A Service Approval based company document can only be finalized after the Service Approval is fully approved."; return RedirectToAction(nameof(Details), new { id }); }
        }
        if (document.TemplateHtmlSnapshot.Contains("{{CompanyStamp}}", StringComparison.OrdinalIgnoreCase))
        {
            var hasStamp = await _db.DocumentAssetUsages.AnyAsync(x => !x.IsRemoved && x.ReferenceType == "CompanyDocument" && x.ReferenceId == id.ToString() && x.OfficialDocumentAsset != null && x.OfficialDocumentAsset.AssetType == "CompanyStamp");
            if (!hasStamp) { TempData["Error"] = "Apply the company stamp before finalizing this template."; return RedirectToAction(nameof(Details), new { id }); }
        }

        document.FinalHtmlSnapshot = await _renderer.RenderGeneratedAsync(document, _userManager.GetUserId(User));
        document.Status = GeneratedDocumentStatus.Finalized;
        document.FinalizedByUserId = _userManager.GetUserId(User); document.FinalizedAt = DateTime.UtcNow;
        document.UpdatedByUserId = document.FinalizedByUserId ?? string.Empty; document.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Finalize", "CompanyDocument", id, "Final rendered snapshot locked. Further editing/signature changes are disabled.");
        TempData["Success"] = "Company document finalized. The final rendered record is now locked for audit consistency.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Documents.Finalize")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Void(long id, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) { TempData["Error"] = "A void reason is required."; return RedirectToAction(nameof(Details), new { id }); }
        var document = await _db.GeneratedCompanyDocuments.FirstOrDefaultAsync(x => x.Id == id); if (document == null) return NotFound();
        if (!await CanAccessAsync(document.DepartmentId)) return Forbid();
        if (document.Status != GeneratedDocumentStatus.Finalized) { TempData["Error"] = "Only a finalized document can be voided."; return RedirectToAction(nameof(Details), new { id }); }
        document.Status = GeneratedDocumentStatus.Voided; document.VoidedByUserId = _userManager.GetUserId(User); document.VoidedAt = DateTime.UtcNow; document.VoidReason = reason.Trim(); document.UpdatedAt = DateTime.UtcNow; document.UpdatedByUserId = document.VoidedByUserId ?? string.Empty;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Void", "CompanyDocument", id, reason.Trim());
        TempData["Success"] = "Final document marked as void. The original final snapshot remains preserved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<CompanyDocumentDetailsVm?> BuildDetailsVmAsync(long id)
    {
        var document = await _db.GeneratedCompanyDocuments.AsNoTracking().Include(x => x.DocumentTemplate).FirstOrDefaultAsync(x => x.Id == id);
        if (document == null) return null;
        var signatures = await _db.GeneratedDocumentSignatures.AsNoTracking().Include(x => x.UserSignature).Where(x => x.GeneratedCompanyDocumentId == id).OrderBy(x => x.SlotNumber).ToListAsync();
        var usages = await _db.DocumentAssetUsages.AsNoTracking().Include(x => x.OfficialDocumentAsset).Where(x => !x.IsRemoved && x.ReferenceType == "CompanyDocument" && x.ReferenceId == id.ToString()).OrderBy(x => x.AppliedAt).ToListAsync();
        var userIds = signatures.Select(x => x.AppliedByUserId).Concat(usages.Select(x => x.AppliedByUserId)).Append(document.CreatedByUserId).Append(document.UpdatedByUserId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(document.FinalizedByUserId)) userIds.Add(document.FinalizedByUserId);
        if (!string.IsNullOrWhiteSpace(document.VoidedByUserId)) userIds.Add(document.VoidedByUserId);
        var users = await _db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.FullName) ? (x.Email ?? x.UserName ?? x.Id) : x.FullName);
        var currentUserId = _userManager.GetUserId(User) ?? string.Empty;
        return new CompanyDocumentDetailsVm
        {
            Document = document,
            Template = document.DocumentTemplate,
            Signatures = signatures,
            AssetUsages = usages,
            UserNames = users,
            SignatureSlotLabels = ParseLabels(document.SignatureSlotLabelsSnapshot),
            CurrentUserHasSignature = await _db.UserSignatures.AsNoTracking().AnyAsync(x => x.UserId == currentUserId && x.IsActive),
            HasCompanyStamp = usages.Any(x => x.OfficialDocumentAsset?.AssetType == "CompanyStamp"),
            AuditHistory = await _db.AuditLogs.AsNoTracking().Where(x => x.EntityName == "CompanyDocument" && x.EntityId == id.ToString()).OrderByDescending(x => x.TimestampUtc).Take(100).ToListAsync()
        };
    }

    private async Task FillTemplateListAsync(CompanyDocumentCreateVm vm)
    {
        vm.Templates = await _db.DocumentTemplates.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DocumentType).ThenBy(x => x.Name)
            .Select(x => new SelectListItem($"{x.DocumentType} · {x.Name}", x.Id.ToString(), x.Id == vm.DocumentTemplateId)).ToListAsync();
    }

    private async Task<bool> CanEditAsync(GeneratedCompanyDocument document)
    {
        var userId = _userManager.GetUserId(User) ?? string.Empty;
        return document.CreatedByUserId == userId || IsElevated() || await _permissions.HasPermissionAsync(User, "DocumentTemplates.Manage");
    }

    private bool IsElevated() => User.IsInRole("Administrator") || User.IsInRole("Super Admin");

    private async Task<bool> CanAccessAsync(int? departmentId)
    {
        if (!departmentId.HasValue) return true;
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        return allowed == null || allowed.Contains(departmentId.Value);
    }

    private async Task<ReferenceContext?> ResolveContextAsync(long? serviceApprovalId, int? jobId, string? requisitionNumber, int? projectId)
    {
        if (serviceApprovalId.HasValue)
        {
            var sa = await _db.ServiceApprovals.AsNoTracking().Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == serviceApprovalId.Value);
            if (sa == null) return null;
            return new("ServiceApproval", sa.Id.ToString(), sa.ApprovalNumber ?? $"Pending SA · {sa.RequisitionNumber}", sa.RequisitionNumber, sa.ProjectId, sa.DepartmentId, $"Service Approval · {(sa.ApprovalNumber ?? sa.RequisitionNumber)}");
        }
        if (jobId.HasValue)
        {
            var job = await _db.Jobs.AsNoTracking().IgnoreQueryFilters().Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == jobId.Value && !x.IsDeleted);
            if (job == null) return null;
            return new("Job", job.Id.ToString(), job.JobNumber, null, job.ProjectId, job.DepartmentId, $"Job · {job.JobNumber}");
        }
        if (!string.IsNullOrWhiteSpace(requisitionNumber) && projectId.HasValue)
        {
            var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId.Value && x.IsActive && x.Name == "Requisition");
            if (sheet == null) return null;
            var reqCol = await _db.SheetColumns.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.Name == "Requisition No");
            if (reqCol == null || !await _db.SheetCells.AsNoTracking().AnyAsync(x => x.SheetColumnId == reqCol.Id && x.Value == requisitionNumber)) return null;
            int? departmentId = null;
            var departmentCol = await _db.SheetColumns.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.Name == "Request Department");
            if (departmentCol != null)
            {
                var rowId = await _db.SheetCells.AsNoTracking().Where(x => x.SheetColumnId == reqCol.Id && x.Value == requisitionNumber).Select(x => x.SheetRowId).FirstOrDefaultAsync();
                var departmentCode = rowId == 0 ? null : await _db.SheetCells.AsNoTracking().Where(x => x.SheetRowId == rowId && x.SheetColumnId == departmentCol.Id).Select(x => x.Value).FirstOrDefaultAsync();
                if (!string.IsNullOrWhiteSpace(departmentCode)) departmentId = await _db.Departments.AsNoTracking().Where(x => x.Code == departmentCode).Select(x => (int?)x.Id).FirstOrDefaultAsync();
            }
            return new("Requisition", requisitionNumber.Trim(), requisitionNumber.Trim(), requisitionNumber.Trim(), projectId, departmentId, $"Requisition · {requisitionNumber.Trim()}");
        }
        return null;
    }

    private static string[] ParseLabels(string? labels) => string.IsNullOrWhiteSpace(labels)
        ? Array.Empty<string>()
        : labels.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToArray();

    private sealed record ReferenceContext(string ReferenceType, string ReferenceId, string? ReferenceNumber, string? RequisitionNumber, int? ProjectId, int? DepartmentId, string Label);
}
