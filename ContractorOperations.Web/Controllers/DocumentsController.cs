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
public class DocumentsController : Controller
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".jpg", ".jpeg", ".png" };
    private const long MaxFileBytes = 25L * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _environment;

    public DocumentsController(ApplicationDbContext db, IPermissionService permissions, IAuditService audit,
        UserManager<ApplicationUser> userManager, IWebHostEnvironment environment)
    {
        _db = db; _permissions = permissions; _audit = audit; _userManager = userManager; _environment = environment;
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Index(string? q, string? referenceType, string? requisitionNumber, string? jobNumber, string? serviceApprovalNumber)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var query = _db.DocumentRecords.AsNoTracking().Where(x => !x.IsDeleted);
        if (allowed != null) query = query.Where(x => x.DepartmentId == null || allowed.Contains(x.DepartmentId.Value));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(x => x.OriginalFileName.Contains(search) || (x.ReferenceNumber != null && x.ReferenceNumber.Contains(search)) || (x.Notes != null && x.Notes.Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(referenceType)) query = query.Where(x => x.ReferenceType == referenceType);
        if (!string.IsNullOrWhiteSpace(requisitionNumber)) query = query.Where(x => x.RequisitionNumber == requisitionNumber);
        if (!string.IsNullOrWhiteSpace(jobNumber)) query = query.Where(x => x.JobNumber == jobNumber);
        if (!string.IsNullOrWhiteSpace(serviceApprovalNumber)) query = query.Where(x => x.ServiceApprovalNumber == serviceApprovalNumber);

        var vm = new DocumentLibraryVm
        {
            Documents = await query.OrderByDescending(x => x.UploadedAt).Take(1000).ToListAsync(),
            Query = q, ReferenceType = referenceType, RequisitionNumber = requisitionNumber,
            JobNumber = jobNumber, ServiceApprovalNumber = serviceApprovalNumber
        };
        var uploaderIds = vm.Documents.Select(x => x.UploadedByUserId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        ViewBag.UploaderNames = await _db.Users.AsNoTracking().Where(x => uploaderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.FullName) ? (x.Email ?? x.UserName ?? x.Id) : x.FullName);
        return View(vm);
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Record(string referenceType, string referenceId, int? projectId = null)
    {
        var context = await ResolveReferenceAsync(referenceType, referenceId, projectId);
        if (context == null) return NotFound();
        if (!await CanAccessAsync(context.DepartmentId)) return Forbid();

        ViewBag.ReferenceType = context.ReferenceType;
        ViewBag.ReferenceId = context.ReferenceId;
        ViewBag.ReferenceNumber = context.ReferenceNumber;
        ViewBag.ProjectId = context.ProjectId;
        ViewBag.DepartmentId = context.DepartmentId;
        ViewBag.RequisitionNumber = context.RequisitionNumber;
        ViewBag.JobNumber = context.JobNumber;
        ViewBag.ServiceApprovalNumber = context.ServiceApprovalNumber;
        ViewBag.CanUpload = await _permissions.HasPermissionAsync(User, "Documents.Upload");
        ViewBag.CanDelete = await _permissions.HasPermissionAsync(User, "Documents.Delete");
        ViewBag.CanPrint = await _permissions.HasPermissionAsync(User, "Documents.Print");

        var docs = await _db.DocumentRecords.AsNoTracking()
            .Where(x => !x.IsDeleted && x.ReferenceType == context.ReferenceType && x.ReferenceId == context.ReferenceId)
            .OrderByDescending(x => x.UploadedAt).ToListAsync();
        var uploaderIds = docs.Select(x => x.UploadedByUserId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        ViewBag.UploaderNames = await _db.Users.AsNoTracking().Where(x => uploaderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.FullName) ? (x.Email ?? x.UserName ?? x.Id) : x.FullName);
        return View(docs);
    }

    [RequirePermission("Documents.Upload")]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(string referenceType, string referenceId, int? projectId, IFormFile? file, string? notes, long? replaceDocumentId)
    {
        var context = await ResolveReferenceAsync(referenceType, referenceId, projectId);
        if (context == null) return NotFound();
        if (!await CanAccessAsync(context.DepartmentId)) return Forbid();
        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Select a document to upload.";
            return RedirectToAction(nameof(Record), new { referenceType, referenceId, projectId });
        }
        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext) || file.Length > MaxFileBytes)
        {
            TempData["Error"] = "Allowed formats: PDF, DOC, DOCX, XLS, XLSX, JPG, JPEG and PNG. Maximum size is 25 MB.";
            return RedirectToAction(nameof(Record), new { referenceType, referenceId, projectId });
        }

        DocumentRecord? replacedDocument = null;
        if (replaceDocumentId.HasValue)
        {
            replacedDocument = await _db.DocumentRecords.FirstOrDefaultAsync(x => x.Id == replaceDocumentId.Value && !x.IsDeleted);
            if (replacedDocument == null || replacedDocument.ReferenceType != context.ReferenceType || replacedDocument.ReferenceId != context.ReferenceId)
            {
                TempData["Error"] = "The selected document cannot be replaced from this record.";
                return RedirectToAction(nameof(Record), new { referenceType, referenceId, projectId });
            }
            if (!await CanAccessAsync(replacedDocument.DepartmentId)) return Forbid();
        }

        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "Documents");
        Directory.CreateDirectory(folder);
        var storedName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var physical = Path.Combine(folder, storedName);
        await using (var stream = System.IO.File.Create(physical)) await file.CopyToAsync(stream);

        var record = new DocumentRecord
        {
            OriginalFileName = Path.GetFileName(file.FileName), StoredFileName = storedName,
            ContentType = GetContentType(ext),
            SizeBytes = file.Length, StoragePath = Path.Combine("App_Data", "Documents", storedName).Replace('\\', '/'),
            ReferenceType = context.ReferenceType, ReferenceId = context.ReferenceId, ReferenceNumber = context.ReferenceNumber,
            RequisitionNumber = context.RequisitionNumber, JobNumber = context.JobNumber, ServiceApprovalNumber = context.ServiceApprovalNumber,
            ProjectId = context.ProjectId, DepartmentId = context.DepartmentId, Notes = notes?.Trim(),
            UploadedByUserId = _userManager.GetUserId(User) ?? string.Empty, UploadedAt = DateTime.UtcNow
        };
        _db.DocumentRecords.Add(record);
        if (replacedDocument != null)
        {
            replacedDocument.IsDeleted = true;
            replacedDocument.DeletedAt = DateTime.UtcNow;
            replacedDocument.DeletedByUserId = _userManager.GetUserId(User);
        }
        await _db.SaveChangesAsync();
        if (replacedDocument != null)
            await _audit.WriteAsync(HttpContext, "DocumentReplaced", "Document", record.Id, $"Replaced document {replacedDocument.Id} ({replacedDocument.OriginalFileName}) with {record.OriginalFileName}; {record.ReferenceType} {record.ReferenceNumber}");
        else
            await _audit.WriteAsync(HttpContext, "DocumentUploaded", "Document", record.Id, $"{record.OriginalFileName}; {record.ReferenceType} {record.ReferenceNumber}");
        TempData["Success"] = replacedDocument == null ? "Document uploaded successfully." : "Document replaced successfully. The previous revision remains in audit history.";
        return RedirectToAction(nameof(Record), new { referenceType, referenceId, projectId });
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Preview(long id)
    {
        var doc = await GetAccessibleDocumentAsync(id); if (doc == null) return NotFound();
        var path = PhysicalPath(doc); if (!System.IO.File.Exists(path)) return NotFound();
        var ext = Path.GetExtension(doc.OriginalFileName);
        if (!new[] { ".pdf", ".jpg", ".jpeg", ".png" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Download), new { id });
        await _audit.WriteAsync(HttpContext, "DocumentPreviewed", "Document", doc.Id, doc.OriginalFileName);
        return PhysicalFile(path, doc.ContentType);
    }

    [RequirePermission("Documents.View")]
    public async Task<IActionResult> Download(long id)
    {
        var doc = await GetAccessibleDocumentAsync(id); if (doc == null) return NotFound();
        var path = PhysicalPath(doc); if (!System.IO.File.Exists(path)) return NotFound();
        await _audit.WriteAsync(HttpContext, "DocumentDownloaded", "Document", doc.Id, doc.OriginalFileName);
        return PhysicalFile(path, doc.ContentType, doc.OriginalFileName);
    }

    [RequirePermission("Documents.Print")]
    public async Task<IActionResult> Print(long id)
    {
        var doc = await GetAccessibleDocumentAsync(id); if (doc == null) return NotFound();
        ViewBag.CanInline = new[] { ".pdf", ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(doc.OriginalFileName), StringComparer.OrdinalIgnoreCase);
        return View(doc);
    }

    [RequirePermission("Documents.Delete")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id)
    {
        var doc = await _db.DocumentRecords.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (doc == null) return NotFound();
        if (!await CanAccessAsync(doc.DepartmentId)) return Forbid();
        doc.IsDeleted = true; doc.DeletedAt = DateTime.UtcNow; doc.DeletedByUserId = _userManager.GetUserId(User);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "DocumentDeleted", "Document", doc.Id, $"{doc.OriginalFileName}; {doc.ReferenceType} {doc.ReferenceNumber}");
        TempData["Success"] = "Document removed from the active library. Audit history has been preserved.";
        return RedirectToAction(nameof(Record), new { referenceType = doc.ReferenceType, referenceId = doc.ReferenceId, projectId = doc.ProjectId });
    }

    private async Task<DocumentRecord?> GetAccessibleDocumentAsync(long id)
    {
        var doc = await _db.DocumentRecords.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (doc == null || !await CanAccessAsync(doc.DepartmentId)) return null;
        return doc;
    }

    private string PhysicalPath(DocumentRecord doc)
    {
        var relative = doc.StoragePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relative));
    }

    private async Task<bool> CanAccessAsync(int? departmentId)
    {
        if (!departmentId.HasValue) return true;
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        return allowed == null || allowed.Contains(departmentId.Value);
    }

    private async Task<ReferenceContext?> ResolveReferenceAsync(string referenceType, string referenceId, int? projectId)
    {
        referenceType = (referenceType ?? string.Empty).Trim(); referenceId = (referenceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId)) return null;
        if (referenceType.Equals("Job", StringComparison.OrdinalIgnoreCase) && int.TryParse(referenceId, out var jobId))
        {
            var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId); if (job == null) return null;
            return new("Job", job.Id.ToString(), job.JobNumber, job.ProjectId, job.DepartmentId, null, job.JobNumber, null);
        }
        if (referenceType.Equals("ServiceApproval", StringComparison.OrdinalIgnoreCase) && long.TryParse(referenceId, out var approvalId))
        {
            var a = await _db.ServiceApprovals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == approvalId); if (a == null) return null;
            return new("ServiceApproval", a.Id.ToString(), a.ApprovalNumber ?? $"Pending SA #{a.Id}", a.ProjectId, a.DepartmentId, a.RequisitionNumber, null, a.ApprovalNumber);
        }
        if (referenceType.Equals("Sheet", StringComparison.OrdinalIgnoreCase) && int.TryParse(referenceId, out var sheetId))
        {
            var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sheetId); if (sheet == null) return null;
            return new("Sheet", sheet.Id.ToString(), sheet.UniqueId, sheet.ProjectId, sheet.DepartmentId, null, null, null);
        }
        if (referenceType.Equals("SheetRow", StringComparison.OrdinalIgnoreCase) && long.TryParse(referenceId, out var rowId))
        {
            var row = await _db.SheetRows.AsNoTracking().Include(x => x.ProjectSheet).FirstOrDefaultAsync(x => x.Id == rowId); if (row?.ProjectSheet == null) return null;
            return new("SheetRow", row.Id.ToString(), row.UniqueId, row.ProjectSheet.ProjectId, row.ProjectSheet.DepartmentId, null, null, null);
        }
        if (referenceType.Equals("Requisition", StringComparison.OrdinalIgnoreCase) && projectId.HasValue)
        {
            var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition"); if (sheet == null) return null;
            var reqCol = await _db.SheetColumns.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.Name == "Requisition No"); if (reqCol == null) return null;
            var exists = await _db.SheetCells.AsNoTracking().AnyAsync(x => x.SheetColumnId == reqCol.Id && x.Value == referenceId); if (!exists) return null;
            var deptCode = await GetRequisitionDepartmentAsync(sheet.Id, reqCol.Id, referenceId);
            var deptId = string.IsNullOrWhiteSpace(deptCode) ? sheet.DepartmentId : await _db.Departments.AsNoTracking().Where(x => x.Code == deptCode).Select(x => (int?)x.Id).FirstOrDefaultAsync();
            return new("Requisition", referenceId, referenceId, projectId, deptId, referenceId, null, null);
        }
        if (referenceType.Equals("InventoryRequest", StringComparison.OrdinalIgnoreCase) && long.TryParse(referenceId, out var requestId))
        {
            var r = await _db.InventoryRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == requestId); if (r == null) return null;
            return new("InventoryRequest", r.Id.ToString(), r.RequestNumber, r.ProjectId, r.DepartmentId, null, null, null);
        }
        if (referenceType.Equals("StockItem", StringComparison.OrdinalIgnoreCase) && int.TryParse(referenceId, out var stockId))
        {
            var item = await _db.StockItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == stockId); if (item == null) return null;
            return new("StockItem", item.Id.ToString(), item.ItemCode, null, null, null, null, null);
        }
        if (referenceType.Equals("Contractor", StringComparison.OrdinalIgnoreCase) && int.TryParse(referenceId, out var contractorId))
        {
            var c = await _db.Contractors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractorId); if (c == null) return null;
            return new("Contractor", c.Id.ToString(), c.Name, null, null, null, null, null);
        }
        return null;
    }

    private async Task<string?> GetRequisitionDepartmentAsync(int sheetId, int reqColumnId, string reqNo)
    {
        var departmentColumn = await _db.SheetColumns.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectSheetId == sheetId && x.Name == "Request Department");
        if (departmentColumn == null) return null;
        var rowId = await _db.SheetCells.AsNoTracking().Where(x => x.SheetColumnId == reqColumnId && x.Value == reqNo).Select(x => x.SheetRowId).FirstOrDefaultAsync();
        return rowId == 0 ? null : await _db.SheetCells.AsNoTracking().Where(x => x.SheetRowId == rowId && x.SheetColumnId == departmentColumn.Id).Select(x => x.Value).FirstOrDefaultAsync();
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", _ => "application/octet-stream"
    };

    private sealed record ReferenceContext(string ReferenceType, string ReferenceId, string? ReferenceNumber, int? ProjectId, int? DepartmentId,
        string? RequisitionNumber, string? JobNumber, string? ServiceApprovalNumber);
}
