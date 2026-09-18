using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using Microsoft.AspNetCore.Identity;

namespace ContractorOperations.Web.Services;

public interface IAuditService
{
    Task WriteAsync(HttpContext http, string action, string entityName, object? entityId = null, string? details = null);
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    public AuditService(ApplicationDbContext db, UserManager<ApplicationUser> userManager) { _db = db; _userManager = userManager; }

    public async Task WriteAsync(HttpContext http, string action, string entityName, object? entityId = null, string? details = null)
    {
        var userId = _userManager.GetUserId(http.User);
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = http.User.Identity?.Name,
            Action = action,
            EntityName = entityName,
            EntityId = entityId?.ToString(),
            Details = details,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            TimestampUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }
}
