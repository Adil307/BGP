using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;

namespace ContractorOperations.Web.Controllers;

[Authorize]
public class JobsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IJobNumberService _numbers;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;
    public JobsController(ApplicationDbContext db, IPermissionService permissions, IJobNumberService numbers, IAuditService audit, UserManager<ApplicationUser> userManager)
    { _db = db; _permissions = permissions; _numbers = numbers; _audit = audit; _userManager = userManager; }

    [RequirePermission("Jobs.View")]
    public async Task<IActionResult> Index(string? q, int? projectId, int? departmentId, int? contractorId, JobStatus? status, DateTime? from, DateTime? to)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var query = _db.Jobs.AsNoTracking().Include(x => x.Project).Include(x => x.Department).Include(x => x.Contractor)
            .Include(x => x.Lines).ThenInclude(x => x.Currency).AsQueryable();
        if (allowed != null) query = query.Where(x => allowed.Contains(x.DepartmentId));
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.JobNumber.Contains(q) || x.Description.Contains(q) || x.Contractor!.Name.Contains(q));
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId);
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId);
        if (contractorId.HasValue) query = query.Where(x => x.ContractorId == contractorId);
        if (status.HasValue) query = query.Where(x => x.Status == status);
        if (from.HasValue) query = query.Where(x => x.JobDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(x => x.JobDate < to.Value.Date.AddDays(1));

        ViewBag.Projects = new SelectList(await _db.Projects.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(), "Id", "Name", projectId);
        ViewBag.Departments = new SelectList(await _db.Departments.Where(x => x.IsActive && (allowed == null || allowed.Contains(x.Id))).OrderBy(x => x.Name).ToListAsync(), "Id", "Name", departmentId);
        ViewBag.Contractors = new SelectList(await _db.Contractors.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(), "Id", "Name", contractorId);
        ViewBag.Query = q; ViewBag.Status = status; ViewBag.From = from; ViewBag.To = to;
        return View(await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync());
    }

    [RequirePermission("Jobs.View")]
    public async Task<IActionResult> Details(int id)
    {
        var job = await _db.Jobs.Include(x => x.Project).Include(x => x.Department).Include(x => x.Contractor).Include(x => x.CreatedByUser)
            .Include(x => x.Lines).ThenInclude(x => x.Unit).Include(x => x.Lines).ThenInclude(x => x.Currency).FirstOrDefaultAsync(x => x.Id == id);
        if (job == null) return NotFound();
        if (!await CanAccessDepartment(job.DepartmentId)) return Forbid();
        return View(job);
    }

    [RequirePermission("Jobs.Create")]
    [HttpGet]
    public async Task<IActionResult> Create(int? projectId)
    {
        var vm = new JobFormVm { JobNumber = "Auto-generated on save", ProjectId = projectId ?? 0 };
        await FillListsAsync(vm);
        return View(vm);
    }

    [RequirePermission("Jobs.Create")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(JobFormVm vm)
    {
        NormalizeLines(vm);
        if (!await CanAccessDepartment(vm.DepartmentId)) ModelState.AddModelError(nameof(vm.DepartmentId), "You do not have access to this department.");
        await ValidateBusinessRulesAsync(vm, null);
        if (!ModelState.IsValid) { vm.JobNumber = "Auto-generated on save"; await FillListsAsync(vm); return View(vm); }

        var userId = _userManager.GetUserId(User)!;
        var job = new Job
        {
            JobNumber = await _numbers.NextAsync(vm.DepartmentId), DeduplicationKey = BuildDeduplicationKey(vm), ProjectId = vm.ProjectId, DepartmentId = vm.DepartmentId,
            ContractorId = vm.ContractorId, Description = vm.Description.Trim(), JobDate = vm.JobDate,
            ServiceStart = vm.ServiceStart, ServiceEnd = vm.ServiceEnd, Status = vm.Status,
            InvoiceNumber = vm.InvoiceNumber?.Trim(), InvoiceDate = vm.InvoiceDate, PaymentStatus = vm.PaymentStatus,
            ProcessorName = vm.ProcessorName?.Trim(), CreatedByUserId = userId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = vm.Lines.Select(x => new JobLine { Description = x.Description.Trim(), UnitId = x.UnitId, Quantity = x.Quantity, UnitRate = x.UnitRate, CurrencyId = x.CurrencyId }).ToList()
        };
        _db.Jobs.Add(job);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // This also protects against a double-click / concurrent submit race.
            // Business-rule validation normally catches duplicates before save, but
            // the database remains the final source of truth.
            ModelState.AddModelError(string.Empty, "This job could not be saved because an active duplicate or conflicting job number already exists. Please review the existing jobs and try again.");
            vm.JobNumber = "Auto-generated on save";
            await FillListsAsync(vm);
            return View(vm);
        }
        await _audit.WriteAsync(HttpContext, "Create", "Job", job.Id, job.JobNumber);
        TempData["Success"] = $"Job {job.JobNumber} created successfully.";
        return RedirectToAction(nameof(Details), new { id = job.Id });
    }

    [RequirePermission("Jobs.Edit")]
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var job = await _db.Jobs.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (job == null) return NotFound();
        if (!await CanAccessDepartment(job.DepartmentId)) return Forbid();
        var vm = new JobFormVm
        {
            Id = job.Id, JobNumber = job.JobNumber, ProjectId = job.ProjectId, DepartmentId = job.DepartmentId, ContractorId = job.ContractorId,
            Description = job.Description, JobDate = job.JobDate, ServiceStart = job.ServiceStart, ServiceEnd = job.ServiceEnd, Status = job.Status,
            InvoiceNumber = job.InvoiceNumber, InvoiceDate = job.InvoiceDate, PaymentStatus = job.PaymentStatus, ProcessorName = job.ProcessorName,
            Lines = job.Lines.Select(x => new JobLineInputVm { Id = x.Id, Description = x.Description, UnitId = x.UnitId, Quantity = x.Quantity, UnitRate = x.UnitRate, CurrencyId = x.CurrencyId }).ToList()
        };
        await FillListsAsync(vm);
        return View(vm);
    }

    [RequirePermission("Jobs.Edit")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, JobFormVm vm)
    {
        if (id != vm.Id) return BadRequest();
        var job = await _db.Jobs.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (job == null) return NotFound();
        if (!await CanAccessDepartment(job.DepartmentId) || !await CanAccessDepartment(vm.DepartmentId)) return Forbid();
        NormalizeLines(vm);
        await ValidateBusinessRulesAsync(vm, id);
        if (!ModelState.IsValid) { vm.JobNumber = job.JobNumber; await FillListsAsync(vm); return View(vm); }

        job.ProjectId = vm.ProjectId; job.DepartmentId = vm.DepartmentId; job.ContractorId = vm.ContractorId; job.Description = vm.Description.Trim(); job.DeduplicationKey = BuildDeduplicationKey(vm);
        job.JobDate = vm.JobDate; job.ServiceStart = vm.ServiceStart; job.ServiceEnd = vm.ServiceEnd; job.Status = vm.Status;
        job.InvoiceNumber = vm.InvoiceNumber?.Trim(); job.InvoiceDate = vm.InvoiceDate; job.PaymentStatus = vm.PaymentStatus; job.ProcessorName = vm.ProcessorName?.Trim();
        job.UpdatedAt = DateTime.UtcNow;
        _db.JobLines.RemoveRange(job.Lines);
        job.Lines = vm.Lines.Select(x => new JobLine { Description = x.Description.Trim(), UnitId = x.UnitId, Quantity = x.Quantity, UnitRate = x.UnitRate, CurrencyId = x.CurrencyId }).ToList();
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Edit", "Job", job.Id, job.JobNumber);
        TempData["Success"] = $"Job {job.JobNumber} updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Jobs.Delete")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
        if (job == null) return NotFound();
        if (!await CanAccessDepartment(job.DepartmentId)) return Forbid();
        var number = job.JobNumber;
        job.IsDeleted = true;
        job.DeletedAt = DateTime.UtcNow;
        job.DeletedByUserId = _userManager.GetUserId(User);
        job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "SoftDelete", "Job", id, number);
        TempData["Success"] = $"Job {number} moved to recycle bin.";
        return RedirectToAction(nameof(Index));
    }


    [RequirePermission("Jobs.Delete")]
    public async Task<IActionResult> RecycleBin()
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var query = _db.Jobs.IgnoreQueryFilters().AsNoTracking().Where(x => x.IsDeleted)
            .Include(x => x.Project).Include(x => x.Department).Include(x => x.Contractor).AsQueryable();
        if (allowed != null) query = query.Where(x => allowed.Contains(x.DepartmentId));
        return View(await query.OrderByDescending(x => x.DeletedAt).Take(500).ToListAsync());
    }

    [RequirePermission("Jobs.Delete")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id)
    {
        var job = await _db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);
        if (job == null) return NotFound();
        if (!await CanAccessDepartment(job.DepartmentId)) return Forbid();

        // A replacement job may have been created after this record was moved to
        // the recycle bin. The active-only unique index allows that workflow, but
        // restoring the old row would then create two active duplicates.
        var activeDuplicateExists = await _db.Jobs.AnyAsync(x => x.DeduplicationKey == job.DeduplicationKey);
        if (activeDuplicateExists)
        {
            TempData["Error"] = $"Job {job.JobNumber} cannot be restored because an active job with the same project, department, contractor, date and description already exists.";
            return RedirectToAction(nameof(RecycleBin));
        }

        job.IsDeleted = false; job.DeletedAt = null; job.DeletedByUserId = null; job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Restore", "Job", id, job.JobNumber);
        TempData["Success"] = $"Job {job.JobNumber} restored.";
        return RedirectToAction(nameof(RecycleBin));
    }

    [RequirePermission("Jobs.Export")]
    public async Task<FileResult> ExportCsv()
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var jobs = _db.Jobs.AsNoTracking().Include(x => x.Project).Include(x => x.Department).Include(x => x.Contractor).Include(x => x.Lines).ThenInclude(x => x.Currency).AsQueryable();
        if (allowed != null) jobs = jobs.Where(x => allowed.Contains(x.DepartmentId));
        var rows = await jobs.OrderByDescending(x => x.JobDate).ToListAsync();
        var sb = new StringBuilder("Job Number,Date,Project,Department,Contractor,Description,Status,Payment Status,Currency Totals\n");
        foreach (var j in rows)
        {
            var totals = string.Join(" | ", j.Lines.GroupBy(x => x.Currency?.Code ?? "").Select(g => $"{g.Key} {g.Sum(x => x.Total):0.00}"));
            sb.AppendLine(string.Join(',', Csv(j.JobNumber), Csv(j.JobDate.ToString("yyyy-MM-dd")), Csv(j.Project?.Name), Csv(j.Department?.Name), Csv(j.Contractor?.Name), Csv(j.Description), Csv(j.Status.ToString()), Csv(j.PaymentStatus.ToString()), Csv(totals)));
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"jobs-{DateTime.Today:yyyyMMdd}.csv");
    }

    private async Task FillListsAsync(JobFormVm vm)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var projects = await _db.Projects.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        var projectIdValues = projects.Select(p => p.Id).ToList();
        var projectIds = await _db.ProjectWorkspaces
            .Where(x => projectIdValues.Contains(x.ProjectId))
            .ToDictionaryAsync(x => x.ProjectId, x => x.UniqueId);
        vm.Projects = projects.Select(x => new SelectListItem($"{(projectIds.TryGetValue(x.Id, out var uid) ? uid : x.Code)} - {x.Name}", x.Id.ToString())).ToList();
        vm.Departments = await _db.Departments.Where(x => x.IsActive && (allowed == null || allowed.Contains(x.Id))).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
        vm.Contractors = await _db.Contractors.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
        vm.Units = await _db.Units.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync();
        vm.Currencies = await _db.Currencies.Where(x => x.IsActive).OrderBy(x => x.Code).Select(x => new SelectListItem(x.Code, x.Id.ToString())).ToListAsync();
    }

    private void NormalizeLines(JobFormVm vm)
    {
        vm.Lines = (vm.Lines ?? new List<JobLineInputVm>()).Where(x => !string.IsNullOrWhiteSpace(x.Description) || x.Quantity > 0 || x.UnitRate > 0).ToList();
        if (vm.Lines.Count == 0) ModelState.AddModelError(nameof(vm.Lines), "At least one job line is required.");
    }

    private async Task ValidateBusinessRulesAsync(JobFormVm vm, int? currentId)
    {
        if (vm.ServiceStart.HasValue && vm.ServiceEnd.HasValue && vm.ServiceEnd < vm.ServiceStart)
            ModelState.AddModelError(nameof(vm.ServiceEnd), "Service end date cannot be before start date.");
        var duplicateKey = BuildDeduplicationKey(vm);
        var duplicate = await _db.Jobs.AnyAsync(x => x.Id != currentId && x.DeduplicationKey == duplicateKey);
        if (duplicate) ModelState.AddModelError(string.Empty, "A duplicate job already exists for the same project, department, contractor, date and description.");
        var invoice = vm.InvoiceNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(invoice))
        {
            var upper = invoice.ToUpper();
            var duplicateInvoiceInJobs = await _db.Jobs.IgnoreQueryFilters().AnyAsync(x => !x.IsDeleted && x.Id != currentId && x.InvoiceNumber != null && x.InvoiceNumber.ToUpper() == upper);
            var invoiceColumnIds = (await _db.SheetColumns.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync())
                .Where(x => IsInvoiceColumnName(x.Name)).Select(x => x.Id).ToList();
            var duplicateInvoiceInSheets = invoiceColumnIds.Count > 0 && await _db.SheetCells.AsNoTracking()
                .AnyAsync(x => invoiceColumnIds.Contains(x.SheetColumnId) && x.Value != null && x.Value.ToUpper() == upper);
            if (duplicateInvoiceInJobs || duplicateInvoiceInSheets)
                ModelState.AddModelError(nameof(vm.InvoiceNumber), $"Invoice number {invoice} already exists. Duplicate invoices are not allowed anywhere in BGP.");
        }
        var duplicateLine = vm.Lines.GroupBy(x => new { Desc = (x.Description ?? "").Trim().ToLowerInvariant(), x.UnitId, x.CurrencyId, x.Quantity, x.UnitRate }).Any(g => g.Count() > 1);
        if (duplicateLine) ModelState.AddModelError(nameof(vm.Lines), "Duplicate line items are not allowed.");
    }

    private static bool IsInvoiceColumnName(string name)
    {
        var normalized = new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Contains("INVOICE") && (normalized.EndsWith("NO") || normalized.Contains("NUMBER"));
    }

    private async Task<bool> CanAccessDepartment(int departmentId)
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        return allowed == null || allowed.Contains(departmentId);
    }

    private static string BuildDeduplicationKey(JobFormVm vm)
    {
        var raw = $"{vm.ProjectId}|{vm.DepartmentId}|{vm.ContractorId}|{vm.JobDate:yyyy-MM-dd}|{(vm.Description ?? string.Empty).Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627);

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
