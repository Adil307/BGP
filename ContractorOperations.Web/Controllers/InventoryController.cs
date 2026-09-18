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
using System.Text;

namespace ContractorOperations.Web.Controllers;

[Authorize]
public class InventoryController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IAuditService _audit;
    private readonly UserManager<ApplicationUser> _userManager;
    public InventoryController(ApplicationDbContext db, IStockService stock, IAuditService audit, UserManager<ApplicationUser> userManager)
    { _db = db; _stock = stock; _audit = audit; _userManager = userManager; }

    [RequirePermission("Inventory.View")]
    public async Task<IActionResult> Index(string? q)
    {
        var query = _db.StockItems.AsNoTracking().Include(x => x.StockCategory).Include(x => x.Unit).Include(x => x.Currency).Include(x => x.Balances).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.ItemCode.Contains(q) || x.Name.Contains(q) || x.StockCategory!.Name.Contains(q));
        var items = await query.OrderBy(x => x.Name).ToListAsync();
        var rows = items.Select(x => new StockItemRowVm
        {
            Id = x.Id, ItemCode = x.ItemCode, Name = x.Name, Category = x.StockCategory?.Name ?? "", Unit = x.Unit?.Code ?? "",
            QuantityOnHand = x.Balances.Sum(b => b.QuantityOnHand), ReorderLevel = x.ReorderLevel, UnitCost = x.UnitCost,
            Currency = x.Currency?.Code ?? "", IsActive = x.IsActive
        }).ToList();
        return View(new InventoryDashboardVm
        {
            Items = rows, TotalItems = rows.Count(x => x.IsActive), LowStock = rows.Count(x => x.QuantityOnHand > 0 && x.QuantityOnHand <= x.ReorderLevel),
            OutOfStock = rows.Count(x => x.QuantityOnHand <= 0), Warehouses = await _db.Warehouses.CountAsync(x => x.IsActive)
        });
    }


    [RequirePermission("Inventory.View")]
    public async Task<IActionResult> Details(int id)
    {
        var item = await _db.StockItems.AsNoTracking().Include(x => x.StockCategory).Include(x => x.Unit).Include(x => x.Currency)
            .Include(x => x.Balances).ThenInclude(x => x.Warehouse).FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        ViewBag.Transactions = await _db.StockTransactions.AsNoTracking().Include(x => x.Warehouse).Include(x => x.CreatedByUser)
            .Where(x => x.StockItemId == id).OrderByDescending(x => x.CreatedAt).Take(25).ToListAsync();
        return View(item);
    }

    [RequirePermission("Inventory.Transfer")]
    [HttpGet]
    public async Task<IActionResult> Transfer()
    {
        var vm = new StockTransferVm { Quantity = 1 };
        await FillTransferLists(vm);
        return View(vm);
    }

    [RequirePermission("Inventory.Transfer")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(StockTransferVm vm)
    {
        if (!ModelState.IsValid) { await FillTransferLists(vm); return View(vm); }
        var result = await _stock.TransferAsync(vm.StockItemId, vm.FromWarehouseId, vm.ToWarehouseId, vm.Quantity, _userManager.GetUserId(User)!, vm.ReferenceNo, vm.Notes);
        if (!result.Ok) { ModelState.AddModelError(string.Empty, result.Message); await FillTransferLists(vm); return View(vm); }
        await _audit.WriteAsync(HttpContext, "Transfer", "Stock", vm.StockItemId, $"Qty {vm.Quantity}; {vm.FromWarehouseId} -> {vm.ToWarehouseId}");
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Transactions));
    }

    [RequirePermission("Inventory.CreateItem")]
    [HttpGet]
    public async Task<IActionResult> CreateItem()
    {
        var vm = new StockItemFormVm { ItemCode = await NextItemCodeAsync() };
        await FillItemLists(vm);
        return View("ItemForm", vm);
    }

    [RequirePermission("Inventory.CreateItem")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateItem(StockItemFormVm vm)
    {
        if (await _db.StockItems.AnyAsync(x => x.ItemCode == vm.ItemCode.Trim())) ModelState.AddModelError(nameof(vm.ItemCode), "Item code already exists.");
        if (!ModelState.IsValid) { await FillItemLists(vm); return View("ItemForm", vm); }
        var item = new StockItem
        {
            ItemCode = vm.ItemCode.Trim().ToUpperInvariant(), Name = vm.Name.Trim(), StockCategoryId = vm.StockCategoryId,
            UnitId = vm.UnitId, ReorderLevel = vm.ReorderLevel, UnitCost = vm.UnitCost, CurrencyId = vm.CurrencyId, IsActive = vm.IsActive
        };
        _db.StockItems.Add(item); await _db.SaveChangesAsync();
        await _audit.WriteAsync(HttpContext, "Create", "StockItem", item.Id, item.ItemCode);
        TempData["Success"] = "Stock item created.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Inventory.EditItem")]
    [HttpGet]
    public async Task<IActionResult> EditItem(int id)
    {
        var x = await _db.StockItems.FindAsync(id); if (x == null) return NotFound();
        var vm = new StockItemFormVm { Id = x.Id, ItemCode = x.ItemCode, Name = x.Name, StockCategoryId = x.StockCategoryId, UnitId = x.UnitId, ReorderLevel = x.ReorderLevel, UnitCost = x.UnitCost, CurrencyId = x.CurrencyId, IsActive = x.IsActive };
        await FillItemLists(vm); return View("ItemForm", vm);
    }

    [RequirePermission("Inventory.EditItem")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditItem(int id, StockItemFormVm vm)
    {
        if (id != vm.Id) return BadRequest();
        var x = await _db.StockItems.FindAsync(id); if (x == null) return NotFound();
        if (await _db.StockItems.AnyAsync(i => i.Id != id && i.ItemCode == vm.ItemCode.Trim())) ModelState.AddModelError(nameof(vm.ItemCode), "Item code already exists.");
        if (!ModelState.IsValid) { await FillItemLists(vm); return View("ItemForm", vm); }
        x.ItemCode = vm.ItemCode.Trim().ToUpperInvariant(); x.Name = vm.Name.Trim(); x.StockCategoryId = vm.StockCategoryId; x.UnitId = vm.UnitId;
        x.ReorderLevel = vm.ReorderLevel; x.UnitCost = vm.UnitCost; x.CurrencyId = vm.CurrencyId; x.IsActive = vm.IsActive;
        await _db.SaveChangesAsync(); await _audit.WriteAsync(HttpContext, "Edit", "StockItem", x.Id, x.ItemCode);
        TempData["Success"] = "Stock item updated."; return RedirectToAction(nameof(Index));
    }

    [RequirePermission("Inventory.View")]
    public async Task<IActionResult> Transactions(int? itemId, int? warehouseId)
    {
        var q = _db.StockTransactions.AsNoTracking().Include(x => x.StockItem).Include(x => x.Warehouse).Include(x => x.Currency).Include(x => x.Job).Include(x => x.CreatedByUser).AsQueryable();
        if (itemId.HasValue) q = q.Where(x => x.StockItemId == itemId.Value);
        if (warehouseId.HasValue) q = q.Where(x => x.WarehouseId == warehouseId.Value);
        ViewBag.Items = new SelectList(await _db.StockItems.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(), "Id", "Name", itemId);
        ViewBag.Warehouses = new SelectList(await _db.Warehouses.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(), "Id", "Name", warehouseId);
        return View(await q.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync());
    }

    [RequirePermission("Inventory.Receive")]
    [HttpGet] public async Task<IActionResult> Receive() => View("TransactionForm", await NewTransactionVm(StockTransactionType.Receive));
    [RequirePermission("Inventory.Issue")]
    [HttpGet] public async Task<IActionResult> Issue() => View("TransactionForm", await NewTransactionVm(StockTransactionType.Issue));
    [RequirePermission("Inventory.Adjust")]
    [HttpGet] public async Task<IActionResult> Adjust(bool increase = true) => View("TransactionForm", await NewTransactionVm(increase ? StockTransactionType.AdjustmentIn : StockTransactionType.AdjustmentOut));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostTransaction(StockTransactionFormVm vm)
    {
        var key = vm.Type switch
        {
            StockTransactionType.Receive => "Inventory.Receive",
            StockTransactionType.Issue => "Inventory.Issue",
            _ => "Inventory.Adjust"
        };
        var permissionService = HttpContext.RequestServices.GetRequiredService<IPermissionService>();
        if (!await permissionService.HasPermissionAsync(User, key)) return Forbid();
        if (!ModelState.IsValid) { await FillTransactionLists(vm); return View("TransactionForm", vm); }
        var selectedItem = await _db.StockItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vm.StockItemId && x.IsActive);
        if (selectedItem == null) { ModelState.AddModelError(nameof(vm.StockItemId), "Stock item not found or inactive."); await FillTransactionLists(vm); return View("TransactionForm", vm); }
        var effectiveCost = vm.UnitCost > 0 ? vm.UnitCost : selectedItem.UnitCost;
        var effectiveCurrency = vm.CurrencyId > 0 ? vm.CurrencyId : selectedItem.CurrencyId;
        var result = await _stock.PostAsync(vm.StockItemId, vm.WarehouseId, vm.Type, vm.Quantity, effectiveCost, effectiveCurrency,
            _userManager.GetUserId(User)!, vm.JobId, vm.ContractorId, vm.ReferenceNo?.Trim(), vm.Notes?.Trim());
        if (!result.Ok) { ModelState.AddModelError(string.Empty, result.Message); await FillTransactionLists(vm); return View("TransactionForm", vm); }
        await _audit.WriteAsync(HttpContext, vm.Type.ToString(), "StockTransaction", null, $"Item {vm.StockItemId}; Qty {vm.Quantity}");
        TempData["Success"] = result.Message; return RedirectToAction(nameof(Transactions));
    }

    [RequirePermission("Inventory.Export")]
    public async Task<FileResult> ExportCsv()
    {
        var items = await _db.StockItems.AsNoTracking().Include(x => x.StockCategory).Include(x => x.Unit).Include(x => x.Currency).Include(x => x.Balances).OrderBy(x => x.Name).ToListAsync();
        var sb = new StringBuilder("Item Code,Item Name,Category,Unit,Quantity On Hand,Reorder Level,Unit Cost,Currency,Active\n");
        foreach (var x in items)
            sb.AppendLine(string.Join(',', Csv(x.ItemCode), Csv(x.Name), Csv(x.StockCategory?.Name), Csv(x.Unit?.Code), x.Balances.Sum(b => b.QuantityOnHand), x.ReorderLevel, x.UnitCost, Csv(x.Currency?.Code), x.IsActive));
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"inventory-{DateTime.Today:yyyyMMdd}.csv");
    }

    private async Task<string> NextItemCodeAsync()
    {
        var last = await _db.StockItems.OrderByDescending(x => x.Id).Select(x => x.Id).FirstOrDefaultAsync();
        return $"STK-{last + 1:0000}";
    }
    private async Task FillItemLists(StockItemFormVm vm)
    {
        vm.Categories = await _db.StockCategories.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
        vm.Units = await _db.Units.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem($"{x.Code} - {x.Name}", x.Id.ToString())).ToListAsync();
        vm.Currencies = await _db.Currencies.Where(x => x.IsActive).OrderBy(x => x.Code).Select(x => new SelectListItem(x.Code, x.Id.ToString())).ToListAsync();
    }
    private async Task<StockTransactionFormVm> NewTransactionVm(StockTransactionType type)
    {
        var vm = new StockTransactionFormVm { Type = type, Quantity = 1 };
        var qar = await _db.Currencies.FirstOrDefaultAsync(x => x.Code == "QAR"); if (qar != null) vm.CurrencyId = qar.Id;
        await FillTransactionLists(vm); return vm;
    }
    private async Task FillTransactionLists(StockTransactionFormVm vm)
    {
        vm.Items = await _db.StockItems.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem($"{x.ItemCode} - {x.Name}", x.Id.ToString())).ToListAsync();
        vm.Warehouses = await _db.Warehouses.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
        vm.Currencies = await _db.Currencies.Where(x => x.IsActive).OrderBy(x => x.Code).Select(x => new SelectListItem(x.Code, x.Id.ToString())).ToListAsync();
        vm.Jobs = await _db.Jobs.OrderByDescending(x => x.CreatedAt).Take(300).Select(x => new SelectListItem($"{x.JobNumber} - {x.Description}", x.Id.ToString())).ToListAsync();
        vm.Contractors = await _db.Contractors.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
    }
    private async Task FillTransferLists(StockTransferVm vm)
    {
        vm.Items = await _db.StockItems.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem($"{x.ItemCode} - {x.Name}", x.Id.ToString())).ToListAsync();
        vm.Warehouses = await _db.Warehouses.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync();
    }
    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
