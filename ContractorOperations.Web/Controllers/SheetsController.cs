using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    public SheetsController(ApplicationDbContext db, IBusinessIdService ids, IAuditService audit, IPermissionService permissions, IWebHostEnvironment environment, IJobNumberService jobNumbers)
    {
        _db = db;
        _ids = ids;
        _audit = audit;
        _permissions = permissions;
        _environment = environment;
        _jobNumbers = jobNumbers;
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
        if (string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase) && sheet.ProjectId.HasValue)
        {
            linkedSheetId = await _db.ProjectSheets.AsNoTracking()
                .Where(x => x.ProjectId == sheet.ProjectId && x.IsActive && x.Name == "Import and Export")
                .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        }

        return View(new SheetDetailsVm
        {
            Sheet = sheet, Columns = columns, Rows = rows, Search = q, LinkedImportExportSheetId = linkedSheetId
        });
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
    public async Task<IActionResult> AddColumn(int sheetId, string name, SheetColumnType columnType, bool isRequired = false, string? options = null)
    {
        var sheet = await _db.ProjectSheets.FindAsync(sheetId);
        if (sheet == null) return NotFound();
        if (!await CanAccessDepartmentAsync(sheet.DepartmentId)) return Forbid();
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Column name is required.";
            return RedirectToAction(nameof(Details), new { id = sheetId });
        }
        if (await _db.SheetColumns.AnyAsync(x => x.ProjectSheetId == sheetId && x.Name == name))
        {
            TempData["Error"] = "A column with this name already exists in the sheet.";
            return RedirectToAction(nameof(Details), new { id = sheetId });
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
        return RedirectToAction(nameof(Details), new { id = sheetId });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameColumn(int id, string name)
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
            return RedirectToAction(nameof(Details), new { id = column.ProjectSheetId });
        }
        if (await _db.SheetColumns.AnyAsync(x => x.ProjectSheetId == column.ProjectSheetId && x.Id != id && x.Name == name))
        {
            TempData["Error"] = "A column with this name already exists.";
            return RedirectToAction(nameof(Details), new { id = column.ProjectSheetId });
        }
        column.Name = name;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Column renamed.";
        return RedirectToAction(nameof(Details), new { id = column.ProjectSheetId });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveColumn(int id, int direction)
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
        return RedirectToAction(nameof(Details), new { id = column.ProjectSheetId });
    }

    [RequirePermission("Sheets.Manage")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteColumn(int id)
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
        return RedirectToAction(nameof(Details), new { id = sheetId });
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
        await _audit.WriteAsync(HttpContext, "AddRow", "SheetRow", row.Id, $"{row.UniqueId} in {sheet.UniqueId}");
        TempData["Success"] = $"Row {row.UniqueId} added.";
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
            Currencies = await _db.Currencies.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Code).ToListAsync()
        };
    }

    private static bool IsSystemManagedColumn(SheetColumn column)
        => !string.IsNullOrWhiteSpace(column.Options) &&
           (column.Options.StartsWith("__AUTO_", StringComparison.OrdinalIgnoreCase) || column.Options.StartsWith("__COMPUTED_", StringComparison.OrdinalIgnoreCase));

    private static void ApplySheetDefaults(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
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
    }

    private async Task ApplyGeneratedValuesForNewRowAsync(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
        if (string.Equals(sheet.Name, "Service Business", StringComparison.OrdinalIgnoreCase))
        {
            var jobNumber = FindColumn(columns, "Job Number");
            var department = FindColumn(columns, "Request Department");
            var departmentValue = department == null ? null : values.GetValueOrDefault(department.Id);
            if (jobNumber != null && !string.IsNullOrWhiteSpace(departmentValue))
                values[jobNumber.Id] = await _jobNumbers.NextAsync(departmentValue);
        }
        else if (string.Equals(sheet.Name, "Import and Export", StringComparison.OrdinalIgnoreCase))
        {
            var jobNumber = FindColumn(columns, "Job Number");
            var shipmentType = FindColumn(columns, "Shipment type (Import or Export)");
            var typeValue = shipmentType == null ? null : values.GetValueOrDefault(shipmentType.Id);
            if (jobNumber != null && !string.IsNullOrWhiteSpace(typeValue))
                values[jobNumber.Id] = await NextShipmentJobNumberAsync(sheet, typeValue);
        }
    }

    private async Task ApplyGeneratedValuesForExistingRowIfMissingAsync(ProjectSheet sheet, List<SheetColumn> columns, Dictionary<int, string?> values)
    {
        var generated = columns.FirstOrDefault(x => x.Options == "__AUTO_JOB_NUMBER__" || x.Options == "__AUTO_SHIPMENT_JOB_NUMBER__");
        if (generated == null || !string.IsNullOrWhiteSpace(values.GetValueOrDefault(generated.Id))) return;
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
            .Where(x => x.ProjectId == sheet.ProjectId && x.IsActive && x.Name == "Finacial")
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
