using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Sheets.View")]
public class SheetsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IBusinessIdService _ids;
    private readonly IAuditService _audit;
    private readonly IPermissionService _permissions;
    private readonly IWebHostEnvironment _environment;
    private readonly IJobNumberService _jobNumbers;
    private readonly UserManager<ApplicationUser> _userManager;

    public SheetsController(ApplicationDbContext db, IBusinessIdService ids, IAuditService audit, IPermissionService permissions, IWebHostEnvironment environment, IJobNumberService jobNumbers, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _ids = ids;
        _audit = audit;
        _permissions = permissions;
        _environment = environment;
        _jobNumbers = jobNumbers;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(int? projectId, int? departmentId)
    {
        var query = _db.ProjectSheets.AsNoTracking().Include(x => x.Project).Include(x => x.Department).Where(x => x.IsActive).AsQueryable();
        var allowedDepartments = await _permissions.GetAllowedDepartmentIdsAsync(User);
        if (allowedDepartments != null) query = query.Where(x => x.DepartmentId == null || allowedDepartments.Contains(x.DepartmentId.Value));
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId.Value);
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);

        var departmentQuery = _db.Departments.AsNoTracking().Where(x => x.IsActive).AsQueryable();
        if (allowedDepartments != null) departmentQuery = departmentQuery.Where(x => allowedDepartments.Contains(x.Id));

        return View(new SheetIndexVm
        {
            Sheets = await query.OrderBy(x => x.ProjectId).ThenBy(x => x.DepartmentId).ThenBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(),
            Projects = await _db.Projects.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(),
            Departments = await departmentQuery.OrderBy(x => x.Name).ToListAsync()
        });
    }

    public async Task<IActionResult> Details(int id, string? q)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().Include(x => x.Project).Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();

        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var rowsQuery = _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == id).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            rowsQuery = rowsQuery.Where(x => x.UniqueId.Contains(search) || x.Cells.Any(c => c.Value != null && c.Value.Contains(search)));
        }

        var rowLimit = string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase) || string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase) ? 5000 : 500;
        var rows = await rowsQuery.OrderByDescending(x => x.CreatedAt).Take(rowLimit).ToListAsync();
        if (string.Equals(sheet.Name, "Contract Summary", StringComparison.OrdinalIgnoreCase))
            await ApplyContractSummaryComputedValuesAsync(sheet, columns, rows);

        int? linkedSheetId = null;
        if (sheet.ProjectId.HasValue && columns.Any(x => NormalizeName(x.Name) is "EXPORTJOBNO" or "IMPORTJOBNO"))
        {
            linkedSheetId = await _db.ProjectSheets.AsNoTracking()
                .Where(x => x.ProjectId == sheet.ProjectId && x.IsActive && x.Name == "Import and Export")
                .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        }

        var workflowNames = new[] { "Requisition", "Service Business", "Purchase Order", "Service Logistics", "Invoice Reception" };
        var workflowSheets = sheet.ProjectId.HasValue
            ? await _db.ProjectSheets.AsNoTracking()
                .Where(x => x.ProjectId == sheet.ProjectId.Value && x.IsActive && workflowNames.Contains(x.Name))
                .OrderBy(x => x.SortOrder).ToListAsync()
            : new List<ProjectSheet>();
        var approvalManagers = string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)
            ? await GetApprovalManagersAsync()
            : new List<ApplicationUser>();

        return View(new SheetDetailsVm
        {
            Sheet = sheet, Columns = columns, Rows = rows, Search = q, LinkedImportExportSheetId = linkedSheetId, WorkflowSheets = workflowSheets, ApprovalManagers = approvalManagers
        });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpGet]
    public async Task<IActionResult> Columns(int? sheetId)
    {
        var allowedDepartments = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var sheetQuery = _db.ProjectSheets.AsNoTracking().Include(x => x.Project).Include(x => x.Department).Where(x => x.IsActive).AsQueryable();
        if (allowedDepartments != null) sheetQuery = sheetQuery.Where(x => x.DepartmentId == null || allowedDepartments.Contains(x.DepartmentId.Value));
        var sheets = await sheetQuery.OrderBy(x => x.ProjectId).ThenBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync();
        var selected = sheetId.HasValue ? sheets.FirstOrDefault(x => x.Id == sheetId.Value) : sheets.FirstOrDefault();
        var columns = selected == null
            ? new List<SheetColumn>()
            : await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == selected.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        return View(new ColumnManagementVm { Sheets = sheets, SelectedSheet = selected, Columns = columns });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, int? projectId, int? departmentId)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name) || (!projectId.HasValue && !departmentId.HasValue))
        {
            TempData["Error"] = "Sheet name and either a project or department are required.";
            return RedirectBack(projectId, departmentId);
        }
        if (projectId.HasValue && !await _db.Projects.AnyAsync(x => x.Id == projectId.Value)) return NotFound();
        if (departmentId.HasValue && !await _db.Departments.AnyAsync(x => x.Id == departmentId.Value)) return NotFound();
        if (!await CanAccessDepartmentAsync(departmentId)) return Forbid();

        var maxOrder = await _db.ProjectSheets.Where(x => x.ProjectId == projectId && x.DepartmentId == departmentId).Select(x => (int?)x.SortOrder).MaxAsync() ?? 0;
        var sheet = new ProjectSheet
        {
            UniqueId = await _ids.NextAsync("SHT"),
            Name = name,
            ProjectId = projectId,
            DepartmentId = departmentId,
            SortOrder = maxOrder + 1,
            IsActive = true
        };
        _db.ProjectSheets.Add(sheet);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Create", "Sheet", sheet.Id, $"{sheet.UniqueId} - {sheet.Name}");
        TempData["Success"] = $"Sheet \"{sheet.Name}\" created.";
        return RedirectToAction(nameof(Details), new { id = sheet.Id });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(int id, string name)
    {
        var sheet = await _db.ProjectSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Sheet name is required.";
            return RedirectToAction(nameof(Details), new { id });
        }
        sheet.Name = name;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Rename", "Sheet", sheet.Id, sheet.Name);
        TempData["Success"] = "Sheet renamed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var sheet = await _db.ProjectSheets.FindAsync(id);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        sheet.IsActive = false;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Delete", "Sheet", sheet.Id, $"Soft deleted {sheet.UniqueId}");
        TempData["Success"] = "Sheet removed from active view.";
        return RedirectBack(sheet.ProjectId, sheet.DepartmentId);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddColumn(int sheetId, string name, SheetColumnType columnType, bool isRequired = false, string? options = null, string? source = null)
    {
        var sheet = await _db.ProjectSheets.FindAsync(sheetId);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Column name is required.";
            return RedirectAfterColumnChange(sheetId, source);
        }
        if (await _db.SheetColumns.AnyAsync(x => x.ProjectSheetId == sheetId && x.Name == name))
        {
            TempData["Error"] = "A column with this name already exists in the sheet.";
            return RedirectAfterColumnChange(sheetId, source);
        }

        var normalizedColumnName = NormalizeName(name);
        if (normalizedColumnName.Contains("REQUISITIONNO") && string.IsNullOrWhiteSpace(options))
        {
            columnType = SheetColumnType.Dropdown;
            options = "__REQUISITIONS__";
        }

        var maxOrder = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheetId).Select(x => (int?)x.SortOrder).MaxAsync() ?? 0;
        var column = new SheetColumn
        {
            ProjectSheetId = sheetId,
            Name = name,
            ColumnType = columnType,
            IsRequired = isRequired,
            Options = options?.Trim(),
            SortOrder = maxOrder + 1
        };
        _db.SheetColumns.Add(column);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "AddColumn", "Sheet", sheet.Id, $"{column.Name} ({column.ColumnType})");
        TempData["Success"] = $"Column \"{column.Name}\" added.";
        return RedirectAfterColumnChange(sheetId, source);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameColumn(int id, string name, string? source = null)
    {
        var column = await _db.SheetColumns.FindAsync(id);
        if (column == null) return NotFound();
        var columnSheet = await _db.ProjectSheets.FindAsync(column.ProjectSheetId);
        if (columnSheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(columnSheet.DepartmentId)) return Forbid();
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Column name is required.";
            return RedirectAfterColumnChange(column.ProjectSheetId, source);
        }
        if (await _db.SheetColumns.AnyAsync(x => x.ProjectSheetId == column.ProjectSheetId && x.Id != id && x.Name == name))
        {
            TempData["Error"] = "A column with this name already exists.";
            return RedirectAfterColumnChange(column.ProjectSheetId, source);
        }
        column.Name = name;
        if (NormalizeName(name).Contains("REQUISITIONNO"))
        {
            column.ColumnType = SheetColumnType.Dropdown;
            column.Options = "__REQUISITIONS__";
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "Column renamed.";
        return RedirectAfterColumnChange(column.ProjectSheetId, source);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveColumn(int id, int direction, string? source = null)
    {
        var column = await _db.SheetColumns.FindAsync(id);
        if (column == null) return NotFound();
        var columnSheet = await _db.ProjectSheets.FindAsync(column.ProjectSheetId);
        if (columnSheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(columnSheet.DepartmentId)) return Forbid();
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == column.ProjectSheetId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var index = columns.FindIndex(x => x.Id == id);
        var targetIndex = index + Math.Sign(direction);
        if (index >= 0 && targetIndex >= 0 && targetIndex < columns.Count)
        {
            (columns[index].SortOrder, columns[targetIndex].SortOrder) = (columns[targetIndex].SortOrder, columns[index].SortOrder);
            await _db.SaveChangesAsync();
        }
        return RedirectAfterColumnChange(column.ProjectSheetId, source);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteColumn(int id, string? source = null)
    {
        var column = await _db.SheetColumns.FindAsync(id);
        if (column == null) return NotFound();
        var columnSheet = await _db.ProjectSheets.FindAsync(column.ProjectSheetId);
        if (columnSheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(columnSheet.DepartmentId)) return Forbid();
        var sheetId = column.ProjectSheetId;
        var cells = await _db.SheetCells.Where(x => x.SheetColumnId == id).ToListAsync();
        _db.SheetCells.RemoveRange(cells);
        _db.SheetColumns.Remove(column);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Column deleted.";
        return RedirectAfterColumnChange(sheetId, source);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpGet]
    public async Task<IActionResult> AddRow(int sheetId, string? requisitionNo = null)
    {
        var vm = await BuildRowVm(sheetId, null);
        if (vm == null) return NotFound();

        if (string.Equals(vm.Sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(requisitionNo))
        {
            var prepared = await PrefillExistingRequisitionAsync(vm, requisitionNo);
            if (!prepared)
            {
                TempData["Error"] = $"Requisition {requisitionNo} is not open for new items.";
                return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
            }
        }

        return View("RowForm", vm);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRowPost(int sheetId)
    {
        var sheet = await _db.ProjectSheets.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == sheetId && x.IsActive);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheetId).OrderBy(x => x.SortOrder).ToListAsync();
        var form = await Request.ReadFormAsync();
        var itemNumberMethod = form["itemNumberMethod"].ToString().Trim();
        var releasedItemNumberText = form["releasedItemNumber"].ToString().Trim();
        ReleasedItemNumber? releasedNumberToUse = null;

        var submittedValues = new Dictionary<int, string?>();
        foreach (var column in columns)
        {
            var key = $"col_{column.Id}";
            if (column.ColumnType != SheetColumnType.File)
                submittedValues[column.Id] = form[key].ToString().Trim();
        }

        ApplySheetDefaults(sheet, columns, submittedValues);

        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase) && string.Equals(itemNumberMethod, "reuse", StringComparison.OrdinalIgnoreCase))
        {
            var reqColumn = FindColumn(columns, "Requisition No");
            var itemColumn = FindColumn(columns, "Item No");
            var reqNo = GetValue(submittedValues, reqColumn);
            if (string.IsNullOrWhiteSpace(reqNo) || !int.TryParse(releasedItemNumberText, out var releasedItemNumber) || itemColumn == null)
            {
                ModelState.AddModelError(string.Empty, "Select a valid released item number to reuse.");
            }
            else
            {
                releasedNumberToUse = await _db.ReleasedItemNumbers.FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.RequisitionNumber == reqNo && x.ItemNumber == releasedItemNumber && !x.IsUsed);
                if (releasedNumberToUse == null)
                    ModelState.AddModelError(string.Empty, $"Item {releasedItemNumber:000} is no longer available for reuse.");
                else
                    submittedValues[itemColumn.Id] = releasedItemNumber.ToString("000", CultureInfo.InvariantCulture);
            }
        }

        foreach (var column in columns)
        {
            if (!column.IsRequired || IsSystemManagedColumn(column)) continue;
            var key = $"col_{column.Id}";
            var valueForValidation = column.ColumnType == SheetColumnType.File
                ? form.Files.GetFile(key)?.FileName
                : submittedValues.GetValueOrDefault(column.Id);
            if (string.IsNullOrWhiteSpace(valueForValidation))
                ModelState.AddModelError(string.Empty, $"{column.Name} is required.");
        }
        await ValidateRowBusinessRulesAsync(sheet, columns, submittedValues, null);

        if (!ModelState.IsValid)
            return View("RowForm", await BuildRowVmWithValuesAsync(sheet, null, columns, submittedValues));

        if (string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase))
        {
            var creation = await CreateServiceJobFromRequisitionAsync(sheet, columns, submittedValues);
            if (!creation.Success)
            {
                ModelState.AddModelError(string.Empty, creation.Error ?? "The service job could not be created.");
                return View("RowForm", await BuildRowVmWithValuesAsync(sheet, null, columns, submittedValues));
            }

            await _audit.WriteAsync(HttpContext, "CreateJobFromRequisition", "ServiceJob", creation.JobNumber ?? string.Empty, $"{creation.JobNumber} from {creation.RequisitionNo} with {creation.ItemCount} item(s)");
            TempData["Success"] = $"Job {creation.JobNumber} created from requisition {creation.RequisitionNo}. All {creation.ItemCount} requisition item(s) were added automatically under the same job number.";
            return RedirectToAction(nameof(Details), new { id = sheetId, q = creation.JobNumber });
        }

        var releasedNumberId = releasedNumberToUse?.Id;
        var saveTransaction = releasedNumberId.HasValue ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;
        if (saveTransaction != null)
        {
            releasedNumberToUse = await _db.ReleasedItemNumbers.FirstOrDefaultAsync(x => x.Id == releasedNumberId!.Value && !x.IsUsed);
            if (releasedNumberToUse == null)
            {
                ModelState.AddModelError(string.Empty, "That released item number has just been used by another request. Choose another number or Auto Generate.");
                await saveTransaction.RollbackAsync();
                await saveTransaction.DisposeAsync();
                return View("RowForm", await BuildRowVmWithValuesAsync(sheet, null, columns, submittedValues));
            }
        }
        await ApplyGeneratedValuesForNewRowAsync(sheet, columns, submittedValues);

        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase))
        {
            var reqColumn = FindColumn(columns, "Requisition No");
            var itemColumn = FindColumn(columns, "Item No");
            var reqNo = GetValue(submittedValues, reqColumn);
            var itemNoText = GetValue(submittedValues, itemColumn);
            if (!string.IsNullOrWhiteSpace(reqNo) && !string.IsNullOrWhiteSpace(itemNoText))
            {
                var duplicateItem = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
                    .Where(x => x.ProjectSheetId == sheet.Id)
                    .AnyAsync(x => x.Cells.Any(c => c.SheetColumnId == reqColumn!.Id && c.Value == reqNo)
                                && x.Cells.Any(c => c.SheetColumnId == itemColumn!.Id && c.Value == itemNoText));
                if (duplicateItem)
                {
                    ModelState.AddModelError(string.Empty, $"Item {itemNoText} already exists in requisition {reqNo}.");
                    if (saveTransaction != null)
                    {
                        await saveTransaction.RollbackAsync();
                        await saveTransaction.DisposeAsync();
                    }
                    return View("RowForm", await BuildRowVmWithValuesAsync(sheet, null, columns, submittedValues));
                }
            }
        }

        var row = new SheetRow { ProjectSheetId = sheetId, UniqueId = await _ids.NextAsync("ROW") };
        _db.SheetRows.Add(row);
        await _db.SaveChangesAsync();

        foreach (var column in columns)
        {
            var value = column.ColumnType == SheetColumnType.File
                ? await ReadColumnValueAsync(sheet, column, form, null)
                : submittedValues.GetValueOrDefault(column.Id);
            if (column.ColumnType == SheetColumnType.Checkbox && string.IsNullOrWhiteSpace(value)) value = "false";
            if (!string.IsNullOrWhiteSpace(value))
                _db.SheetCells.Add(new SheetCell { SheetRowId = row.Id, SheetColumnId = column.Id, Value = value });
        }
        await _db.SaveChangesAsync();
        if (releasedNumberToUse != null)
        {
            releasedNumberToUse.IsUsed = true;
            releasedNumberToUse.UsedByRowId = row.Id;
            releasedNumberToUse.UsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        if (saveTransaction != null)
        {
            await saveTransaction.CommitAsync();
            await saveTransaction.DisposeAsync();
        }
        if (string.Equals(sheet.Name, "Requisition Approval", StringComparison.OrdinalIgnoreCase))
            await NotifyServiceApprovalSubmittedAsync(sheet, columns, submittedValues, row.Id);
        await _audit.WriteAsync(HttpContext, "AddRow", "SheetRow", row.Id, $"{row.UniqueId} in {sheet.UniqueId}");
        var createdRequisition = string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)
            ? GetValue(submittedValues, FindColumn(columns, "Requisition No"))
            : null;
        TempData["Success"] = string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)
            ? $"Item added to requisition {createdRequisition}. Use Add Item for more lines under the same requisition, then complete the requisition when all items are entered."
            : $"Row {row.UniqueId} added.";
        return RedirectToAction(nameof(Details), new { id = sheetId });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpGet]
    public async Task<IActionResult> EditRow(long id)
    {
        var row = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return NotFound();
        var vm = await BuildRowVm(row.ProjectSheetId, row);
        return vm == null ? NotFound() : View("RowForm", vm);
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRowPost(long id)
    {
        var row = await _db.SheetRows.Include(x => x.Cells).FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return NotFound();
        var sheet = await _db.ProjectSheets.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == row.ProjectSheetId && x.IsActive);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheet.Id).OrderBy(x => x.SortOrder).ToListAsync();
        var form = await Request.ReadFormAsync();

        var submittedValues = new Dictionary<int, string?>();
        foreach (var column in columns)
        {
            var current = row.Cells.FirstOrDefault(x => x.SheetColumnId == column.Id)?.Value;
            if (column.ColumnType == SheetColumnType.File)
            {
                submittedValues[column.Id] = current;
            }
            else if (IsSystemManagedColumn(column))
            {
                submittedValues[column.Id] = current;
            }
            else
            {
                submittedValues[column.Id] = form[$"col_{column.Id}"].ToString().Trim();
            }
        }

        ApplySheetDefaults(sheet, columns, submittedValues);
        foreach (var column in columns)
        {
            if (!column.IsRequired || IsSystemManagedColumn(column)) continue;
            var current = row.Cells.FirstOrDefault(x => x.SheetColumnId == column.Id)?.Value;
            var valueForValidation = column.ColumnType == SheetColumnType.File
                ? form.Files.GetFile($"col_{column.Id}")?.FileName ?? current
                : submittedValues.GetValueOrDefault(column.Id);
            if (string.IsNullOrWhiteSpace(valueForValidation))
                ModelState.AddModelError(string.Empty, $"{column.Name} is required.");
        }
        await ValidateRowBusinessRulesAsync(sheet, columns, submittedValues, row.Id);

        if (!ModelState.IsValid)
            return View("RowForm", await BuildRowVmWithValuesAsync(sheet, row, columns, submittedValues));

        // If an older row predates auto numbering, assign its number once. Existing
        // generated numbers are immutable even if the department/type later changes.
        await ApplyGeneratedValuesForExistingRowIfMissingAsync(sheet, columns, submittedValues);

        foreach (var column in columns)
        {
            var current = row.Cells.FirstOrDefault(x => x.SheetColumnId == column.Id)?.Value;
            var value = column.ColumnType == SheetColumnType.File
                ? await ReadColumnValueAsync(sheet, column, form, current)
                : submittedValues.GetValueOrDefault(column.Id);
            if (column.ColumnType == SheetColumnType.Checkbox && string.IsNullOrWhiteSpace(value)) value = "false";

            var cell = row.Cells.FirstOrDefault(x => x.SheetColumnId == column.Id);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (cell != null) _db.SheetCells.Remove(cell);
            }
            else if (cell == null)
            {
                _db.SheetCells.Add(new SheetCell { SheetRowId = row.Id, SheetColumnId = column.Id, Value = value });
            }
            else
            {
                cell.Value = value;
            }
        }

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "EditRow", "SheetRow", row.Id, row.UniqueId);
        TempData["Success"] = $"Row {row.UniqueId} updated.";
        return RedirectToAction(nameof(Details), new { id = sheet.Id });
    }

    [Authorize(Roles = "Project Manager,Department Head,Administrator,Super Admin")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveService(long id, bool approve, string? comment)
    {
        var row = await _db.SheetRows.Include(x => x.Cells).FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return NotFound();
        var sheet = await _db.ProjectSheets.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == row.ProjectSheetId && x.IsActive);
        if (sheet == null || !string.Equals(sheet.Name, "Requisition Approval", StringComparison.OrdinalIgnoreCase)) return BadRequest();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var selectedApprover = GetCellValue(row, FindColumn(columns, "Approver / Manager"));
        var currentUser = await _userManager.GetUserAsync(User);
        var elevatedApprover = User.IsInRole("Administrator") || User.IsInRole("Super Admin");
        if (!string.IsNullOrWhiteSpace(selectedApprover) && !elevatedApprover)
        {
            var isAssignedApprover = string.Equals(currentUser?.Email, selectedApprover, StringComparison.OrdinalIgnoreCase)
                || string.Equals(currentUser?.UserName, selectedApprover, StringComparison.OrdinalIgnoreCase);
            if (!isAssignedApprover)
            {
                TempData["Error"] = "This approval is assigned to another manager. Only the selected manager or an administrator can make the decision.";
                return RedirectToAction(nameof(Details), new { id = sheet.Id });
            }
        }
        var currentStatus = GetCellValue(row, FindColumn(columns, "Approval Status"));
        if (!string.IsNullOrWhiteSpace(currentStatus) && !string.Equals(currentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = $"This requisition approval has already been {currentStatus.ToLowerInvariant()}.";
            return RedirectToAction(nameof(Details), new { id = sheet.Id });
        }
        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            TempData["Error"] = "A rejection reason is required.";
            return RedirectToAction(nameof(Details), new { id = sheet.Id, q = GetCellValue(row, FindColumn(columns, "Requisition No")) });
        }
        SetCellValue(row, FindColumn(columns, "Approval Status"), approve ? "Approved" : "Rejected");
        SetCellValue(row, FindColumn(columns, "Manager Comment"), comment?.Trim());
        SetCellValue(row, FindColumn(columns, "Approved By"), User.Identity?.Name);
        SetCellValue(row, FindColumn(columns, "Decision Date"), DateTime.Today.ToString("yyyy-MM-dd"));
        row.UpdatedAt = DateTime.UtcNow;

        var requester = GetCellValue(row, FindColumn(columns, "Requested By"));
        var requisition = GetCellValue(row, FindColumn(columns, "Requisition No"));
        if (sheet.ProjectId.HasValue && !string.IsNullOrWhiteSpace(requisition))
            await SetRequisitionStatusAsync(sheet.ProjectId.Value, requisition, approve ? "Approved" : "Rejected");
        await _db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(requester))
        {
            var user = await _userManager.FindByEmailAsync(requester) ?? await _userManager.FindByNameAsync(requester);
            if (user != null)
                await CreateNotificationAsync(user.Id, approve ? "Requisition approved" : "Requisition rejected",
                    $"{requisition ?? "Service request"} was {(approve ? "approved" : "rejected")} by the project manager ({User.Identity?.Name}).",
                    Url.Action(nameof(Details), "Sheets", new { id = sheet.Id, q = requisition }) ?? $"/Sheets/Details/{sheet.Id}");
        }
        await _audit.WriteAsync(HttpContext, approve ? "Approve" : "Reject", "RequisitionApproval", row.Id, requisition);
        TempData["Success"] = approve ? "Requisition approved. The requester was notified and Service Approval can now be created." : "Requisition rejected with reason. The requester was notified and the record remains in history.";
        return RedirectToAction(nameof(Details), new { id = sheet.Id });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRow(long id)
    {
        var row = await _db.SheetRows.Include(x => x.Cells).FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return NotFound();
        var sheetId = row.ProjectSheetId;
        var sheet = await _db.ProjectSheets.FindAsync(sheetId);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();

        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase))
        {
            var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
            var reqColumn = FindColumn(columns, "Requisition No");
            var itemColumn = FindColumn(columns, "Item No");
            var statusColumn = FindColumn(columns, "Status");
            var reqNo = GetCellValue(row, reqColumn);
            var itemNoText = GetCellValue(row, itemColumn);
            var status = GetCellValue(row, statusColumn);

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("Pending", StringComparison.OrdinalIgnoreCase) && !status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = $"Item cannot be deleted because requisition {reqNo} is {status}. Approved/submitted business records must remain in history.";
                return RedirectToAction(nameof(Details), new { id = sheetId, q = reqNo });
            }

            if (!string.IsNullOrWhiteSpace(reqNo) && int.TryParse(itemNoText, out var itemNo))
            {
                var released = await _db.ReleasedItemNumbers.FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.RequisitionNumber == reqNo && x.ItemNumber == itemNo);
                if (released == null)
                {
                    _db.ReleasedItemNumbers.Add(new ReleasedItemNumber
                    {
                        ProjectSheetId = sheet.Id, RequisitionNumber = reqNo, ItemNumber = itemNo, ReleasedFromRowId = row.Id,
                        ReleasedByUserId = _userManager.GetUserId(User) ?? string.Empty, ReleasedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    released.IsUsed = false; released.UsedByRowId = null; released.UsedAt = null; released.ReleasedFromRowId = row.Id;
                    released.ReleasedByUserId = _userManager.GetUserId(User) ?? string.Empty; released.ReleasedAt = DateTime.UtcNow;
                }
            }
        }

        _db.SheetRows.Remove(row);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "DeleteRow", "SheetRow", id, row.UniqueId);
        TempData["Success"] = "Record deleted. Its item number is available for controlled reuse where applicable.";
        return RedirectToAction(nameof(Details), new { id = sheetId });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteRequisition(int sheetId, string requisitionNo, string approverEmail)
    {
        var sheet = await _db.ProjectSheets.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == sheetId && x.IsActive);
        if (sheet == null) return NotFound();
        if (!string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)) return BadRequest();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();

        requisitionNo = (requisitionNo ?? string.Empty).Trim();
        approverEmail = (approverEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requisitionNo))
        {
            TempData["Error"] = "Requisition number is required.";
            return RedirectToAction(nameof(Details), new { id = sheetId });
        }

        var eligibleManagers = await GetApprovalManagersAsync();
        var manager = eligibleManagers.FirstOrDefault(x =>
            string.Equals(x.Email, approverEmail, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.UserName, approverEmail, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Id, approverEmail, StringComparison.OrdinalIgnoreCase));
        if (manager == null)
        {
            TempData["Error"] = "Select a valid manager/approver before submitting the requisition.";
            return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
        }
        var managerIdentity = manager.Email ?? manager.UserName ?? manager.Id;

        var columns = await EnsureRequisitionApprovalColumnsAsync(sheetId);
        var requisitionColumn = FindColumn(columns, "Requisition No");
        var statusColumn = FindColumn(columns, "Status");
        var approverColumn = FindColumn(columns, "Approver / Manager");
        var commentColumn = FindColumn(columns, "Approval Comment");
        var decisionByColumn = FindColumn(columns, "Decision By");
        var decisionDateColumn = FindColumn(columns, "Decision Date");
        if (requisitionColumn == null || statusColumn == null)
        {
            TempData["Error"] = "Requisition columns are not configured correctly.";
            return RedirectToAction(nameof(Details), new { id = sheetId });
        }

        var upper = requisitionNo.ToUpperInvariant();
        var rowIds = await _db.SheetCells.AsNoTracking()
            .Where(x => x.SheetColumnId == requisitionColumn.Id && x.Value != null && x.Value.ToUpper() == upper)
            .Select(x => x.SheetRowId)
            .ToListAsync();
        var rows = await _db.SheetRows.Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == sheetId && rowIds.Contains(x.Id))
            .ToListAsync();
        if (rows.Count == 0)
        {
            TempData["Error"] = $"Requisition {requisitionNo} was not found.";
            return RedirectToAction(nameof(Details), new { id = sheetId });
        }
        if (rows.Any(x =>
            !string.IsNullOrWhiteSpace(GetCellValue(x, statusColumn)) &&
            !string.Equals(GetCellValue(x, statusColumn), "Draft", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(GetCellValue(x, statusColumn), "Pending", StringComparison.OrdinalIgnoreCase)))
        {
            TempData["Error"] = $"Requisition {requisitionNo} has already been submitted or decided.";
            return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
        }

        foreach (var row in rows)
        {
            SetCellValue(row, statusColumn, "Waiting Approval");
            SetCellValue(row, approverColumn, managerIdentity);
            SetCellValue(row, commentColumn, null);
            SetCellValue(row, decisionByColumn, null);
            SetCellValue(row, decisionDateColumn, null);
            row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var url = Url.Action(nameof(Details), "Sheets", new { id = sheetId, q = requisitionNo }) ?? $"/Sheets/Details/{sheetId}?q={Uri.EscapeDataString(requisitionNo)}";
        await CreateNotificationAsync(manager.Id, "Requisition waiting for your decision", $"{requisitionNo} is waiting for your Accept or Reject decision.", url);
        var currentId = _userManager.GetUserId(User);
        if (!string.IsNullOrWhiteSpace(currentId))
            await CreateNotificationAsync(currentId, "Requisition submitted", $"{requisitionNo} was sent to {manager.FullName ?? managerIdentity} and is now Waiting Approval.", url);

        await _audit.WriteAsync(HttpContext, "SubmitForApproval", "Requisition", requisitionNo, $"{rows.Count} item(s); approver {managerIdentity}");
        TempData["Success"] = $"Requisition {requisitionNo} is now Waiting Approval and has been sent to {manager.FullName ?? managerIdentity}.";
        return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
    }

    [Authorize(Roles = "Project Manager,Department Head,Administrator,Super Admin")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideRequisition(int sheetId, string requisitionNo, bool approve, string? reason)
    {
        var sheet = await _db.ProjectSheets.Include(x => x.Project).FirstOrDefaultAsync(x => x.Id == sheetId && x.IsActive);
        if (sheet == null) return NotFound();
        if (!string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)) return BadRequest();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        requisitionNo = (requisitionNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requisitionNo)) return BadRequest();

        var columns = await EnsureRequisitionApprovalColumnsAsync(sheetId);
        var requisitionColumn = FindColumn(columns, "Requisition No");
        var statusColumn = FindColumn(columns, "Status");
        var approverColumn = FindColumn(columns, "Approver / Manager");
        var commentColumn = FindColumn(columns, "Approval Comment");
        var decisionByColumn = FindColumn(columns, "Decision By");
        var decisionDateColumn = FindColumn(columns, "Decision Date");
        var requesterColumn = FindColumn(columns, "Requested By");
        if (requisitionColumn == null || statusColumn == null) return BadRequest();

        var upper = requisitionNo.ToUpperInvariant();
        var rows = await _db.SheetRows.Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == sheetId && x.Cells.Any(c => c.SheetColumnId == requisitionColumn.Id && c.Value != null && c.Value.ToUpper() == upper))
            .ToListAsync();
        if (rows.Count == 0) return NotFound();
        var status = GetCellValue(rows[0], statusColumn);
        if (!string.Equals(status, "Waiting Approval", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = $"Requisition {requisitionNo} is not waiting for approval.";
            return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
        }

        var assignedApprover = GetCellValue(rows[0], approverColumn);
        var currentUser = await _userManager.GetUserAsync(User);
        var elevated = User.IsInRole("Administrator") || User.IsInRole("Super Admin");
        if (!string.IsNullOrWhiteSpace(assignedApprover) && !elevated)
        {
            var assigned = string.Equals(currentUser?.Email, assignedApprover, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(currentUser?.UserName, assignedApprover, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(currentUser?.Id, assignedApprover, StringComparison.OrdinalIgnoreCase);
            if (!assigned) return Forbid();
        }
        if (!approve && string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "A rejection reason is required.";
            return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
        }

        var decision = approve ? "Approved" : "Rejected";
        var decisionBy = currentUser?.Email ?? currentUser?.UserName ?? User.Identity?.Name ?? "Unknown";
        foreach (var row in rows)
        {
            SetCellValue(row, statusColumn, decision);
            SetCellValue(row, commentColumn, reason?.Trim());
            SetCellValue(row, decisionByColumn, decisionBy);
            SetCellValue(row, decisionDateColumn, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var requesterIdentity = GetCellValue(rows[0], requesterColumn);
        if (!string.IsNullOrWhiteSpace(requesterIdentity))
        {
            var requester = await _userManager.FindByEmailAsync(requesterIdentity) ?? await _userManager.FindByNameAsync(requesterIdentity);
            if (requester != null)
            {
                var url = Url.Action(nameof(Details), "Sheets", new { id = sheetId, q = requisitionNo }) ?? $"/Sheets/Details/{sheetId}";
                await CreateNotificationAsync(requester.Id, approve ? "Requisition accepted" : "Requisition rejected",
                    approve ? $"{requisitionNo} was accepted. Service Approval can now be created." : $"{requisitionNo} was rejected. Reason: {reason}", url);
            }
        }

        await _audit.WriteAsync(HttpContext, approve ? "Accept" : "Reject", "Requisition", requisitionNo, reason);
        TempData["Success"] = approve
            ? $"Requisition {requisitionNo} accepted. Service Approval is now available."
            : $"Requisition {requisitionNo} rejected. The reason has been recorded and the requisition remains in history.";
        return RedirectToAction(nameof(Details), new { id = sheetId, q = requisitionNo });
    }

    [Authorize]
    public async Task<IActionResult> PrintRow(long id)
    {
        var row = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).FirstOrDefaultAsync(x => x.Id == id);
        if (row == null) return NotFound();
        var sheet = await _db.ProjectSheets.AsNoTracking().Include(x => x.Project).Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == row.ProjectSheetId && x.IsActive);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        return View("PrintRecord", new SheetDetailsVm { Sheet = sheet, Columns = columns, Rows = new List<SheetRow> { row } });
    }

    [Authorize]
    public async Task<IActionResult> PrintRequisition(int sheetId, string requisitionNo)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().Include(x => x.Project).Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == sheetId && x.IsActive);
        if (sheet == null || !string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheetId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var reqColumn = FindColumn(columns, "Requisition No");
        if (reqColumn == null) return NotFound();
        var upper = (requisitionNo ?? string.Empty).Trim().ToUpperInvariant();
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == sheetId && x.Cells.Any(c => c.SheetColumnId == reqColumn.Id && c.Value != null && c.Value.ToUpper() == upper))
            .OrderBy(x => x.Id).ToListAsync();
        if (rows.Count == 0) return NotFound();
        return View("PrintRequisition", new SheetDetailsVm { Sheet = sheet, Columns = columns, Rows = rows, Search = requisitionNo });
    }

    private async Task<SheetRowFormVm?> BuildRowVm(int sheetId, SheetRow? row)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().Include(x => x.Project).Include(x => x.Department).FirstOrDefaultAsync(x => x.Id == sheetId && x.IsActive);
        if (sheet == null) return null;
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return null;
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheetId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var values = row?.Cells.ToDictionary(x => x.SheetColumnId, x => x.Value) ?? new Dictionary<int, string?>();
        return await BuildRowVmWithValuesAsync(sheet, row, columns, values);
    }

    private async Task<SheetRowFormVm> BuildRowVmWithValuesAsync(ProjectSheet sheet, SheetRow? row, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
        var vm = new SheetRowFormVm
        {
            Sheet = sheet,
            Row = row,
            Columns = columns,
            Values = values,
            Departments = await _db.Departments.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(),
            Projects = await _db.Projects.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(),
            Currencies = await _db.Currencies.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Code).ToListAsync(),
            ReferenceOptions = await BuildReferenceOptionsAsync(sheet),
            ApprovalManagers = string.Equals(sheet.Name, "Requisition Approval", StringComparison.OrdinalIgnoreCase) ? await GetApprovalManagersAsync() : new List<ApplicationUser>()
        };

        if (row == null && string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase))
        {
            var reqColumn = FindColumn(columns, "Requisition No");
            var reqNo = reqColumn == null ? null : values.GetValueOrDefault(reqColumn.Id)?.Trim();
            if (!string.IsNullOrWhiteSpace(reqNo))
            {
                vm.ReleasedItemNumbers = await _db.ReleasedItemNumbers.AsNoTracking()
                    .Where(x => x.ProjectSheetId == sheet.Id && x.RequisitionNumber == reqNo && !x.IsUsed)
                    .OrderBy(x => x.ItemNumber).Select(x => x.ItemNumber).ToListAsync();
            }
        }
        return vm;
    }

    private async Task<bool> PrefillExistingRequisitionAsync(SheetRowFormVm vm, string requisitionNo)
    {
        var reqColumn = FindColumn(vm.Columns, "Requisition No");
        var departmentColumn = FindColumn(vm.Columns, "Request Department");
        var statusColumn = FindColumn(vm.Columns, "Status");
        if (reqColumn == null || departmentColumn == null || statusColumn == null) return false;

        var upper = requisitionNo.Trim().ToUpperInvariant();
        var sourceRow = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == vm.Sheet.Id
                        && x.Cells.Any(c => c.SheetColumnId == reqColumn.Id && c.Value != null && c.Value.ToUpper() == upper))
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();
        if (sourceRow == null) return false;

        var status = GetCellValue(sourceRow, statusColumn);
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase) && !string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
            return false;

        vm.Values[reqColumn.Id] = GetCellValue(sourceRow, reqColumn);
        vm.Values[departmentColumn.Id] = GetCellValue(sourceRow, departmentColumn);

        foreach (var name in new[] { "Requested By", "Request Date" })
        {
            var column = FindColumn(vm.Columns, name);
            if (column != null)
                vm.Values[column.Id] = GetCellValue(sourceRow, column);
        }
        return true;
    }

    private async Task<(bool Success, string? Error, string? JobNumber, string? RequisitionNo, int ItemCount)> CreateServiceJobFromRequisitionAsync(
        ProjectSheet serviceSheet,
        List<SheetColumn> serviceColumns,
        Dictionary<int, string?> submittedValues)
    {
        if (!serviceSheet.ProjectId.HasValue)
            return (false, "The service job must belong to a project.", null, null, 0);

        var requisitionColumn = FindColumn(serviceColumns, "Requisition No.") ?? FindColumn(serviceColumns, "Requisition No");
        var requisitionNo = GetValue(submittedValues, requisitionColumn);
        if (string.IsNullOrWhiteSpace(requisitionNo))
            return (false, "Select an approved requisition before creating the job.", null, null, 0);

        var requisitionSheet = await _db.ProjectSheets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == serviceSheet.ProjectId.Value && x.IsActive && x.Name == "Requisition");
        if (requisitionSheet == null)
            return (false, "The Requisition sheet was not found for this project.", null, requisitionNo, 0);

        var reqColumns = await _db.SheetColumns.AsNoTracking()
            .Where(x => x.ProjectSheetId == requisitionSheet.Id).ToListAsync();
        var reqNoColumn = FindColumn(reqColumns, "Requisition No");
        var reqStatusColumn = FindColumn(reqColumns, "Status");
        var reqDepartmentColumn = FindColumn(reqColumns, "Request Department");
        var reqItemColumn = FindColumn(reqColumns, "Item No");
        var reqDescriptionColumn = FindColumn(reqColumns, "Description");
        var reqUnitColumn = FindColumn(reqColumns, "Unit");
        var reqQtyColumn = FindColumn(reqColumns, "Qty");
        if (reqNoColumn == null || reqStatusColumn == null || reqDepartmentColumn == null)
            return (false, "The requisition sheet is missing required workflow columns.", null, requisitionNo, 0);

        var upper = requisitionNo.ToUpperInvariant();
        var reqRows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == requisitionSheet.Id
                        && x.Cells.Any(c => c.SheetColumnId == reqNoColumn.Id && c.Value != null && c.Value.ToUpper() == upper))
            .ToListAsync();
        if (reqRows.Count == 0)
            return (false, $"Requisition {requisitionNo} has no line items.", null, requisitionNo, 0);
        if (reqRows.Any(x => !string.Equals(GetCellValue(x, reqStatusColumn), "Approved", StringComparison.OrdinalIgnoreCase)))
            return (false, $"Requisition {requisitionNo} must be approved before Job Creation.", null, requisitionNo, reqRows.Count);

        var departmentCode = GetCellValue(reqRows[0], reqDepartmentColumn);
        if (string.IsNullOrWhiteSpace(departmentCode))
            return (false, $"Requisition {requisitionNo} does not have a request department.", null, requisitionNo, reqRows.Count);

        var jobNumber = await _jobNumbers.NextAsync(departmentCode);
        var jobNumberColumn = FindColumn(serviceColumns, "Job Number");
        var serviceDepartmentColumn = FindColumn(serviceColumns, "Request Department");
        var serviceItemColumn = FindColumn(serviceColumns, "Item No");
        var serviceDescriptionColumn = FindColumn(serviceColumns, "Service Description");
        var serviceUnitColumn = FindColumn(serviceColumns, "Unit");
        var serviceQtyColumn = FindColumn(serviceColumns, "Qty");
        var serviceInvoiceColumn = FindColumn(serviceColumns, "Invoice Number");
        var serviceInvoiceAmountColumn = FindColumn(serviceColumns, "Invoice Amount");
        var serviceInvoiceCurrencyColumn = FindColumn(serviceColumns, "Invoice Currency");
        var serviceExchangeColumn = FindColumn(serviceColumns, "Exchange to USD");

        var orderedRows = reqRows
            .OrderBy(x => int.TryParse(GetCellValue(x, reqItemColumn), out var itemNo) ? itemNo : int.MaxValue)
            .ThenBy(x => x.Id)
            .ToList();

        for (var index = 0; index < orderedRows.Count; index++)
        {
            var reqRow = orderedRows[index];
            var rowValues = new Dictionary<int, string?>(submittedValues);
            void Set(SheetColumn? column, string? value)
            {
                if (column != null) rowValues[column.Id] = value;
            }

            Set(jobNumberColumn, jobNumber);
            Set(requisitionColumn, requisitionNo);
            Set(serviceDepartmentColumn, departmentCode);
            Set(serviceItemColumn, GetCellValue(reqRow, reqItemColumn));
            Set(serviceDescriptionColumn, GetCellValue(reqRow, reqDescriptionColumn));
            Set(serviceUnitColumn, GetCellValue(reqRow, reqUnitColumn));
            Set(serviceQtyColumn, GetCellValue(reqRow, reqQtyColumn));
            if (index > 0)
            {
                Set(serviceInvoiceColumn, null);
                Set(serviceInvoiceAmountColumn, null);
                Set(serviceInvoiceCurrencyColumn, null);
                Set(serviceExchangeColumn, null);
            }

            var newRow = new SheetRow
            {
                ProjectSheetId = serviceSheet.Id,
                UniqueId = await _ids.NextAsync("ROW")
            };
            foreach (var column in serviceColumns)
            {
                var value = rowValues.GetValueOrDefault(column.Id)?.Trim();
                if (column.ColumnType == SheetColumnType.Checkbox && string.IsNullOrWhiteSpace(value)) value = "false";
                if (!string.IsNullOrWhiteSpace(value))
                    newRow.Cells.Add(new SheetCell { SheetColumnId = column.Id, Value = value });
            }
            _db.SheetRows.Add(newRow);
        }

        await _db.SaveChangesAsync();
        await SetRequisitionStatusAsync(serviceSheet.ProjectId.Value, requisitionNo, "Converted to Job");
        await _db.SaveChangesAsync();
        return (true, null, jobNumber, requisitionNo, orderedRows.Count);
    }

    private static bool IsSystemManagedColumn(SheetColumn column)
        => !string.IsNullOrWhiteSpace(column.Options) &&
           (column.Options.StartsWith("__AUTO_", StringComparison.OrdinalIgnoreCase) || column.Options.StartsWith("__COMPUTED_", StringComparison.OrdinalIgnoreCase));

    private static void ApplySheetDefaults(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
        void SetDefault(string columnName, string defaultValue)
        {
            var column = FindColumn(columns, columnName);
            if (column != null && string.IsNullOrWhiteSpace(values.GetValueOrDefault(column.Id))) values[column.Id] = defaultValue;
        }

        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)) SetDefault("Status", "Draft");
        if (string.Equals(sheet.Name, "Purchase Order", StringComparison.OrdinalIgnoreCase)) SetDefault("Status", "Draft");
        if (string.Equals(sheet.Name, "Service Logistics", StringComparison.OrdinalIgnoreCase)) SetDefault("Follow-up Status", "Pending");
        if (string.Equals(sheet.Name, "Invoice Reception", StringComparison.OrdinalIgnoreCase)) SetDefault("Payment Status", "Pending");

        if (string.Equals(sheet.Name, "Import and Export", StringComparison.OrdinalIgnoreCase))
        {
            var status = FindColumn(columns, "Status");
            if (status != null && string.IsNullOrWhiteSpace(values.GetValueOrDefault(status.Id))) values[status.Id] = "In Progress";
        }

        if (string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase))
        {
            var localAbroad = FindColumn(columns, "Local/Abroad");
            var selection = localAbroad == null ? null : values.GetValueOrDefault(localAbroad.Id);
            if (string.Equals(selection, "Local", StringComparison.OrdinalIgnoreCase))
            {
                var export = FindColumn(columns, "Export Job No");
                var import = FindColumn(columns, "Import Job No");
                if (export != null) values[export.Id] = null;
                if (import != null) values[import.Id] = null;
            }
        }
        if (string.Equals(sheet.Name, "Service Logistics", StringComparison.OrdinalIgnoreCase))
        {
            var mode = FindColumn(columns, "Mode");
            var selection = mode == null ? null : values.GetValueOrDefault(mode.Id);
            if (string.Equals(selection, "Local", StringComparison.OrdinalIgnoreCase))
            {
                var export = FindColumn(columns, "Export Job No");
                var import = FindColumn(columns, "Import Job No");
                if (export != null) values[export.Id] = null;
                if (import != null) values[import.Id] = null;
            }
        }
    }

    private async Task ApplyGeneratedValuesForNewRowAsync(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase))
        {
            var requisitionNumber = FindColumn(columns, "Requisition No");
            var itemNumber = FindColumn(columns, "Item No");
            var department = FindColumn(columns, "Request Department");
            var departmentValue = department == null ? null : values.GetValueOrDefault(department.Id)?.Trim();

            if (requisitionNumber != null && string.IsNullOrWhiteSpace(values.GetValueOrDefault(requisitionNumber.Id)) && !string.IsNullOrWhiteSpace(departmentValue))
                values[requisitionNumber.Id] = await NextRequisitionNumberAsync(sheet, columns, departmentValue);

            var requisitionValue = requisitionNumber == null ? null : values.GetValueOrDefault(requisitionNumber.Id);
            if (itemNumber != null && string.IsNullOrWhiteSpace(values.GetValueOrDefault(itemNumber.Id)) && !string.IsNullOrWhiteSpace(requisitionValue))
                values[itemNumber.Id] = (await NextRequisitionItemNumberAsync(sheet, columns, requisitionValue)).ToString("000", CultureInfo.InvariantCulture);
        }

        foreach (var column in columns.Where(IsSystemManagedColumn))
        {
            if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault(column.Id))) continue;
            values[column.Id] = column.Options switch
            {
                "__AUTO_REQUISITION_NUMBER__" => values.GetValueOrDefault(column.Id),
                "__AUTO_REQUISITION_ITEM_NO__" => values.GetValueOrDefault(column.Id),
                "__AUTO_REQUISITION_STATUS__" => "Draft",
                "__AUTO_REQUISITION_APPROVAL_NUMBER__" => await _ids.NextAsync("RAP"),
                "__AUTO_PO_NUMBER__" => await _ids.NextAsync("PO"),
                "__AUTO_CURRENT_USER_EMAIL__" => User.Identity?.Name ?? string.Empty,
                "__AUTO_TODAY__" => DateTime.Today.ToString("yyyy-MM-dd"),
                "__AUTO_APPROVAL_STATUS__" => "Pending",
                _ => values.GetValueOrDefault(column.Id)
            };
        }

        if (string.Equals(sheet.Name, "Import and Export", StringComparison.OrdinalIgnoreCase))
        {
            var jobNumber = FindColumn(columns, "Job Number");
            var shipmentType = FindColumn(columns, "Shipment type (Import or Export)");
            var typeValue = shipmentType == null ? null : values.GetValueOrDefault(shipmentType.Id);
            if (jobNumber != null && string.IsNullOrWhiteSpace(values.GetValueOrDefault(jobNumber.Id)) && !string.IsNullOrWhiteSpace(typeValue))
                values[jobNumber.Id] = await NextShipmentJobNumberAsync(sheet, typeValue);
        }
    }

    private async Task ApplyGeneratedValuesForExistingRowIfMissingAsync(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
        if (!columns.Any(x => IsSystemManagedColumn(x) && string.IsNullOrWhiteSpace(values.GetValueOrDefault(x.Id)))) return;
        await ApplyGeneratedValuesForNewRowAsync(sheet, columns, values);
    }

    private async Task<string> NextRequisitionNumberAsync(ProjectSheet sheet, List<SheetColumn> columns, string departmentCode)
    {
        if (!sheet.ProjectId.HasValue) return await _ids.NextAsync("REQ");

        var requisitionColumn = FindColumn(columns, "Requisition No");
        var normalizedDepartment = departmentCode.Trim().ToUpperInvariant();
        var department = await _db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Code.ToUpper() == normalizedDepartment);
        if (department == null || requisitionColumn == null) return await _ids.NextAsync("REQ");

        var projectCode = sheet.Project?.Code;
        if (string.IsNullOrWhiteSpace(projectCode))
            projectCode = await _db.Projects.AsNoTracking().Where(x => x.Id == sheet.ProjectId.Value).Select(x => x.Code).FirstOrDefaultAsync();
        projectCode = NormalizeBusinessCode(projectCode, "PRJ");
        var deptCode = NormalizeBusinessCode(department.Code, "DEP");
        var visiblePrefix = $"{projectCode}-{deptCode}-";
        var sequenceScope = $"R{sheet.ProjectId.Value:X8}{department.Id:X8}";

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var existingNumbers = await (from cell in _db.SheetCells.AsNoTracking()
                                     join row in _db.SheetRows.AsNoTracking() on cell.SheetRowId equals row.Id
                                     where row.ProjectSheetId == sheet.Id
                                           && cell.SheetColumnId == requisitionColumn.Id
                                           && cell.Value != null
                                           && cell.Value.StartsWith(visiblePrefix)
                                     select cell.Value!).ToListAsync();

        var sequence = await _db.BusinessSequences.SingleOrDefaultAsync(x => x.Prefix == sequenceScope && x.Year == 0);
        if (sequence == null)
        {
            var max = existingNumbers
                .Select(x => x.StartsWith(visiblePrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(x[visiblePrefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
            sequence = new BusinessSequence { Prefix = sequenceScope, Year = 0, LastNumber = max + 1 };
            _db.BusinessSequences.Add(sequence);
        }
        else
        {
            sequence.LastNumber++;
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return $"{visiblePrefix}{sequence.LastNumber:000}";
    }

    private async Task<int> NextRequisitionItemNumberAsync(ProjectSheet sheet, List<SheetColumn> columns, string requisitionNo)
    {
        var requisitionColumn = FindColumn(columns, "Requisition No");
        var itemColumn = FindColumn(columns, "Item No");
        if (requisitionColumn == null || itemColumn == null) return 1;

        var upper = requisitionNo.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sheet.Id}|{upper}")));
        var scope = $"I{hash[..19]}";

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == sheet.Id && x.Cells.Any(c => c.SheetColumnId == requisitionColumn.Id && c.Value != null && c.Value.ToUpper() == upper))
            .ToListAsync();
        var maxItem = rows.Select(x => GetCellValue(x, itemColumn))
            .Select(x => int.TryParse(x, out var n) ? n : 0).DefaultIfEmpty(0).Max();

        var sequence = await _db.BusinessSequences.SingleOrDefaultAsync(x => x.Prefix == scope && x.Year == 0);
        int next;
        if (sequence == null)
        {
            next = maxItem + 1;
            sequence = new BusinessSequence { Prefix = scope, Year = 0, LastNumber = next };
            _db.BusinessSequences.Add(sequence);
        }
        else
        {
            next = Math.Max(sequence.LastNumber, maxItem) + 1;
            sequence.LastNumber = next;
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return next;
    }

    private static string NormalizeBusinessCode(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private async Task<string> NextShipmentJobNumberAsync(ProjectSheet sheet, string shipmentType)
    {
        var projectCode = sheet.Project?.Code;
        if (string.IsNullOrWhiteSpace(projectCode) && sheet.ProjectId.HasValue)
            projectCode = await _db.Projects.Where(x => x.Id == sheet.ProjectId.Value).Select(x => x.Code).FirstOrDefaultAsync();
        projectCode = string.IsNullOrWhiteSpace(projectCode) ? "PRJ" : projectCode.Trim().ToUpperInvariant();
        var kind = shipmentType.StartsWith("E", StringComparison.OrdinalIgnoreCase) ? "EXP" : "IMP";
        var visiblePrefix = $"{projectCode}-{kind}-";
        var scope = $"S:{projectCode}:{kind}";
        if (scope.Length > 20) scope = scope[..20];

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var sequence = await _db.BusinessSequences.SingleOrDefaultAsync(x => x.Prefix == scope && x.Year == 0);
        if (sequence == null)
        {
            var values = await (from cell in _db.SheetCells
                                join column in _db.SheetColumns on cell.SheetColumnId equals column.Id
                                join projectSheet in _db.ProjectSheets on column.ProjectSheetId equals projectSheet.Id
                                where projectSheet.ProjectId == sheet.ProjectId && projectSheet.Name == "Import and Export" && column.Name == "Job Number" && cell.Value != null && cell.Value.StartsWith(visiblePrefix)
                                select cell.Value!).ToListAsync();
            var max = values.Select(x => x.StartsWith(visiblePrefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(x[visiblePrefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
            sequence = new BusinessSequence { Prefix = scope, Year = 0, LastNumber = max + 1 };
            _db.BusinessSequences.Add(sequence);
        }
        else sequence.LastNumber++;

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return $"{visiblePrefix}{sequence.LastNumber:0000}";
    }

    private async Task ApplyContractSummaryComputedValuesAsync(ProjectSheet sheet, List<SheetColumn> columns, List<SheetRow> rows)
    {
        if (!sheet.ProjectId.HasValue || rows.Count == 0) return;
        var contractNoColumn = FindColumn(columns, "Contract No");
        var amountColumn = FindColumn(columns, "Contract amount");
        var paidColumn = FindColumn(columns, "Total Amount Paid till date");
        var balanceColumn = FindColumn(columns, "Contract Balance Amount");
        if (contractNoColumn == null || paidColumn == null || balanceColumn == null) return;

        var financialSheetId = await _db.ProjectSheets.AsNoTracking()
            .Where(x => x.ProjectId == sheet.ProjectId && x.IsActive && (x.Name == "Financial" || x.Name == "Finacial"))
            .Select(x => (int?)x.Id).FirstOrDefaultAsync();

        var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (financialSheetId.HasValue)
        {
            var financialColumns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == financialSheetId).ToListAsync();
            var financialContract = financialColumns.FirstOrDefault(x => NormalizeName(x.Name) == NormalizeName("Contract NO."));
            var paymentAmount = financialColumns.FirstOrDefault(x => NormalizeName(x.Name) == NormalizeName("Payment Amount"));
            if (financialContract != null && paymentAmount != null)
            {
                var financialRows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == financialSheetId).ToListAsync();
                foreach (var fRow in financialRows)
                {
                    var contract = fRow.Cells.FirstOrDefault(x => x.SheetColumnId == financialContract.Id)?.Value?.Trim();
                    var paymentText = fRow.Cells.FirstOrDefault(x => x.SheetColumnId == paymentAmount.Id)?.Value;
                    if (string.IsNullOrWhiteSpace(contract) || !TryDecimal(paymentText, out var payment)) continue;
                    payments[contract] = payments.GetValueOrDefault(contract) + payment;
                }
            }
        }

        foreach (var row in rows)
        {
            var contract = row.Cells.FirstOrDefault(x => x.SheetColumnId == contractNoColumn.Id)?.Value?.Trim();
            var paid = !string.IsNullOrWhiteSpace(contract) ? payments.GetValueOrDefault(contract) : 0m;
            var amountText = amountColumn == null ? null : row.Cells.FirstOrDefault(x => x.SheetColumnId == amountColumn.Id)?.Value;
            var amount = TryDecimal(amountText, out var parsedAmount) ? parsedAmount : 0m;
            SetVirtualCell(row, paidColumn.Id, paid.ToString("0.00", CultureInfo.InvariantCulture));
            SetVirtualCell(row, balanceColumn.Id, (amount - paid).ToString("0.00", CultureInfo.InvariantCulture));
        }
    }

    private async Task ValidateRowBusinessRulesAsync(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values, long? currentRowId)
    {
        await ValidateDuplicateInvoicesAsync(columns, values, currentRowId);
        if (!sheet.ProjectId.HasValue) return;

        string? Value(string name)
        {
            var column = FindColumn(columns, name);
            return column == null ? null : values.GetValueOrDefault(column.Id)?.Trim();
        }

        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase))
        {
            var departmentCode = Value("Request Department");
            if (string.IsNullOrWhiteSpace(departmentCode) || !await _db.Departments.AsNoTracking().AnyAsync(x => x.IsActive && x.Code.ToUpper() == departmentCode.ToUpper()))
                ModelState.AddModelError(string.Empty, "Select a valid request department before adding the requisition item.");

            var req = Value("Requisition No");
            var reqColumn = FindColumn(columns, "Requisition No");
            var statusColumn = FindColumn(columns, "Status");
            var departmentColumn = FindColumn(columns, "Request Department");
            if (reqColumn != null && statusColumn != null && departmentColumn != null)
            {
                var allRows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
                    .Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();

                if (string.IsNullOrWhiteSpace(req))
                {
                    var openForDepartment = allRows
                        .Where(x => string.Equals(GetCellValue(x, departmentColumn), departmentCode, StringComparison.OrdinalIgnoreCase))
                        .Where(x => { var st = GetCellValue(x, statusColumn); return string.IsNullOrWhiteSpace(st) || string.Equals(st, "Pending", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "Draft", StringComparison.OrdinalIgnoreCase); })
                        .Select(x => GetCellValue(x, reqColumn))
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                    if (!string.IsNullOrWhiteSpace(openForDepartment))
                        ModelState.AddModelError(string.Empty, $"Department {departmentCode} already has open requisition {openForDepartment}. Use Add Item on that requisition, or complete it before starting the next requisition.");
                }
                else
                {
                    var existing = allRows.FirstOrDefault(x => string.Equals(GetCellValue(x, reqColumn), req, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                        ModelState.AddModelError(string.Empty, $"Requisition {req} was not found. Use Add Req to create a new requisition.");
                    else
                    {
                        var existingStatus = GetCellValue(existing, statusColumn);
                        var existingDepartment = GetCellValue(existing, departmentColumn);
                        if (!string.Equals(existingStatus, "Pending", StringComparison.OrdinalIgnoreCase) && !string.Equals(existingStatus, "Draft", StringComparison.OrdinalIgnoreCase))
                            ModelState.AddModelError(string.Empty, $"Requisition {req} is already {existingStatus ?? "closed"} and cannot accept more items.");
                        if (!string.Equals(existingDepartment, departmentCode, StringComparison.OrdinalIgnoreCase))
                            ModelState.AddModelError(string.Empty, $"Requisition {req} belongs to department {existingDepartment}; its department cannot be changed.");
                    }
                }
            }
        }
        else if (string.Equals(sheet.Name, "Requisition Approval", StringComparison.OrdinalIgnoreCase))
        {
            var req = Value("Requisition No");
            if (!string.IsNullOrWhiteSpace(req) && !await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Requisition", "Requisition No", req))
                ModelState.AddModelError(string.Empty, $"Requisition {req} was not found in the Requisition sheet.");
            else if (!string.IsNullOrWhiteSpace(req) && !await RequisitionReadyForApprovalAsync(sheet.ProjectId.Value, req))
                ModelState.AddModelError(string.Empty, $"Complete requisition {req} before submitting it for Manager Approval.");

            var approver = Value("Approver / Manager");
            if (!string.IsNullOrWhiteSpace(approver))
            {
                var eligible = await GetApprovalManagersAsync();
                if (!eligible.Any(x => string.Equals(x.Email, approver, StringComparison.OrdinalIgnoreCase) || string.Equals(x.UserName, approver, StringComparison.OrdinalIgnoreCase)))
                    ModelState.AddModelError(string.Empty, "Select a valid Project Manager, Department Head or Administrator for approval.");
            }
        }
        else if (string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase))
        {
            var req = Value("Requisition No.") ?? Value("Requisition No");
            var approval = Value("Service Approval No");
            if (string.IsNullOrWhiteSpace(req) || !await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Requisition", "Requisition No", req))
            {
                ModelState.AddModelError(string.Empty, "Select a valid approved requisition before creating the job.");
            }
            else
            {
                var reqStatus = await SheetReferencedValueAsync(sheet.ProjectId.Value, "Requisition", "Requisition No", req, "Status");
                if (!string.Equals(reqStatus, "Approved", StringComparison.OrdinalIgnoreCase))
                    ModelState.AddModelError(string.Empty, $"Requisition {req} must be Approved before job creation.");

                if (await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Service Business", "Requisition No.", req))
                    ModelState.AddModelError(string.Empty, $"A service job has already been created from requisition {req}.");
            }

            if (string.IsNullOrWhiteSpace(approval))
                ModelState.AddModelError(string.Empty, "A final approved Service Approval is required before a job can be created.");
            else
            {
                var serviceApproval = await _db.ServiceApprovals.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == sheet.ProjectId.Value && x.ApprovalNumber == approval);
                if (serviceApproval == null || serviceApproval.Status != ServiceApprovalStatus.Approved)
                    ModelState.AddModelError(string.Empty, $"Service Approval {approval} must be finally Approved before job creation.");
                else if (!string.Equals(serviceApproval.RequisitionNumber.Trim(), req?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ModelState.AddModelError(string.Empty, $"Service Approval {approval} does not belong to requisition {req}.");
            }
        }
        else if (string.Equals(sheet.Name, "Purchase Order", StringComparison.OrdinalIgnoreCase))
        {
            var job = Value("Job Number");
            if (string.IsNullOrWhiteSpace(job) || !await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Service Business", "Job Number", job))
                ModelState.AddModelError(string.Empty, "Select/use a valid Service Business Job Number before creating a PO.");
        }
        else if (string.Equals(sheet.Name, "Service Logistics", StringComparison.OrdinalIgnoreCase))
        {
            var job = Value("Job Number");
            if (string.IsNullOrWhiteSpace(job) || !await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Service Business", "Job Number", job))
                ModelState.AddModelError(string.Empty, "A valid Service Business Job Number is required.");
            else if (!await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Purchase Order", "Job Number", job))
                ModelState.AddModelError(string.Empty, $"Create the Purchase Order for job {job} before delivery/export follow-up.");
        }
        else if (string.Equals(sheet.Name, "Invoice Reception", StringComparison.OrdinalIgnoreCase))
        {
            var job = Value("Job Number");
            if (string.IsNullOrWhiteSpace(job) || !await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Service Business", "Job Number", job))
                ModelState.AddModelError(string.Empty, "A valid Service Business Job Number is required.");
            else
            {
                var receivedDate = await SheetReferencedValueAsync(sheet.ProjectId.Value, "Service Logistics", "Job Number", job, "Received Back Date");
                var followStatus = await SheetReferencedValueAsync(sheet.ProjectId.Value, "Service Logistics", "Job Number", job, "Follow-up Status");
                if (string.IsNullOrWhiteSpace(receivedDate) && !string.Equals(followStatus, "Received Back", StringComparison.OrdinalIgnoreCase) && !string.Equals(followStatus, "Closed", StringComparison.OrdinalIgnoreCase))
                    ModelState.AddModelError(string.Empty, $"Equipment/service for job {job} must be received back before invoice reception.");
            }
        }
    }

    private async Task ValidateDuplicateInvoicesAsync(List<SheetColumn> columns, Dictionary<int, string?> values, long? currentRowId)
    {
        var invoiceColumns = columns.Where(x => IsInvoiceColumnName(x.Name)).ToList();
        if (invoiceColumns.Count == 0) return;
        var allInvoiceColumns = (await _db.SheetColumns.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync())
            .Where(x => IsInvoiceColumnName(x.Name)).Select(x => x.Id).ToList();

        var seenInSubmittedRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in invoiceColumns)
        {
            var invoice = values.GetValueOrDefault(column.Id)?.Trim();
            if (string.IsNullOrWhiteSpace(invoice)) continue;
            if (!seenInSubmittedRow.Add(invoice))
            {
                ModelState.AddModelError(string.Empty, $"Invoice number {invoice} is repeated in this record. Duplicate invoices are not allowed anywhere in BGP.");
                continue;
            }

            var upper = invoice.ToUpper();
            var inJobs = await _db.Jobs.IgnoreQueryFilters().AnyAsync(x => !x.IsDeleted && x.InvoiceNumber != null && x.InvoiceNumber.ToUpper() == upper);
            var inSheets = await _db.SheetCells.AsNoTracking().AnyAsync(x => allInvoiceColumns.Contains(x.SheetColumnId) && (!currentRowId.HasValue || x.SheetRowId != currentRowId.Value) && x.Value != null && x.Value.ToUpper() == upper);
            if (inJobs || inSheets)
                ModelState.AddModelError(string.Empty, $"Invoice number {invoice} already exists. Duplicate invoices are not allowed anywhere in BGP.");
        }
    }

    private static bool IsInvoiceColumnName(string name)
        => NormalizeName(name).Contains("INVOICE") && (NormalizeName(name).EndsWith("NO") || NormalizeName(name).Contains("NUMBER"));

    private async Task<bool> SheetReferenceExistsAsync(int projectId, string sheetName, string columnName, string value)
        => (await SheetReferencedValueAsync(projectId, sheetName, columnName, value, columnName)) != null;

    private async Task<string?> SheetReferencedValueAsync(int projectId, string sheetName, string referenceColumnName, string referenceValue, string targetColumnName)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == sheetName);
        if (sheet == null) return null;
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var referenceColumn = FindColumn(columns, referenceColumnName);
        var targetColumn = FindColumn(columns, targetColumnName);
        if (referenceColumn == null || targetColumn == null) return null;
        var upper = referenceValue.Trim().ToUpper();
        var rowId = await _db.SheetCells.AsNoTracking()
            .Where(x => x.SheetColumnId == referenceColumn.Id && x.Value != null && x.Value.ToUpper() == upper)
            .OrderByDescending(x => x.SheetRowId)
            .Select(x => (long?)x.SheetRowId).FirstOrDefaultAsync();
        if (!rowId.HasValue) return null;
        if (referenceColumn.Id == targetColumn.Id) return referenceValue;
        return await _db.SheetCells.AsNoTracking().Where(x => x.SheetRowId == rowId.Value && x.SheetColumnId == targetColumn.Id).Select(x => x.Value).FirstOrDefaultAsync();
    }

    private async Task<Dictionary<string, List<string>>> BuildReferenceOptionsAsync(ProjectSheet sheet)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!sheet.ProjectId.HasValue) return result;

        result["__REQUISITIONS__"] = string.Equals(sheet.Name, "Requisition Approval", StringComparison.OrdinalIgnoreCase)
            ? await GetRequisitionNumbersByStatusAsync(sheet.ProjectId.Value, "Submitted")
            : string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase)
                ? await GetRequisitionNumbersByStatusAsync(sheet.ProjectId.Value, "Approved", "Converted to Job")
                : await GetAllRequisitionNumbersAsync(sheet.ProjectId.Value);
        result["__SERVICE_JOBS__"] = await GetSheetColumnValuesAsync(sheet.ProjectId.Value, "Service Business", "Job Number");
        result["__APPROVED_SERVICE_APPROVALS__"] = await GetApprovedServiceApprovalNumbersAsync(sheet.ProjectId.Value);
        return result;
    }

    private async Task<List<string>> GetSheetColumnValuesAsync(int projectId, string sheetName, string columnName)
    {
        var sheetId = await _db.ProjectSheets.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.IsActive && x.Name == sheetName)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (!sheetId.HasValue) return new List<string>();
        var columnId = await _db.SheetColumns.AsNoTracking()
            .Where(x => x.ProjectSheetId == sheetId.Value && x.Name == columnName)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (!columnId.HasValue) return new List<string>();
        return await _db.SheetCells.AsNoTracking()
            .Where(x => x.SheetColumnId == columnId.Value && x.Value != null && x.Value != "")
            .Select(x => x.Value!).Distinct().OrderByDescending(x => x).ToListAsync();
    }

    private async Task<List<string>> GetRequisitionNumbersByStatusAsync(int projectId, params string[] statuses)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return new List<string>();
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var numberColumn = FindColumn(columns, "Requisition No");
        var statusColumn = FindColumn(columns, "Status");
        if (numberColumn == null || statusColumn == null) return new List<string>();
        var allowed = new HashSet<string>(statuses, StringComparer.OrdinalIgnoreCase);
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        return rows
            .Where(x => allowed.Contains(GetCellValue(x, statusColumn) ?? string.Empty))
            .Select(x => GetCellValue(x, numberColumn))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x)
            .ToList();
    }

    private async Task<List<string>> GetAllRequisitionNumbersAsync(int projectId)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return new List<string>();
        var numberColumn = await _db.SheetColumns.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectSheetId == sheet.Id && x.Name == "Requisition No");
        if (numberColumn == null) return new List<string>();
        return await _db.SheetCells.AsNoTracking()
            .Where(x => x.SheetColumnId == numberColumn.Id && x.Value != null && x.Value != "")
            .Select(x => x.Value!).Distinct().OrderByDescending(x => x).ToListAsync();
    }

    private async Task<List<string>> GetCompletedRequisitionNumbersAsync(int projectId)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return new List<string>();
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var numberColumn = FindColumn(columns, "Requisition No");
        var statusColumn = FindColumn(columns, "Status");
        if (numberColumn == null || statusColumn == null) return new List<string>();
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        return rows
            .Where(x => IsCompletedRequisitionStatus(GetCellValue(x, statusColumn)))
            .Select(x => GetCellValue(x, numberColumn))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x)
            .ToList();
    }

    private async Task<bool> RequisitionReadyForApprovalAsync(int projectId, string requisitionNo)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return false;
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var numberColumn = FindColumn(columns, "Requisition No");
        var statusColumn = FindColumn(columns, "Status");
        if (numberColumn == null || statusColumn == null) return false;
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var matching = rows.Where(x => string.Equals(GetCellValue(x, numberColumn), requisitionNo, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count > 0 && matching.All(x => IsCompletedRequisitionStatus(GetCellValue(x, statusColumn)));
    }

    private static bool IsCompletedRequisitionStatus(string? status)
        => string.Equals(status, "Waiting Approval", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "Submitted", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "Converted to Job", StringComparison.OrdinalIgnoreCase);

    private async Task<List<SheetColumn>> EnsureRequisitionApprovalColumnsAsync(int sheetId)
    {
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheetId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var nextOrder = columns.Count == 0 ? 1 : columns.Max(x => x.SortOrder) + 1;
        var required = new[]
        {
            (Name: "Approver / Manager", Type: SheetColumnType.Email, Options: "__COMPUTED_REQUISITION_APPROVER__"),
            (Name: "Approval Comment", Type: SheetColumnType.Text, Options: "__COMPUTED_REQUISITION_APPROVAL_COMMENT__"),
            (Name: "Decision By", Type: SheetColumnType.Email, Options: "__COMPUTED_REQUISITION_DECISION_BY__"),
            (Name: "Decision Date", Type: SheetColumnType.Date, Options: "__COMPUTED_REQUISITION_DECISION_DATE__")
        };
        var changed = false;
        foreach (var definition in required)
        {
            if (columns.Any(x => x.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var column = new SheetColumn
            {
                ProjectSheetId = sheetId, Name = definition.Name, ColumnType = definition.Type,
                IsRequired = false, Options = definition.Options, SortOrder = nextOrder++
            };
            _db.SheetColumns.Add(column);
            columns.Add(column);
            changed = true;
        }
        if (changed) await _db.SaveChangesAsync();
        return columns.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList();
    }

    private async Task SetRequisitionStatusAsync(int projectId, string requisitionNo, string status)
    {
        var sheet = await _db.ProjectSheets.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return;
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var numberColumn = FindColumn(columns, "Requisition No");
        var statusColumn = FindColumn(columns, "Status");
        if (numberColumn == null || statusColumn == null) return;
        var rows = await _db.SheetRows.Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        foreach (var row in rows.Where(x => string.Equals(GetCellValue(x, numberColumn), requisitionNo, StringComparison.OrdinalIgnoreCase)))
        {
            SetCellValue(row, statusColumn, status);
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task<List<string>> GetApprovedServiceApprovalNumbersAsync(int projectId)
    {
        return await _db.ServiceApprovals.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.Status == ServiceApprovalStatus.Approved && x.ApprovalNumber != null)
            .OrderByDescending(x => x.FinalApprovedAt ?? x.UpdatedAt)
            .Select(x => x.ApprovalNumber!)
            .ToListAsync();
    }

    private async Task<List<ApplicationUser>> GetApprovalManagersAsync()
    {
        var managers = new Dictionary<string, ApplicationUser>();
        foreach (var role in new[] { "Project Manager", "Department Head", "Administrator", "Super Admin" })
        {
            foreach (var user in await _userManager.GetUsersInRoleAsync(role))
            {
                if (user.IsActive) managers[user.Id] = user;
            }
        }
        return managers.Values
            .OrderBy(x => string.IsNullOrWhiteSpace(x.FullName) ? x.Email ?? x.UserName : x.FullName)
            .ThenBy(x => x.Email)
            .ToList();
    }

    private async Task NotifyServiceApprovalSubmittedAsync(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values, long rowId)
    {
        var approvalNo = GetValue(values, FindColumn(columns, "Requisition Approval No")) ?? "Requisition approval";
        var req = GetValue(values, FindColumn(columns, "Requisition No"));
        var requester = GetValue(values, FindColumn(columns, "Requested By")) ?? User.Identity?.Name;
        var selectedManagerIdentity = GetValue(values, FindColumn(columns, "Approver / Manager"));
        var recipients = new Dictionary<string, ApplicationUser>();

        if (!string.IsNullOrWhiteSpace(selectedManagerIdentity))
        {
            var selectedManager = await _userManager.FindByEmailAsync(selectedManagerIdentity) ?? await _userManager.FindByNameAsync(selectedManagerIdentity);
            if (selectedManager != null) recipients[selectedManager.Id] = selectedManager;
        }
        else if (sheet.ProjectId.HasValue)
        {
            var managerIdentity = await _db.ProjectWorkspaces.AsNoTracking().Where(x => x.ProjectId == sheet.ProjectId.Value).Select(x => x.ProjectManager).FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(managerIdentity))
            {
                var assignedManager = await _userManager.FindByEmailAsync(managerIdentity) ?? await _userManager.FindByNameAsync(managerIdentity);
                if (assignedManager != null) recipients[assignedManager.Id] = assignedManager;
            }
        }

        // Administrators receive oversight notifications, while the selected manager
        // remains the person responsible for the approval decision.
        foreach (var role in new[] { "Administrator", "Super Admin" })
            foreach (var user in await _userManager.GetUsersInRoleAsync(role)) recipients[user.Id] = user;

        var url = Url.Action(nameof(Details), "Sheets", new { id = sheet.Id, q = approvalNo }) ?? $"/Sheets/Details/{sheet.Id}";
        foreach (var user in recipients.Values)
            await CreateNotificationAsync(user.Id, "Requisition approval decision required", $"Review {approvalNo} linked to requisition {req ?? "N/A"}, submitted by {requester ?? "a user"}.", url);

        var currentId = _userManager.GetUserId(User);
        if (!string.IsNullOrWhiteSpace(currentId))
            await CreateNotificationAsync(currentId, "Requisition sent for approval", $"{approvalNo} was submitted to the project manager for review. You will be notified after the decision.", url);
    }

    private async Task CreateNotificationAsync(string userId, string title, string message, string? url)
    {
        _db.WorkflowNotifications.Add(new WorkflowNotification { UserId = userId, Title = title, Message = message, Url = url, IsRead = false, CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    private static string? GetValue(Dictionary<int, string?> values, SheetColumn? column)
        => column == null ? null : values.GetValueOrDefault(column.Id)?.Trim();

    private static string? GetCellValue(SheetRow row, SheetColumn? column)
        => column == null ? null : row.Cells.FirstOrDefault(x => x.SheetColumnId == column.Id)?.Value?.Trim();

    private void SetCellValue(SheetRow row, SheetColumn? column, string? value)
    {
        if (column == null) return;
        var cell = row.Cells.FirstOrDefault(x => x.SheetColumnId == column.Id);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (cell != null) _db.SheetCells.Remove(cell);
            return;
        }
        if (cell == null) row.Cells.Add(new SheetCell { SheetRowId = row.Id, SheetColumnId = column.Id, Value = value.Trim() });
        else cell.Value = value.Trim();
    }

    private IActionResult RedirectAfterColumnChange(int sheetId, string? source)
        => string.Equals(source, "columns", StringComparison.OrdinalIgnoreCase)
            ? RedirectToAction(nameof(Columns), new { sheetId })
            : RedirectToAction(nameof(Details), new { id = sheetId });

    private static SheetColumn? FindColumn(IEnumerable<SheetColumn> columns, string name)
        => columns.FirstOrDefault(x => NormalizeName(x.Name) == NormalizeName(name));

    private static string NormalizeName(string value)
        => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static bool TryDecimal(string? value, out decimal number)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return true;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static void SetVirtualCell(SheetRow row, int columnId, string value)
    {
        var cell = row.Cells.FirstOrDefault(x => x.SheetColumnId == columnId);
        if (cell == null) row.Cells.Add(new SheetCell { SheetRowId = row.Id, SheetColumnId = columnId, Value = value });
        else cell.Value = value;
    }

    private async Task<string?> ReadColumnValueAsync(ProjectSheet sheet, SheetColumn column, IFormCollection form, string? existingValue)
    {
        var key = $"col_{column.Id}";
        if (column.ColumnType != SheetColumnType.File) return form[key].ToString().Trim();

        var file = form.Files.GetFile(key);
        if (file == null || file.Length == 0) return existingValue;
        var extension = Path.GetExtension(file.FileName);
        var safeFileName = $"{Guid.NewGuid():N}{extension}";
        var relativeFolder = Path.Combine("uploads", "sheets", sheet.UniqueId);
        var absoluteFolder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(absoluteFolder);
        var absolutePath = Path.Combine(absoluteFolder, safeFileName);
        await using var stream = System.IO.File.Create(absolutePath);
        await file.CopyToAsync(stream);
        return "/" + Path.Combine(relativeFolder, safeFileName).Replace('\\', '/');
    }

    private async Task<bool> CanAccessDepartmentAsync(int? departmentId)
    {
        if (!departmentId.HasValue) return true;
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        return allowed == null || allowed.Contains(departmentId.Value);
    }

    private IActionResult RedirectBack(int? projectId, int? departmentId)
    {
        if (projectId.HasValue) return RedirectToAction("Project", "Workspace", new { id = projectId.Value });
        return RedirectToAction(nameof(Index), new { departmentId });
    }
}
