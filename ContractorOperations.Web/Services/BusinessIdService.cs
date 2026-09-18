using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ContractorOperations.Web.Services;

public interface IBusinessIdService
{
    Task<string> NextAsync(string prefix, int digits = 4);
}

public class BusinessIdService : IBusinessIdService
{
    private readonly ApplicationDbContext _db;
    public BusinessIdService(ApplicationDbContext db) => _db = db;

    public async Task<string> NextAsync(string prefix, int digits = 4)
    {
        prefix = (prefix ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Prefix is required.", nameof(prefix));

        var year = DateTime.Today.Year;
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var sequence = await _db.BusinessSequences.SingleOrDefaultAsync(x => x.Prefix == prefix && x.Year == year);
        if (sequence == null)
        {
            sequence = new BusinessSequence { Prefix = prefix, Year = year, LastNumber = 1 };
            _db.BusinessSequences.Add(sequence);
        }
        else
        {
            sequence.LastNumber++;
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return $"{prefix}-{year}-{sequence.LastNumber.ToString(new string('0', digits))}";
    }
}
