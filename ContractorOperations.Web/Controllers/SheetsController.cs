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

        var rows = await rowsQuery.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync();
        if (string.Equals(sheet.Name, "Contract Summary", StringComparison.OrdinalIgnoreCase))
            await ApplyContractSummaryComputedValuesAsync(sheet, columns, rows);

        int? linkedSheetId = null;
        if (sheet.ProjectId.HasValue && columns.Any(x => NormalizeName(x.Name) is "EXPORTJOBNO" or "IMPORTJOBNO"))
        {
            linkedSheetId = await _db.ProjectSheets.AsNoTracking()
                .Where(x => x.ProjectId == sheet.ProjectId && x.IsActive && x.Name == "Import and Export")
                .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        }

        var workflowNames = new[] { "Requisition", "Service Approval", "Service Business", "Purchase Order", "Service Logistics", "Invoice Reception" };
        var workflowSheets = sheet.ProjectId.HasValue
            ? await _db.ProjectSheets.AsNoTracking()
                .Where(x => x.ProjectId == sheet.ProjectId.Value && x.IsActive && workflowNames.Contains(x.Name))
                .OrderBy(x => x.SortOrder).ToListAsync()
            : new List<ProjectSheet>();

        return View(new SheetDetailsVm
        {
            Sheet = sheet, Columns = columns, Rows = rows, Search = q, LinkedImportExportSheetId = linkedSheetId, WorkflowSheets = workflowSheets
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
    public async Task<IActionResult> AddRow(int sheetId)
    {
        var vm = await BuildRowVm(sheetId, null);
        return vm == null ? NotFound() : View("RowForm", vm);
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

        var submittedValues = new Dictionary<int, string?>();
        foreach (var column in columns)
        {
            var key = $"col_{column.Id}";
            if (column.ColumnType != SheetColumnType.File)
                submittedValues[column.Id] = form[key].ToString().Trim();
        }

        ApplySheetDefaults(sheet, columns, submittedValues);
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

        await ApplyGeneratedValuesForNewRowAsync(sheet, columns, submittedValues);

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
        if (string.Equals(sheet.Name, "Service Approval", StringComparison.OrdinalIgnoreCase))
            await NotifyServiceApprovalSubmittedAsync(sheet, columns, submittedValues, row.Id);
        await _audit.WriteAsync(HttpContext, "AddRow", "SheetRow", row.Id, $"{row.UniqueId} in {sheet.UniqueId}");
        TempData["Success"] = string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase)
            ? $"Job created successfully ({row.UniqueId})."
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
        if (sheet == null || !string.Equals(sheet.Name, "Service Approval", StringComparison.OrdinalIgnoreCase)) return BadRequest();
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
            TempData["Error"] = $"This service approval has already been {currentStatus.ToLowerInvariant()}.";
            return RedirectToAction(nameof(Details), new { id = sheet.Id });
        }
        SetCellValue(row, FindColumn(columns, "Approval Status"), approve ? "Approved" : "Rejected");
        SetCellValue(row, FindColumn(columns, "Manager Comment"), comment?.Trim());
        SetCellValue(row, FindColumn(columns, "Approved By"), User.Identity?.Name);
        SetCellValue(row, FindColumn(columns, "Decision Date"), DateTime.Today.ToString("yyyy-MM-dd"));
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var requester = GetCellValue(row, FindColumn(columns, "Requested By"));
        var requisition = GetCellValue(row, FindColumn(columns, "Requisition No"));
        if (!string.IsNullOrWhiteSpace(requester))
        {
            var user = await _userManager.FindByEmailAsync(requester) ?? await _userManager.FindByNameAsync(requester);
            if (user != null)
                await CreateNotificationAsync(user.Id, approve ? "Service approval approved" : "Service approval rejected",
                    $"{requisition ?? "Service request"} was {(approve ? "approved" : "rejected")} by the project manager ({User.Identity?.Name}).",
                    Url.Action(nameof(Details), "Sheets", new { id = sheet.Id, q = requisition }) ?? $"/Sheets/Details/{sheet.Id}");
        }
        await _audit.WriteAsync(HttpContext, approve ? "Approve" : "Reject", "ServiceApproval", row.Id, requisition);
        TempData["Success"] = approve ? "Service request approved by the project manager. The requester was notified." : "Service request rejected by the project manager. The requester was notified.";
        return RedirectToAction(nameof(Details), new { id = sheet.Id });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRow(long id)
    {
        var row = await _db.SheetRows.FindAsync(id);
        if (row == null) return NotFound();
        var sheetId = row.ProjectSheetId;
        var sheet = await _db.ProjectSheets.FindAsync(sheetId);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        _db.SheetRows.Remove(row);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "DeleteRow", "SheetRow", id, row.UniqueId);
        TempData["Success"] = "Row deleted.";
        return RedirectToAction(nameof(Details), new { id = sheetId });
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
        return new SheetRowFormVm
        {
            Sheet = sheet,
            Row = row,
            Columns = columns,
            Values = values,
            Departments = await _db.Departments.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(),
            Projects = await _db.Projects.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(),
            Currencies = await _db.Currencies.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Code).ToListAsync(),
            ReferenceOptions = await BuildReferenceOptionsAsync(sheet),
            ApprovalManagers = string.Equals(sheet.Name, "Service Approval", StringComparison.OrdinalIgnoreCase) ? await GetApprovalManagersAsync() : new List<ApplicationUser>()
        };
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

        if (string.Equals(sheet.Name, "Requisition", StringComparison.OrdinalIgnoreCase)) SetDefault("Status", "Submitted");
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
        foreach (var column in columns.Where(IsSystemManagedColumn))
        {
            if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault(column.Id))) continue;
            values[column.Id] = column.Options switch
            {
                "__AUTO_REQUISITION_NUMBER__" => await _ids.NextAsync("REQ"),
                "__AUTO_SERVICE_APPROVAL_NUMBER__" => await _ids.NextAsync("SAP"),
                "__AUTO_PO_NUMBER__" => await _ids.NextAsync("PO"),
                "__AUTO_CURRENT_USER_EMAIL__" => User.Identity?.Name ?? string.Empty,
                "__AUTO_TODAY__" => DateTime.Today.ToString("yyyy-MM-dd"),
                "__AUTO_APPROVAL_STATUS__" => "Pending",
                _ => values.GetValueOrDefault(column.Id)
            };
        }

        if (string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase))
        {
            var jobNumber = FindColumn(columns, "Job Number");
            var department = FindColumn(columns, "Request Department");
            var departmentValue = department == null ? null : values.GetValueOrDefault(department.Id);
            if (jobNumber != null && string.IsNullOrWhiteSpace(values.GetValueOrDefault(jobNumber.Id)) && !string.IsNullOrWhiteSpace(departmentValue))
                values[jobNumber.Id] = await _jobNumbers.NextAsync(departmentValue);
        }
        else if (string.Equals(sheet.Name, "Import and Export", StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(sheet.Name, "Service Approval", StringComparison.OrdinalIgnoreCase))
        {
            var req = Value("Requisition No");
            if (!string.IsNullOrWhiteSpace(req) && !await SheetReferenceExistsAsync(sheet.ProjectId.Value, "Requisition", "Requisition No", req))
                ModelState.AddModelError(string.Empty, $"Requisition {req} was not found in the Requisition sheet.");

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
                ModelState.AddModelError(string.Empty, "A valid Requisition No. is required before a job can be created.");
            if (string.IsNullOrWhiteSpace(approval))
                ModelState.AddModelError(string.Empty, "An approved Service Approval No is required before a job can be created.");
            else
            {
                var status = await SheetReferencedValueAsync(sheet.ProjectId.Value, "Service Approval", "Service Approval No", approval, "Approval Status");
                var approvedRequisition = await SheetReferencedValueAsync(sheet.ProjectId.Value, "Service Approval", "Service Approval No", approval, "Requisition No");
                if (!string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase))
                    ModelState.AddModelError(string.Empty, $"Service Approval {approval} must be Approved before job creation.");
                else if (!string.Equals(approvedRequisition?.Trim(), req?.Trim(), StringComparison.OrdinalIgnoreCase))
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

        result["__REQUISITIONS__"] = await GetSheetColumnValuesAsync(sheet.ProjectId.Value, "Requisition", "Requisition No");
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

    private async Task<List<string>> GetApprovedServiceApprovalNumbersAsync(int projectId)
    {
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IsActive && x.Name == "Service Approval");
        if (sheet == null) return new List<string>();
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        var numberColumn = FindColumn(columns, "Service Approval No");
        var statusColumn = FindColumn(columns, "Approval Status");
        if (numberColumn == null || statusColumn == null) return new List<string>();

        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells).Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        return rows
            .Where(x => string.Equals(GetCellValue(x, statusColumn), "Approved", StringComparison.OrdinalIgnoreCase))
            .Select(x => GetCellValue(x, numberColumn))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x)
            .ToList();
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
        var approvalNo = GetValue(values, FindColumn(columns, "Service Approval No")) ?? "Service approval";
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
            await CreateNotificationAsync(user.Id, "Service approval decision required", $"Review {approvalNo} linked to requisition {req ?? "N/A"}, submitted by {requester ?? "a user"}.", url);

        var currentId = _userManager.GetUserId(User);
        if (!string.IsNullOrWhiteSpace(currentId))
            await CreateNotificationAsync(currentId, "Service request sent for PM approval", $"{approvalNo} was submitted to the project manager for review. You will be notified after the decision.", url);
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
