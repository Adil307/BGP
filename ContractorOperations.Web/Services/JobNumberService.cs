using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ContractorOperations.Web.Services;

public interface IJobNumberService
{
    Task<string> NextAsync(int departmentId);
    Task<string> NextAsync(string departmentCodeOrName);
}

public class JobNumberService : IJobNumberService
{
    private readonly ApplicationDbContext _db;

    public JobNumberService(ApplicationDbContext db) => _db = db;

    public async Task<string> NextAsync(int departmentId)
    {
        var department = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == departmentId);
        if (department == null) throw new InvalidOperationException("Department was not found for job-number generation.");
        return await NextForCodeAsync(department.Code);
    }

    public async Task<string> NextAsync(string departmentCodeOrName)
    {
        var input = (departmentCodeOrName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("Department is required before a job number can be generated.");

        // Dynamic sheets store the department code in the dropdown. Also accept a
        // department name for backwards compatibility with manually-entered rows.
        var department = await _db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == input || x.Name == input);

        var code = department?.Code ?? input.Split(new[] { '-' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? input;
        return await NextForCodeAsync(code);
    }

    private async Task<string> NextForCodeAsync(string rawCode)
    {
        var code = NormalizeCode(rawCode);
        var year = DateTime.Today.Year;
        var visiblePrefix = $"{code}-{year}.";
        var sequenceScope = $"J{code}";
        if (sequenceScope.Length > 20) sequenceScope = sequenceScope[..20];

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var sequence = await _db.BusinessSequences.SingleOrDefaultAsync(x => x.Prefix == sequenceScope && x.Year == year);
        if (sequence == null)
        {
            var max = 0;

            // Pick up any already-created department-wise job numbers so an upgraded
            // installation never reuses an identifier.
            var jobNumbers = await _db.Jobs.IgnoreQueryFilters()
                .Where(x => x.JobNumber.StartsWith(visiblePrefix))
                .Select(x => x.JobNumber)
                .ToListAsync();
            max = Math.Max(max, FindMax(jobNumbers, visiblePrefix));

            // Service Business uses the same department-wise number sequence.
            var sheetNumbers = await (from cell in _db.SheetCells
                                      join column in _db.SheetColumns on cell.SheetColumnId equals column.Id
                                      join sheet in _db.ProjectSheets on column.ProjectSheetId equals sheet.Id
                                      where sheet.IsActive && sheet.Name == "Service Business" && column.Name == "Job Number" && cell.Value != null && cell.Value.StartsWith(visiblePrefix)
                                      select cell.Value!).ToListAsync();
            max = Math.Max(max, FindMax(sheetNumbers, visiblePrefix));

            sequence = new BusinessSequence { Prefix = sequenceScope, Year = year, LastNumber = max + 1 };
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

    private static int FindMax(IEnumerable<string> values, string prefix)
    {
        var max = 0;
        foreach (var value in values)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = value[prefix.Length..];
            if (int.TryParse(suffix, out var number) && number > max) max = number;
        }
        return max;
    }

    private static string NormalizeCode(string value)
    {
        var clean = new string((value ?? string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(clean)) clean = "DEP";
        // Job.JobNumber is nvarchar(30); keep room for -YYYY.001.
        return clean.Length > 20 ? clean[..20] : clean;
    }
}
