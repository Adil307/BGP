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
[RequirePermission("Dashboard.View")]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly UserManager<ApplicationUser> _userManager;

    public DashboardController(ApplicationDbContext db, IPermissionService permissions, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _permissions = permissions;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var jobs = _db.Jobs.AsNoTracking().AsQueryable();
        if (allowed != null)
            jobs = jobs.Where(x => allowed.Contains(x.DepartmentId));

        var vm = new DashboardVm
        {
            UserName = (await _userManager.GetUserAsync(User))?.FullName ?? User.Identity?.Name ?? "User",
            TotalJobs = await jobs.CountAsync(),
            CompletedJobs = await jobs.CountAsync(x => x.Status == JobStatus.Completed),
            InProgressJobs = await jobs.CountAsync(x => x.Status == JobStatus.InProgress),
            OnHoldCancelledJobs = await jobs.CountAsync(x => x.Status == JobStatus.OnHold || x.Status == JobStatus.Cancelled),
            DepartmentJobs = new Dictionary<string, int>(),
            StatusJobs = new Dictionary<string, int>(),
            MonthlyJobs = new Dictionary<string, int>(),
            RecentJobs = await jobs
                .Include(x => x.Project)
                .Include(x => x.Department)
                .Include(x => x.Contractor)
                .Include(x => x.Lines).ThenInclude(x => x.Currency)
                .OrderByDescending(x => x.CreatedAt)
                .Take(6)
                .ToListAsync(),
            ProjectCount = await _db.ProjectWorkspaces.CountAsync(x => x.Project!.IsActive),
            DepartmentCount = await _db.Departments.CountAsync(x => x.IsActive),
            ContractorCount = await _db.Contractors.CountAsync(x => x.IsActive),
            CurrencyCount = await _db.Currencies.CountAsync(x => x.IsActive),
            TotalStockItems = await _db.StockItems.CountAsync(x => x.IsActive),
            WarehouseCount = await _db.Warehouses.CountAsync(x => x.IsActive),
            SheetCount = await _db.ProjectSheets.CountAsync(x => x.IsActive),
            TotalSheetRows = await _db.SheetRows.CountAsync(),
            ActiveUserCount = await _db.Users.CountAsync(x => x.IsActive),
            InactiveUserCount = await _db.Users.CountAsync(x => !x.IsActive),
            ActiveCountryCount = await _db.Countries.CountAsync(x => x.IsActive)
        };

        vm.OpenWorkCount = await jobs.CountAsync(x => x.Status == JobStatus.Draft || x.Status == JobStatus.InProgress || x.Status == JobStatus.OnHold);
        var completionBase = vm.CompletedJobs + vm.OpenWorkCount;
        vm.WorkCompletionPercent = completionBase == 0 ? 0 : (int)Math.Round(vm.CompletedJobs * 100d / completionBase);

        vm.ActiveProjectCount = await _db.ProjectWorkspaces.CountAsync(x => x.Status == "Active");
        vm.CompletedProjectCount = await _db.ProjectWorkspaces.CountAsync(x => x.Status == "Completed");
        vm.PendingProjectCount = await _db.ProjectWorkspaces.CountAsync(x => x.Status == "Pending" || x.Status == "On Hold");
        var projectProgress = await _db.ProjectWorkspaces.AsNoTracking().Where(x => x.Project!.IsActive).Select(x => x.ProgressPercent).ToListAsync();
        vm.AverageProjectProgress = projectProgress.Count == 0 ? 0 : (int)Math.Round(projectProgress.Average());
        var projectStatuses = await _db.ProjectWorkspaces.AsNoTracking().Where(x => x.Project!.IsActive)
            .GroupBy(x => x.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
        vm.ProjectStatusCounts = projectStatuses.ToDictionary(x => string.IsNullOrWhiteSpace(x.Status) ? "Unknown" : x.Status, x => x.Count);

        var roleCounts = await (from userRole in _db.UserRoles.AsNoTracking()
                                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                                join user in _db.Users.AsNoTracking() on userRole.UserId equals user.Id
                                where user.IsActive
                                group userRole by role.Name into g
                                select new { Role = g.Key ?? "Role", Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Role).ToListAsync();
        vm.RoleUserCounts = roleCounts.ToDictionary(x => x.Role, x => x.Count);

        var requisitionCount = await CountSheetRowsAsync("Requisition");
        var approvalCount = await CountSheetRowsAsync("Service Approval");
        var serviceJobCount = await CountSheetRowsAsync("Service Business");
        var poCount = await CountSheetRowsAsync("Purchase Order");
        var logisticsCount = await CountSheetRowsAsync("Service Logistics");
        var invoiceCount = await CountSheetRowsAsync("Invoice Reception");
        vm.WorkflowStageCounts = new Dictionary<string, int>
        {
            ["Requisitions"] = requisitionCount,
            ["Service Approvals"] = approvalCount,
            ["Jobs Created"] = serviceJobCount,
            ["Purchase Orders"] = poCount,
            ["Delivery / Return"] = logisticsCount,
            ["Invoices"] = invoiceCount
        };

        var approvalStatuses = await CountSheetStatusAsync("Service Approval", "Approval Status");
        vm.PendingServiceApprovals = approvalStatuses.GetValueOrDefault("Pending");
        vm.ApprovedServiceApprovals = approvalStatuses.GetValueOrDefault("Approved");
        vm.RejectedServiceApprovals = approvalStatuses.GetValueOrDefault("Rejected");
        var invoiceStatuses = await CountSheetStatusAsync("Invoice Reception", "Payment Status");
        vm.PaidInvoices = invoiceStatuses.GetValueOrDefault("Paid");
        vm.PendingInvoices = Math.Max(0, invoiceCount - vm.PaidInvoices);

        var inventoryRequests = _db.InventoryRequests.AsNoTracking().AsQueryable();
        if (allowed != null) inventoryRequests = inventoryRequests.Where(x => allowed.Contains(x.DepartmentId));
        vm.TotalInventoryRequests = await inventoryRequests.CountAsync();
        vm.PendingInventoryRequests = await inventoryRequests.CountAsync(x => x.Status == InventoryRequestStatus.Pending);
        vm.AcceptedInventoryRequests = await inventoryRequests.CountAsync(x => x.Status == InventoryRequestStatus.Accepted);
        vm.RejectedInventoryRequests = await inventoryRequests.CountAsync(x => x.Status == InventoryRequestStatus.Rejected);

        var departmentCounts = await jobs
            .GroupBy(x => x.Department!.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderBy(x => x.Name)
            .ToListAsync();
        vm.DepartmentJobs = departmentCounts.ToDictionary(x => x.Name, x => x.Count);

        var statusCounts = await jobs
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        vm.StatusJobs = statusCounts.ToDictionary(x => x.Status.ToString(), x => x.Count);

        // Rolling six-month activity trend. Empty months are kept at zero so the
        // chart stays easy to understand even when no jobs were logged in a month.
        var firstMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-5);
        var monthlyRows = await jobs
            .Where(x => x.JobDate >= firstMonth)
            .GroupBy(x => new { x.JobDate.Year, x.JobDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();
        for (var i = 0; i < 6; i++)
        {
            var month = firstMonth.AddMonths(i);
            var count = monthlyRows.FirstOrDefault(x => x.Year == month.Year && x.Month == month.Month)?.Count ?? 0;
            vm.MonthlyJobs[month.ToString("MMM yyyy")] = count;
        }

        var amounts = await _db.JobLines.AsNoTracking()
            .Where(x => allowed == null || allowed.Contains(x.Job!.DepartmentId))
            .GroupBy(x => new { Department = x.Job!.Department!.Name, Currency = x.Currency!.Code })
            .Select(g => new { g.Key.Department, g.Key.Currency, Total = g.Sum(x => x.Quantity * x.UnitRate) })
            .ToListAsync();

        var quantityRows = await _db.JobLines.AsNoTracking()
            .Where(x => allowed == null || allowed.Contains(x.Job!.DepartmentId))
            .GroupBy(x => x.Job!.Department!.Name)
            .Select(g => new { Department = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToListAsync();
        var quantityByDepartment = quantityRows.ToDictionary(x => x.Department, x => x.Quantity);

        vm.DepartmentAmounts = amounts
            .GroupBy(x => x.Department)
            .Select(g => new DepartmentAmountVm
            {
                Department = g.Key,
                JobCount = vm.DepartmentJobs.TryGetValue(g.Key, out var c) ? c : 0,
                Quantity = quantityByDepartment.TryGetValue(g.Key, out var q) ? q : 0,
                Amounts = g.ToDictionary(x => x.Currency, x => x.Total)
            })
            .OrderBy(x => x.Department)
            .ToList();

        var stockTotals = await _db.StockItems.AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.ReorderLevel,
                Qty = x.Balances.Sum(b => (decimal?)b.QuantityOnHand) ?? 0m
            })
            .ToListAsync();
        vm.LowStockItems = stockTotals.Count(x => x.Qty > 0 && x.Qty <= x.ReorderLevel);
        vm.OutOfStockItems = stockTotals.Count(x => x.Qty <= 0);
        vm.HealthyStockItems = stockTotals.Count(x => x.Qty > x.ReorderLevel);

        var inventoryValues = await _db.StockBalances.AsNoTracking()
            .GroupBy(x => x.StockItem!.Currency!.Code)
            .Select(g => new
            {
                Currency = g.Key,
                Total = g.Sum(x => x.QuantityOnHand * x.StockItem!.UnitCost)
            })
            .ToListAsync();
        vm.InventoryValueByCurrency = inventoryValues.ToDictionary(x => x.Currency, x => x.Total);

        if (await _permissions.HasPermissionAsync(User, "Permissions.Manage"))
        {
            var roles = await _db.Roles.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            foreach (var role in roles)
            {
                var permissionCount = await _db.RolePermissions.CountAsync(x => x.RoleId == role.Id);
                var userCount = await _db.UserRoles.CountAsync(x => x.RoleId == role.Id);
                var summary = role.Name switch
                {
                    "Administrator" => "Full system access",
                    "Department Head" => "Manage permitted department operations",
                    "Department User" => "Create/view permitted department jobs",
                    "Read Only" => "View permitted data and reports",
                    _ => $"{permissionCount} configured permissions"
                };
                vm.RoleSummaries.Add(new RoleSummaryVm
                {
                    RoleName = role.Name ?? "Role",
                    Users = userCount,
                    PermissionCount = permissionCount,
                    AccessSummary = summary
                });
            }
        }

        return View(vm);
    }

    private async Task<int> CountSheetRowsAsync(string sheetName)
    {
        var sheetIds = await _db.ProjectSheets.AsNoTracking()
            .Where(x => x.IsActive && x.Name == sheetName)
            .Select(x => x.Id).ToListAsync();
        return sheetIds.Count == 0 ? 0 : await _db.SheetRows.AsNoTracking().CountAsync(x => sheetIds.Contains(x.ProjectSheetId));
    }

    private async Task<Dictionary<string, int>> CountSheetStatusAsync(string sheetName, string columnName)
    {
        var sheetIds = await _db.ProjectSheets.AsNoTracking()
            .Where(x => x.IsActive && x.Name == sheetName)
            .Select(x => x.Id).ToListAsync();
        if (sheetIds.Count == 0) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var columnIds = await _db.SheetColumns.AsNoTracking()
            .Where(x => sheetIds.Contains(x.ProjectSheetId) && x.Name == columnName)
            .Select(x => x.Id).ToListAsync();
        if (columnIds.Count == 0) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var values = await _db.SheetCells.AsNoTracking()
            .Where(x => columnIds.Contains(x.SheetColumnId) && x.Value != null && x.Value != "")
            .Select(x => x.Value!)
            .ToListAsync();
        return values
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }
}
