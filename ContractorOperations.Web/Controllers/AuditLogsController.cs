using ContractorOperations.Web.Data;
using ContractorOperations.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
[RequirePermission("Audit.View")]
public class AuditLogsController : Controller
{
    private readonly ApplicationDbContext _db;public AuditLogsController(ApplicationDbContext db){_db=db;}
    public async Task<IActionResult> Index(string? q,int page=1)
    {
        page=Math.Max(1,page);const int size=100;var query=_db.AuditLogs.AsNoTracking().AsQueryable();if(!string.IsNullOrWhiteSpace(q))query=query.Where(x=>x.UserName!.Contains(q)||x.Action.Contains(q)||x.EntityName.Contains(q)||x.Details!.Contains(q));ViewBag.Query=q;ViewBag.Page=page;return View(await query.OrderByDescending(x=>x.TimestampUtc).Skip((page-1)*size).Take(size).ToListAsync());
    }
}
