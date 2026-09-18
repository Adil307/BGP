using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using ContractorOperations.Web.Services;
using ContractorOperations.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Reports.View")]
public class ReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IPermissionService _permissions;
    public ReportsController(ApplicationDbContext db, IPermissionService permissions) { _db = db; _permissions = permissions; }

    public async Task<IActionResult> Index()
    {
        var allowed = await _permissions.GetAllowedDepartmentIdsAsync(User);
        var jobs = _db.Jobs.AsNoTracking().AsQueryable();
        if (allowed != null) jobs = jobs.Where(x => allowed.Contains(x.DepartmentId));

        var amountRows = await _db.JobLines.AsNoTracking().Where(x => allowed == null || allowed.Contains(x.Job!.DepartmentId))
            .GroupBy(x => new { Department = x.Job!.Department!.Name, Currency = x.Currency!.Code })
            .Select(g => new { g.Key.Department, g.Key.Currency, Total = g.Sum(x => x.Quantity * x.UnitRate) }).ToListAsync();

        var stockItems = await _db.StockItems.AsNoTracking().Include(x => x.StockCategory).Include(x => x.Unit).Include(x => x.Currency).Include(x => x.Balances).Where(x => x.IsActive).ToListAsync();
        var low = stockItems.Select(x => new StockItemRowVm
        {
            Id=x.Id, ItemCode=x.ItemCode, Name=x.Name, Category=x.StockCategory!.Name, Unit=x.Unit!.Code, QuantityOnHand=x.Balances.Sum(b=>b.QuantityOnHand),
            ReorderLevel=x.ReorderLevel, UnitCost=x.UnitCost, Currency=x.Currency!.Code, IsActive=x.IsActive
        }).Where(x => x.QuantityOnHand <= x.ReorderLevel).OrderBy(x => x.QuantityOnHand).ToList();

        var departmentCountRows = await jobs.GroupBy(x => x.Department!.Name).Select(g => new { Department = g.Key, Count = g.Count() }).ToListAsync();
        var departmentCounts = departmentCountRows.ToDictionary(x => x.Department, x => x.Count);
        var quantityRows = await _db.JobLines.AsNoTracking().Where(x => allowed == null || allowed.Contains(x.Job!.DepartmentId))
            .GroupBy(x => x.Job!.Department!.Name).Select(g => new { Department = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToListAsync();
        var departmentQty = quantityRows.ToDictionary(x => x.Department, x => x.Quantity);
        var statusRows = await jobs.GroupBy(x => x.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
        var inventoryValues = await _db.StockBalances.AsNoTracking().GroupBy(x => x.StockItem!.Currency!.Code)
            .Select(g => new { Currency = g.Key, Total = g.Sum(x => x.QuantityOnHand * x.StockItem!.UnitCost) }).ToListAsync();
        var vm = new ReportVm
        {
            InventoryValueByCurrency = inventoryValues.ToDictionary(x => x.Currency, x => x.Total),
            DepartmentAmounts = amountRows.GroupBy(x => x.Department).Select(g => new DepartmentAmountVm
            {
                Department = g.Key,
                JobCount = departmentCounts.TryGetValue(g.Key, out var c) ? c : 0,
                Quantity = departmentQty.TryGetValue(g.Key, out var q) ? q : 0,
                Amounts = g.ToDictionary(x => x.Currency, x => x.Total)
            }).ToList(),
            JobsByStatus = statusRows.ToDictionary(x => x.Status.ToString(), x => x.Count),
            LowStockItems = low
        };
        return View(vm);
    }
}
