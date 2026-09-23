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
[RequirePermission("DocumentTemplates.Manage")]
public class DocumentTemplatesController : Controller
{
    private static readonly HashSet<string> AllowedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".jpg", ".jpeg", ".png" };
    private const long MaxSourceBytes = 25L * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _environment;
    private readonly IDocumentRenderingService _renderer;

    public DocumentTemplatesController(
        ApplicationDbContext db,
        IAuditService audit,
        UserManager<ApplicationUser> userManager,
        IWebHostEnvironment environment,
        IDocumentRenderingService renderer)
    {
        _db = db;
        _audit = audit;
        _userManager = userManager;
        _environment = environment;
        _renderer = renderer;
    }

    public async Task<IActionResult> Index() => View(await _db.DocumentTemplates.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DocumentType).ThenBy(x => x.Name).ToListAsync());

    [HttpGet]
    public IActionResult Create(string? type = null)
    {
        var resolvedType = string.IsNullOrWhiteSpace(type) ? "Custom" : type;
        var defaults = TemplateDefaults(resolvedType);
        return View("Form", new DocumentTemplateFormVm
        {
            DocumentType = resolvedType,
            HtmlContent = defaults.Html,
            EditableFieldLabels = defaults.EditableFields,
            SignatureSlotLabels = defaults.SignatureSlots,
            Description = defaults.Description
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxSourceBytes + 1024 * 1024)]
    public async Task<IActionResult> Create(DocumentTemplateFormVm vm, IFormFile? sourceFile)
    {
        ValidateSourceFile(sourceFile);
        if (!ModelState.IsValid) return View("Form", vm);
        var template = new DocumentTemplate
        {
            Name = vm.Name.Trim(),
            DocumentType = vm.DocumentType.Trim(),
            Description = vm.Description?.Trim(),
            HtmlContent = vm.HtmlContent,
            EditableFieldLabels = NormalizeLabels(vm.EditableFieldLabels),
            SignatureSlotLabels = NormalizeLabels(vm.SignatureSlotLabels),
            IsActive = true,
            CreatedByUserId = _userManager.GetUserId(User) ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        if (sourceFile != null) await SaveSourceFileAsync(template, sourceFile);
        _db.DocumentTemplates.Add(template);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Create", "DocumentTemplate", template.Id, $"{template.Name}; type {template.DocumentType}; source {template.SourceOriginalFileName}");
        TempData["Success"] = "Company document template created.";
        return RedirectToAction(nameof(Edit), new { id = template.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var x = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.IsActive);
        if (x == null) return NotFound();
        return View("Form", new DocumentTemplateFormVm
        {
            Id = x.Id,
            Name = x.Name,
            DocumentType = x.DocumentType,
            Description = x.Description,
            HtmlContent = x.HtmlContent,
            EditableFieldLabels = x.EditableFieldLabels,
            SignatureSlotLabels = x.SignatureSlotLabels,
            ExistingSourceFileName = x.SourceOriginalFileName
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxSourceBytes + 1024 * 1024)]
    public async Task<IActionResult> Edit(int id, DocumentTemplateFormVm vm, IFormFile? sourceFile)
    {
        if (id != vm.Id) return BadRequest();
        ValidateSourceFile(sourceFile);
        if (!ModelState.IsValid) return View("Form", vm);
        var x = await _db.DocumentTemplates.FirstOrDefaultAsync(t => t.Id == id && t.IsActive);
        if (x == null) return NotFound();
        x.Name = vm.Name.Trim();
        x.DocumentType = vm.DocumentType.Trim();
        x.Description = vm.Description?.Trim();
        x.HtmlContent = vm.HtmlContent;
        x.EditableFieldLabels = NormalizeLabels(vm.EditableFieldLabels);
        x.SignatureSlotLabels = NormalizeLabels(vm.SignatureSlotLabels);
        x.UpdatedAt = DateTime.UtcNow;
        if (sourceFile != null) await SaveSourceFileAsync(x, sourceFile);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Edit", "DocumentTemplate", x.Id, $"{x.Name}; source {x.SourceOriginalFileName}");
        TempData["Success"] = "Company document template saved.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var x = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.IsActive);
        if (x == null) return NotFound();
        var copy = new DocumentTemplate
        {
            Name = $"{x.Name} - Copy",
            DocumentType = x.DocumentType,
            Description = x.Description,
            HtmlContent = x.HtmlContent,
            EditableFieldLabels = x.EditableFieldLabels,
            SignatureSlotLabels = x.SignatureSlotLabels,
            IsActive = true,
            CreatedByUserId = _userManager.GetUserId(User) ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.DocumentTemplates.Add(copy);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Duplicate", "DocumentTemplate", copy.Id, copy.Name);
        TempData["Success"] = "Template duplicated without copying the original source attachment. Upload a source file if required.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var x = await _db.DocumentTemplates.FirstOrDefaultAsync(t => t.Id == id && t.IsActive);
        if (x == null) return NotFound();
        x.IsActive = false; x.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Delete", "DocumentTemplate", x.Id, x.Name);
        TempData["Success"] = "Template removed from the active library. Existing generated documents keep their template snapshot.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Preview(int id, long? serviceApprovalId = null, int? jobId = null, string? requisitionNumber = null, int? projectId = null)
    {
        var x = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.IsActive);
        if (x == null) return NotFound();
        ViewBag.Template = x;
        ViewBag.RenderedHtml = await _renderer.RenderAsync(x.HtmlContent, new DocumentRenderContext(null, serviceApprovalId, jobId, requisitionNumber, projectId, _userManager.GetUserId(User)));
        return View();
    }

    public async Task<IActionResult> Print(int id, long? serviceApprovalId = null, int? jobId = null, string? requisitionNumber = null, int? projectId = null)
    {
        var x = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.IsActive);
        if (x == null) return NotFound();
        ViewBag.Template = x;
        ViewBag.RenderedHtml = await _renderer.RenderAsync(x.HtmlContent, new DocumentRenderContext(null, serviceApprovalId, jobId, requisitionNumber, projectId, _userManager.GetUserId(User)));
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Source(int id, bool download = false)
    {
        var template = await _db.DocumentTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (template == null || string.IsNullOrWhiteSpace(template.SourceStoragePath) || string.IsNullOrWhiteSpace(template.SourceOriginalFileName)) return NotFound();
        var relative = template.SourceStoragePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, relative));
        if (!System.IO.File.Exists(path)) return NotFound();
        var contentType = template.SourceContentType ?? "application/octet-stream";
        return download ? PhysicalFile(path, contentType, template.SourceOriginalFileName) : PhysicalFile(path, contentType);
    }

    private void ValidateSourceFile(IFormFile? file)
    {
        if (file == null) return;
        var ext = Path.GetExtension(file.FileName);
        if (file.Length == 0 || file.Length > MaxSourceBytes || !AllowedSourceExtensions.Contains(ext))
            ModelState.AddModelError(string.Empty, "Source company document must be PDF, DOC, DOCX, XLS, XLSX, JPG, JPEG or PNG and no larger than 25 MB.");
    }

    private async Task SaveSourceFileAsync(DocumentTemplate template, IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "DocumentTemplates");
        Directory.CreateDirectory(folder);
        var stored = $"{Guid.NewGuid():N}{ext}";
        await using (var stream = System.IO.File.Create(Path.Combine(folder, stored))) await file.CopyToAsync(stream);
        template.SourceOriginalFileName = Path.GetFileName(file.FileName);
        template.SourceStoredFileName = stored;
        template.SourceContentType = SourceContentType(ext);
        template.SourceStoragePath = Path.Combine("App_Data", "DocumentTemplates", stored).Replace('\\', '/');
        template.SourceSizeBytes = file.Length;
        template.SourceUploadedAt = DateTime.UtcNow;
    }

    private static string? NormalizeLabels(string? labels)
    {
        if (string.IsNullOrWhiteSpace(labels)) return null;
        return string.Join('\n', labels.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4));
    }

    private static string SourceContentType(string ext) => ext switch
    {
        ".pdf" => "application/pdf", ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", _ => "application/octet-stream"
    };

    private static (string Html, string? EditableFields, string? SignatureSlots, string Description) TemplateDefaults(string? type)
    {
        if (string.Equals(type, "Service Approval", StringComparison.OrdinalIgnoreCase))
        {
            const string html = "<div class=\"doc-header\">{{Logo}}<h1>SERVICE BUSINESS APPROVAL SHEET</h1><p><strong>Ref. No.</strong> {{ServiceApprovalNumber}}</p></div><table class=\"doc-table\"><tr><th>Type</th><td>{{ServiceType}}</td><th>Vendor Selection</th><td>{{VendorSelectionMethod}}</td></tr><tr><th>BGP Entity / Project</th><td colspan=\"3\">{{Project}}</td></tr><tr><th>Requisition No.</th><td>{{RequisitionNumber}}</td><th>Date</th><td>{{Date}}</td></tr><tr><th>Approval Content</th><td colspan=\"3\">{{ServiceDescription}}</td></tr><tr><th>Approved Amount</th><td colspan=\"3\">{{ServiceAmount}}</td></tr><tr><th>Additional Notes</th><td colspan=\"3\">{{CustomText1}}</td></tr></table><div class=\"approval-sign-grid\"><div><strong>{{Stage1Title}}</strong><p>{{Stage1Name}}</p>{{Stage1Signature}}<small>{{Stage1Date}}</small><small>{{Stage1Comment}}</small></div><div><strong>{{Stage2Title}}</strong><p>{{Stage2Name}}</p>{{Stage2Signature}}<small>{{Stage2Date}}</small><small>{{Stage2Comment}}</small></div><div><strong>{{Stage3Title}}</strong><p>{{Stage3Name}}</p>{{Stage3Signature}}<small>{{Stage3Date}}</small><small>{{Stage3Comment}}</small></div><div><strong>{{Stage4Title}}</strong><p>{{Stage4Name}}</p>{{Stage4Signature}}<small>{{Stage4Date}}</small><small>{{Stage4Comment}}</small></div></div><div class=\"stamp-area\"><strong>Company Stamp</strong>{{CompanyStamp}}</div>";
            return (html, "Additional notes / controlled wording", null, "Service approval form with linked approval data, stage-specific personal signatures and controlled company stamp placement.");
        }
        if (string.Equals(type, "Purchase Order", StringComparison.OrdinalIgnoreCase))
        {
            const string html = "<div class=\"doc-header\">{{Logo}}<h1>PURCHASE ORDER</h1><p>{{CompanyName}}</p></div><table class=\"doc-table compact\"><tr><th>BGP Ref.</th><td>{{JobNumber}}</td><th>Date</th><td>{{Date}}</td></tr><tr><th>Requisition No.</th><td>{{RequisitionNumber}}</td><th>Quotation No.</th><td>{{CustomText1}}</td></tr></table><div class=\"document-two-col\"><div><h3>Contractor Details</h3><p><strong>{{ContractorName}}</strong></p><p>{{CustomText2}}</p></div><div><h3>Company Details</h3><p><strong>{{CompanyName}}</strong></p><p>{{Project}}</p></div></div>{{JobItemsTable}}<div class=\"terms-box\"><strong>Service / Delivery Terms</strong><p>{{CustomText3}}</p><p><strong>Remarks:</strong> {{CustomText4}}</p></div><div class=\"signature-grid\"><div><strong>Company Authorized Signatory</strong><p>{{Signer1Name}}</p>{{Signer1Signature}}<small>{{Signer1Date}}</small></div><div><strong>Company Stamp</strong>{{CompanyStamp}}</div></div>";
            return (html, "Quotation No.\nContractor address / contact details\nService / delivery terms\nRemarks", "Company Authorized Signatory", "Purchase Order layout with linked job data, editable commercial text and controlled signing/stamp positions.");
        }
        const string custom = "<div class=\"doc-header\">{{Logo}}<h1>DOCUMENT TITLE</h1><p>{{CompanyName}}</p></div><table class=\"doc-table\"><tr><th>Reference</th><td>{{RequisitionNumber}}</td><th>Date</th><td>{{Date}}</td></tr></table><h3>{{CustomText1}}</h3><p>{{CustomText2}}</p><div class=\"signature-grid\"><div><strong>{{CustomText3}}</strong><p>{{Signer1Name}}</p>{{Signer1Signature}}<small>{{Signer1Date}}</small></div><div><strong>Company Stamp</strong>{{CompanyStamp}}</div></div>";
        return (custom, "Heading\nBody text\nSignature section label\nAdditional notes", "Authorized Signatory", "Reusable custom company document with safe editable text and controlled signature/stamp slots.");
    }
}
