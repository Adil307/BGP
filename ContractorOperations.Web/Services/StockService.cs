using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ContractorOperations.Web.Services;

public interface IStockService
{
    Task<(bool Ok, string Message)> PostAsync(int itemId, int warehouseId, StockTransactionType type, decimal quantity,
        decimal unitCost, int currencyId, string userId, int? jobId, int? contractorId, string? referenceNo, string? notes);
    Task<(bool Ok, string Message)> TransferAsync(int itemId, int fromWarehouseId, int toWarehouseId, decimal quantity, string userId, string? referenceNo, string? notes);
}

public class StockService : IStockService
{
    private readonly ApplicationDbContext _db;
    public StockService(ApplicationDbContext db) { _db = db; }

    public async Task<(bool Ok, string Message)> PostAsync(int itemId, int warehouseId, StockTransactionType type, decimal quantity,
        decimal unitCost, int currencyId, string userId, int? jobId, int? contractorId, string? referenceNo, string? notes)
    {
        if (quantity <= 0) return (false, "Quantity must be greater than zero.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (!string.IsNullOrWhiteSpace(referenceNo))
        {
            var normalizedRef = referenceNo.Trim();
            var duplicate = await _db.StockTransactions.AnyAsync(x => x.StockItemId == itemId && x.WarehouseId == warehouseId && x.Type == type && x.ReferenceNo == normalizedRef);
            if (duplicate) return (false, "A stock transaction with the same item, warehouse, type and reference already exists.");
        }
        var balance = await _db.StockBalances.SingleOrDefaultAsync(x => x.StockItemId == itemId && x.WarehouseId == warehouseId);
        if (balance == null)
        {
            balance = new StockBalance { StockItemId = itemId, WarehouseId = warehouseId, QuantityOnHand = 0 };
            _db.StockBalances.Add(balance);
        }

        var isIn = type is StockTransactionType.Receive or StockTransactionType.AdjustmentIn;
        if (!isIn && balance.QuantityOnHand < quantity)
            return (false, $"Insufficient stock. Available quantity is {balance.QuantityOnHand:N3}.");

        balance.QuantityOnHand += isIn ? quantity : -quantity;
        balance.UpdatedAt = DateTime.UtcNow;
        _db.StockTransactions.Add(new StockTransaction
        {
            StockItemId = itemId, WarehouseId = warehouseId, Type = type, Quantity = quantity,
            UnitCost = unitCost, CurrencyId = currencyId, CreatedByUserId = userId, JobId = jobId,
            ContractorId = contractorId, ReferenceNo = referenceNo, Notes = notes, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (true, "Stock transaction posted successfully.");
    }

    public async Task<(bool Ok, string Message)> TransferAsync(int itemId, int fromWarehouseId, int toWarehouseId, decimal quantity, string userId, string? referenceNo, string? notes)
    {
        if (fromWarehouseId == toWarehouseId) return (false, "Source and destination warehouse must be different.");
        if (quantity <= 0) return (false, "Quantity must be greater than zero.");
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var item = await _db.StockItems.FirstOrDefaultAsync(x => x.Id == itemId && x.IsActive);
        if (item == null) return (false, "Stock item not found.");
        var from = await _db.StockBalances.SingleOrDefaultAsync(x => x.StockItemId == itemId && x.WarehouseId == fromWarehouseId);
        if (from == null || from.QuantityOnHand < quantity) return (false, $"Insufficient source stock. Available quantity is {(from?.QuantityOnHand ?? 0):N3}.");
        var to = await _db.StockBalances.SingleOrDefaultAsync(x => x.StockItemId == itemId && x.WarehouseId == toWarehouseId);
        if (to == null) { to = new StockBalance { StockItemId=itemId, WarehouseId=toWarehouseId }; _db.StockBalances.Add(to); }
        from.QuantityOnHand -= quantity; from.UpdatedAt = DateTime.UtcNow; to.QuantityOnHand += quantity; to.UpdatedAt = DateTime.UtcNow;
        var reference = string.IsNullOrWhiteSpace(referenceNo) ? $"TRF-{DateTime.UtcNow:yyyyMMddHHmmss}" : referenceNo.Trim();
        _db.StockTransactions.AddRange(
            new StockTransaction { StockItemId=itemId, WarehouseId=fromWarehouseId, Type=StockTransactionType.Issue, Quantity=quantity, UnitCost=item.UnitCost, CurrencyId=item.CurrencyId, CreatedByUserId=userId, ReferenceNo=reference, Notes=$"Warehouse transfer OUT. {notes}".Trim(), CreatedAt=DateTime.UtcNow },
            new StockTransaction { StockItemId=itemId, WarehouseId=toWarehouseId, Type=StockTransactionType.Receive, Quantity=quantity, UnitCost=item.UnitCost, CurrencyId=item.CurrencyId, CreatedByUserId=userId, ReferenceNo=reference, Notes=$"Warehouse transfer IN. {notes}".Trim(), CreatedAt=DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(); await tx.CommitAsync(); return (true, "Stock transferred successfully.");
    }
}
